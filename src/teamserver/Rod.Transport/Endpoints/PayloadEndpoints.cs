using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Rod.Audit;
using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing payload-build endpoints: an operator
/// requests a payload for an engagement, the build pipeline invokes the
/// language's build unit, the bytes are stored in the payload store, and the
/// fingerprinted artifact is recorded into the audit trail. The build response's
/// Location resolves to a download route that returns the compiled bytes, so a
/// built payload is retrievable, not just recorded -- the acceptance point.
///
/// Scoped by engagement (architecture.md Sec 3): the engagement id in the path
/// binds the build. The endpoint composes the audit write, mirroring how task
/// completion is recorded on the beacon stream (architecture.md Sec 11): the
/// build service is audit-agnostic by design, the transport layer is where the
/// build meets the audit trail. No implant is enrolled at build time, so the
/// audit event carries only the engagement and the requesting operator.
/// </summary>
public static class PayloadEndpoints
{
    public static IEndpointRouteBuilder MapPayloadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Operator-facing: a payload build requires an authenticated operator session.
        var group = endpoints.MapGroup("/engagements/{engagementId}/payloads").RequireAuthorization();

        group.MapPost("/", BuildAsync).WithName(nameof(BuildAsync));
        group.MapGet("/{artifactId}", DownloadAsync).WithName(nameof(DownloadAsync));

