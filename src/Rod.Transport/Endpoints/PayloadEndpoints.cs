using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing payload-build endpoint (roadmap M3.1): an operator
/// requests a payload for an engagement, the build pipeline invokes the
/// language's build unit, and the fingerprinted artifact is recorded into the
/// audit trail. Lets an operator request a payload and get back a recorded,
/// fingerprinted artifact -- the M3.1 acceptance point.
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
        var group = endpoints.MapGroup("/engagements/{engagementId}/payloads");

        group.MapPost("/", BuildAsync).WithName(nameof(BuildAsync));

        return endpoints;
    }

    private static async Task<IResult> BuildAsync(
        string engagementId,
        BuildPayloadRequest body,
        PayloadBuildService builds,
        IAuditStore audit,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (body.RequestedBy is null)
            return Results.BadRequest(new Problem("Requesting operator id is required."));

        // Language and class come in as strings and parse to the enums; anything
        // that does not parse is a 400. The defaults keep a minimal request
        // valid (the stub Go unit, a stage-2 implant, linux/amd64).
        if (!TryParseLanguage(body.Language, out var language))
            return Results.BadRequest(new Problem("Language is not recognized."));
        if (!TryParseClass(body.Class, out var @class))
            return Results.BadRequest(new Problem("Implant class is not recognized."));

        var artifact = await builds.BuildAsync(
            new BuildRequest(
                new EngagementId(engagementValue),
                new OperatorId(body.RequestedBy.Value),
                language,
                @class,
                new TargetProfile(body.TargetOs ?? "linux", body.TargetArch ?? "amd64"),
                new TransportProfile(body.Endpoint ?? "http://localhost:5080", body.UriPath ?? "/beacon"),
                ParseDuration(body.SleepSeconds, DefaultSleep),
                ParseDuration(body.JitterSeconds, DefaultJitter),
                body.KillDate),
            cancellationToken);

        // The build is recorded (architecture.md Sec 6/11): a PayloadBuilt audit
        // event carrying the class/config and the artifact fingerprint. No implant
        // or task is bound yet, so those ids are unused. The store stamps the
        // chain hashes on append; the call site supplies only the facts.
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: artifact.EngagementId.Value,
                operatorId: body.RequestedBy.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: artifact.Class.ToString(),
                kind: AuditEventKind.PayloadBuilt,
                payload: $"{artifact.Language}:{artifact.Params.Target.OperatingSystem}/{artifact.Params.Target.Architecture} {artifact.Params.Transport.Endpoint}",
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
            artifact.BuiltAt);

        return Results.Created(
            $"/engagements/{response.EngagementId}/payloads/{response.ArtifactId}",
            response);
    }

    private static readonly TimeSpan DefaultSleep = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultJitter = TimeSpan.FromSeconds(10);

    private static TimeSpan ParseDuration(double? seconds, TimeSpan fallback)
        => seconds is { } value && value >= 0 ? TimeSpan.FromSeconds(value) : fallback;

    // Case-insensitive enum parse off the request string, with a fallback when
    // the field is absent so a minimal request stays valid.
    private static bool TryParseLanguage(string? text, out Language language)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            language = Language.Go; // the default until real per-language units land.
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

    public sealed record BuildPayloadRequest(
        Guid? RequestedBy,
        string? Language,
        string? Class,
        string? TargetOs,
        string? TargetArch,
        string? Endpoint,
        string? UriPath,
        double? SleepSeconds,
        double? JitterSeconds,
        DateTimeOffset? KillDate);

    public sealed record BuildPayloadResponse(
        string ArtifactId,
        string EngagementId,
        string Class,
        string Language,
        string ContentType,
        long Size,
        string Fingerprint,
        DateTimeOffset BuiltAt);

    public sealed record Problem(string Error);
}