        return endpoints;
    }

    private static async Task<IResult> BuildAsync(
        string engagementId,
        BuildPayloadRequest body,
        ClaimsPrincipal user,
        PayloadBuildService builds,
        IEngagementRepository engagements,
        IPayloadStore payloads,
        IAuditStore audit,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // The requesting operator is the authenticated operator, resolved off the
        // session principal rather than named in the body (operator auth).
        var requestedBy = user.TryGetOperatorId();
        if (requestedBy is null)
            return Results.Unauthorized();
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        // The engagement must exist before anything is built: a build for a
        // bogus engagement would otherwise produce and audit an artifact against
        // an engagement with no other record.
        var engagement = await engagements.FindAsync(new EngagementId(engagementValue), cancellationToken);
        if (engagement is null)
            return Results.NotFound(new Problem("Engagement does not exist."));

        // Language and class come in as strings and parse to the enums; anything
        // that does not parse is a 400. The defaults keep a minimal request
        // valid (the in-tree .NET unit, a stage-2 implant, linux/amd64).
        if (!TryParseLanguage(body.Language, out var language))
            return Results.BadRequest(new Problem("Language is not recognized."));
        if (!TryParseClass(body.Class, out var @class))
            return Results.BadRequest(new Problem("Implant class is not recognized."));
        // The check-in mode rides the beacon profile into the artifact: stream
        // (persistent, interactive) or poll (low-and-slow check-ins). A typo
        // must not silently build the interactive shape for an operator who
        // asked for low-and-slow, so anything else is a 400.
        var mode = body.Mode?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mode))
            mode = "stream";
        if (mode is not ("stream" or "poll"))
            return Results.BadRequest(new Problem("Mode must be 'stream' or 'poll'."));

        // The stager output class (architecture.md Sec 6) references the
        // stage-2 payload it fetches at run time: resolve it here so the build
        // contract carries a verified reference -- the payload's id and
        // fingerprint -- rather than a raw operator string.
        Stage2Payload? stage2 = null;
        if (@class == ImplantClass.Stager)
        {
            if (body.Stage2PayloadId is not { } stage2Id)
                return Results.BadRequest(new Problem(
                    "A stager build requires stage2PayloadId: the built stage-2 payload the stager fetches."));
            if (!Guid.TryParse(stage2Id, out var stage2Value))
                return Results.BadRequest(new Problem("Stage2PayloadId is not a valid identifier."));
            var payload = await payloads.FindAsync(stage2Value, engagementValue, cancellationToken);
            if (payload is null)
                return Results.NotFound(new Problem(
                    "Stage2PayloadId does not name a payload in this engagement; build the stage-2 first."));
            stage2 = new Stage2Payload(stage2Value, payload.Fingerprint);
        }
        else if (body.Stage2PayloadId is not null)
        {
            return Results.BadRequest(new Problem("stage2PayloadId is only valid on a stager-class build."));
        }

        BuildArtifact artifact;
        try
        {
            artifact = await builds.BuildAsync(
            new BuildRequest(
                new EngagementId(engagementValue),
                requestedBy.Value,
                language,
                @class,
                new TargetProfile(body.TargetOs ?? "linux", body.TargetArch ?? "amd64"),
                BuildTransport(body),
                ParseDuration(body.SleepSeconds, DefaultSleep),
                ParseDuration(body.JitterSeconds, DefaultJitter),
                body.KillDate,
                mode,
                stage2),
            cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            // No build unit for the requested language (or another contract
            // failure): an operator mistake, not a server fault.
            return Results.BadRequest(new Problem(ex.Message));
        }
        catch (Exception ex)
        {
            // The build unit failed (toolchain error, disk, cancellation of a
            // child process). Keep the response generic; the server log carries
            // the detail.
            loggerFactory.CreateLogger("Rod.Transport.Endpoints.PayloadEndpoints")
                .LogError(ex, "Payload build failed for language {Language}.", language);
            return Results.Problem(
                title: "Payload build failed.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        // The built bytes are stored for retrieval, then the build is recorded
        // (architecture.md Sec 6/11): a PayloadBuilt audit event carrying the
        // class/config, the artifact fingerprint, and -- when the transform
        // chain ran -- the name of every applied transform (Sec 6, the
        // transform seam), so the trail proves which transforms produced the
        // stored bytes. No implant or task is bound yet, so those ids are
        // unused. The store stamps the chain hashes on append; the call site
        // supplies only the facts.
        var transformTrail = artifact.Transforms.Count == 0
            ? ""
            : " transforms=" + string.Join(
                ">",
                artifact.Transforms.Select(t => t.Metadata is null ? t.Name : $"{t.Name}({t.Metadata})"));
        await payloads.SaveAsync(
            new PayloadRecord(
                artifact.ArtifactId,
                artifact.EngagementId.Value,
                artifact.Class.ToString(),
                artifact.Language.ToString(),
                artifact.ContentType,
                artifact.Fingerprint,
                artifact.Content,
                artifact.Size,
                artifact.BuiltAt),
            cancellationToken);
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: artifact.EngagementId.Value,
                operatorId: requestedBy.Value.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "payload.build",
                kind: AuditEventKind.PayloadBuilt,
                payload: $"{artifact.Language}:{artifact.Params.Target.OperatingSystem}/{artifact.Params.Target.Architecture} {artifact.Params.Transport.Endpoint}{transformTrail}",
                output: null,
                outcome: artifact.Fingerprint,
                at: artifact.BuiltAt),
            cancellationToken);

        var response = new BuildPayloadResponse(
            artifact.ArtifactId.ToString(),
            artifact.EngagementId.ToString(),
            artifact.Class.ToString(),
            artifact.Language.ToString(),
            artifact.ContentType,
            artifact.Size,
            artifact.Fingerprint,
            artifact.BuiltAt,
            artifact.Transforms.Select(t => t.Name).ToArray());

        return Results.Created(
            $"/engagements/{response.EngagementId}/payloads/{response.ArtifactId}",
            response);
    }

    private static async Task<IResult> DownloadAsync(
        string engagementId,
        string artifactId,
        IEngagementRepository engagements,
        IPayloadStore payloads,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!Guid.TryParse(artifactId, out var artifactValue))
            return Results.BadRequest(new Problem("Artifact id is not a valid identifier."));

        var engagement = await engagements.FindAsync(new EngagementId(engagementValue), cancellationToken);
        if (engagement is null)
            return Results.NotFound(new Problem("Engagement does not exist."));

        var payload = await payloads.FindAsync(artifactValue, engagementValue, cancellationToken);
        if (payload is null)
            return Results.NotFound(new Problem("Payload does not exist in this engagement."));

        var fileName = $"rod-{payload.Class.ToLowerInvariant()}-{payload.PayloadId.ToString("N")[..8]}.bin";
        return Results.File(payload.Content, payload.ContentType, fileName);
    }

    private static readonly TimeSpan DefaultSleep = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultJitter = TimeSpan.FromSeconds(10);

    // Upper bound for operator-supplied sleep/jitter: a beacon interval beyond a
    // year is nonsense, and an unbounded double would overflow
    // TimeSpan.FromSeconds into a 500. The clamp keeps the request a clean 4xx
    // class instead.
    private const double MaxDurationSeconds = 31_536_000; // 1 year

    private static TimeSpan ParseDuration(double? seconds, TimeSpan fallback)
        => seconds is { } value && value >= 0
            ? TimeSpan.FromSeconds(Math.Min(value, MaxDurationSeconds))
            : fallback;

    // Builds the malleable transport profile off the request body
    // (architecture.md Sec 7). Endpoint and uri path are the always-set
    // positional fields; the malleable knobs default when the operator omits
    // them, so a minimal build request stays valid. Headers arrive as a flat
    // name/value map and are applied verbatim; an empty or null map adds none.
    private static TransportProfile BuildTransport(BuildPayloadRequest body)
    {
        var profile = new TransportProfile(
            body.Endpoint ?? "http://localhost:5080",
            body.UriPath ?? "/beacon");

        if (!string.IsNullOrWhiteSpace(body.EnrollPath))
            profile = profile with { EnrollPath = body.EnrollPath };
        if (!string.IsNullOrWhiteSpace(body.UserAgent))
            profile = profile with { UserAgent = body.UserAgent };
        if (body.Headers is { Count: > 0 } headers)
            profile = profile with { Headers = headers };
        if (body.RequestTimeoutSeconds is { } timeoutSeconds and >= 0)
            profile = profile with { RequestTimeout = TimeSpan.FromSeconds(timeoutSeconds) };
        if (body.Envelope is { } envelope
            && Enum.TryParse<TransportEnvelope>(envelope, ignoreCase: true, out var parsed))
        {
            profile = profile with { Envelope = parsed };
        }
        if (body.FallbackEndpoints is { Count: > 0 } fallbacks)
        {
            // The fallback list is the egress walk order (architecture.md Sec 8):
            // blanks are dropped rather than rejected so a trailing separator in
            // an operator's list is not a 400, and the surviving order is baked
            // verbatim.
            var cleaned = fallbacks
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Trim())
                .ToArray();
            if (cleaned.Length > 0)
                profile = profile with { FallbackEndpoints = cleaned };
        }

        return profile;
    }

    // Case-insensitive enum parse off the request string, with a fallback when
    // the field is absent so a minimal request stays valid.
    private static bool TryParseLanguage(string? text, out Language language)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            language = Language.DotNet; // the in-tree reference unit is .NET (ADR 0009).
            return true;
        }
        return Enum.TryParse(text, ignoreCase: true, out language);
    }

    private static bool TryParseClass(string? text, out ImplantClass @class)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            @class = ImplantClass.Stage2;
            return true;
        }
        return Enum.TryParse(text, ignoreCase: true, out @class);
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    // The malleable transport knobs are all optional: EnrollPath,
    // UserAgent, Headers, RequestTimeoutSeconds, Envelope, FallbackEndpoints.
    // An operator who omits them gets a profile with the unchanged wire shape.
    // Defaulted so a minimal positional construction (as in the integration
    // tests) stays valid.
    public sealed record BuildPayloadRequest(
        string? Language,
        string? Class,
        string? TargetOs,
        string? TargetArch,
        string? Endpoint,
        string? UriPath,
        double? SleepSeconds,
        double? JitterSeconds,
        DateTimeOffset? KillDate,
        string? Mode = null,
        string? EnrollPath = null,
        string? UserAgent = null,
        Dictionary<string, string>? Headers = null,
        double? RequestTimeoutSeconds = null,
        string? Envelope = null,
        string? Stage2PayloadId = null,
        List<string>? FallbackEndpoints = null);

    public sealed record BuildPayloadResponse(
        string ArtifactId,
        string EngagementId,
        string Class,
        string Language,
        string ContentType,
        long Size,
        string Fingerprint,
        DateTimeOffset BuiltAt,
        string[]? Transforms = null);

    public sealed record Problem(string Error);
}
