using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Listeners;
using Rod.CoreState.Operators;
using Rod.CoreState.ShellSessions;
using Rod.CoreState.Staging;
using Rod.Transport.Listeners.ShellCatch;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The engagement's caught-shell endpoints (architecture.md Sec 8): the
/// operator surface for the shells a shellcatch listener holds. Listing and
/// detail read the durable session registry (engagement-scoped like every
/// other surface, so cross-engagement access is refused as a plain 404);
/// the live routes -- input, output, close -- resolve the held socket
/// through the hub, and a shell that ended answers from the registry only.
/// Input and close are operator actions and are attributed as such on the
/// audit trail; the shell's own arrival and ending are recorded by the
/// listener service.
/// </summary>
public static class ShellSessionEndpoints
{
    // How long an output read parks waiting for the next chunk before
    // answering empty. A long poll keeps the console live at one request
    // per open terminal instead of a spinning poll loop, and stays a plain
    // HTTP request -- no second streaming surface to harden.
    private static readonly TimeSpan OutputWaitBudget = TimeSpan.FromSeconds(20);

    // The most chunks one output read returns. A catch-up read after a
    // pause must not ship an unbounded reply; the console pages by cursor.
    private const int OutputChunkCap = 512;

    public static IEndpointRouteBuilder MapShellSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/engagements/{engagementId}/shells").RequireAuthorization();

        group.MapGet("/", ListShellsAsync).WithName(nameof(ListShellsAsync));
        group.MapGet("/{id}", GetShellAsync).WithName(nameof(GetShellAsync));
        group.MapGet("/{id}/output", ReadOutputAsync).WithName(nameof(ReadOutputAsync));
        group.MapPost("/{id}:input", SendInputAsync).WithName(nameof(SendInputAsync));
        group.MapPost("/{id}:close", CloseShellAsync).WithName(nameof(CloseShellAsync));
        group.MapPost("/{id}:upgrade", UpgradeAsync).WithName(nameof(UpgradeAsync));

        return endpoints;
    }

    private static async Task<IResult> ListShellsAsync(
        string engagementId,
        IShellSessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        if (!EngagementId.TryParse(engagementId, out var engagement))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        var listed = await sessions.ListByEngagementAsync(engagement, cancellationToken);
        return Results.Ok(listed.Select(ShellSessionResponse.From).ToArray());
    }

    private static async Task<IResult> GetShellAsync(
        string engagementId,
        string id,
        IShellSessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        var (failure, scope) = await ResolveScopedShellAsync(
            engagementId, id, sessions, cancellationToken);
        if (scope is null)
            return failure!;

        return Results.Ok(ShellSessionResponse.From(scope.Session));
    }

    private static async Task<IResult> ReadOutputAsync(
        HttpContext http,
        string engagementId,
        string id,
        long? after,
        IShellSessionRegistry sessions,
        ShellCatchHub hub,
        CancellationToken cancellationToken)
    {
        var (failure, scope) = await ResolveScopedShellAsync(
            engagementId, id, sessions, cancellationToken);
        if (scope is null)
            return failure!;

        var shell = hub.Find(scope.Session.Id);
        if (shell is null)
            return Results.Json(
                new Problem("This shell has ended; its transcript lives on the audit trail."),
                statusCode: StatusCodes.Status410Gone);

        var cursor = after ?? 0;
        var chunks = shell.Output.ReadSince(cursor);
        if (chunks.Count == 0)
        {
            // The long poll: park until the log advances past the cursor,
            // bounded by the wait budget and the client going away.
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, http.RequestAborted);
            wait.CancelAfter(OutputWaitBudget);
            try
            {
                await shell.Output.WaitForAdvanceAsync(cursor, wait.Token);
                chunks = shell.Output.ReadSince(cursor);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The budget ran out (or the client left): answer with what
                // the cursor sees now, which is the empty catch-up the
                // console treats as "still quiet".
            }
        }

        return Results.Ok(new ShellOutputResponse(
            shell.Output.LatestSequence,
            chunks.Take(OutputChunkCap)
                .Select(c => new ShellChunkResponse(c.Sequence, c.At, c.Text))
                .ToArray()));
    }

    private static async Task<IResult> SendInputAsync(
        string engagementId,
        string id,
        ShellInputRequest body,
        ClaimsPrincipal user,
        IShellSessionRegistry sessions,
        ShellCatchHub hub,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var (failure, scope) = await ResolveScopedShellAsync(
            engagementId, id, sessions, cancellationToken);
        if (scope is null)
            return failure!;

        var operatorId = user.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();

        var shell = hub.Find(scope.Session.Id);
        if (shell is null)
            return Results.Conflict(
                new Problem("This shell has ended; input has nowhere to go."));

        if (string.IsNullOrEmpty(body.Text))
            return Results.BadRequest(new Problem("Input text is required."));

        if (!await shell.WriteInputAsync(body.Text, cancellationToken))
            return Results.Conflict(
                new Problem("Writing to this shell failed; it is ending."));

        var at = clock.GetUtcNow();
        await sessions.NoteInputAsync(scope.Session.Id, at, cancellationToken);
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: scope.Session.EngagementId.Value,
                operatorId: operatorId.Value.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "shell.session.input",
                kind: AuditEventKind.ShellSessionInput,
                payload: body.Text,
                output: null,
                outcome: scope.Session.Id.ToString(),
                at),
            cancellationToken);

        return Results.Ok(new { lastInputAt = at });
    }

    private static async Task<IResult> CloseShellAsync(
        string engagementId,
        string id,
        ClaimsPrincipal user,
        IShellSessionRegistry sessions,
        ShellCatchHub hub,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var (failure, scope) = await ResolveScopedShellAsync(
            engagementId, id, sessions, cancellationToken);
        if (scope is null)
            return failure!;

        var operatorId = user.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();

        var shell = hub.Find(scope.Session.Id);
        if (shell is null)
            return Results.Conflict(
                new Problem("This shell has already ended."));

        shell.CloseByOperator();
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: scope.Session.EngagementId.Value,
                operatorId: operatorId.Value.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "shell.session.closed",
                kind: AuditEventKind.ShellSessionClosed,
                payload: $"remote={shell.RemoteAddress}",
                output: null,
                outcome: scope.Session.Id.ToString(),
                at: clock.GetUtcNow()),
            cancellationToken);

        // The pump observes the close and finishes the session's Closed
        // marking; the roster event follows from it.
        return Results.Accepted();
    }

    // The upgrade render (architecture.md Sec 5.2, Sec 6, Sec 8): a caught
    // shell is anonymous and unenrolled, and its value ends where a real
    // implant begins. The render is deliberately advisory -- the endpoint
    // mints the deployment credential and hands back the one-liners for the
    // operator to paste into the shell, rather than the server writing into
    // the session's input: the paste is an operator action through the
    // audited input route, and every launcher is the standard
    // fetch-verify-run stager shape the stage-2 fetch already defines.
    private static async Task<IResult> UpgradeAsync(
        string engagementId,
        string id,
        ShellUpgradeRequest? body,
        ClaimsPrincipal user,
        IShellSessionRegistry sessions,
        IListenerStore listenerStore,
        IPayloadStore payloads,
        IStagerTokenService tokens,
        IEngagementRepository engagements,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var (failure, scope) = await ResolveScopedShellAsync(
            engagementId, id, sessions, cancellationToken);
        if (scope is null)
            return failure!;

        var operatorId = user.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();

        // The shared launcher flow (the standalone launchers endpoint renders
        // the same shape): resolve the front and payload, mint the paste's
        // download credential, render every downloader family. A caught shell
        // always mints single-use for thirty minutes -- one paste, one
        // download -- and the fetched artifact enrolls on the credential its
        // own build baked. The mint's origin carries this session, so the
        // trail names the shell the render was cut for.
        var (renderFailure, set) = await LauncherRender.ResolveAsync(
            scope.Engagement,
            new LauncherSelection(
                body?.PayloadId,
                body?.ListenerId,
                MaxUses: 1,
                Lifetime: TimeSpan.FromMinutes(30),
                RequestedBy: operatorId.Value,
                OriginShellSession: scope.Session.Id,
                AuditOrigin: $"origin=shell-upgrade shell={scope.Session.Id}"),
            engagements,
            listenerStore,
            payloads,
            tokens,
            audit,
            clock,
            cancellationToken);
        if (set is null)
            return renderFailure!;

        return Results.Ok(new ShellUpgradeResponse(
            set.Payload.PayloadId.ToString("N"),
            set.Url,
            set.Token.Secret,
            set.Token.ExpiresAt,
            set.Launchers));
    }

    // Resolves the scoped shell: the engagement in the path must own the
    // session, so a foreign engagement's id is indistinguishable from an
    // unknown one (the same construction every scoped surface follows).
    // Returns the failure result instead of a shell when resolution
    // refuses.
    private static async Task<(IResult? Failure, ScopedShell? Shell)> ResolveScopedShellAsync(
        string engagementId,
        string id,
        IShellSessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        if (!EngagementId.TryParse(engagementId, out var engagement))
            return (Results.BadRequest(new Problem("Engagement id is not a valid identifier.")), null);

        if (!ShellSessionId.TryParse(id, out var session))
            return (Results.BadRequest(new Problem("Shell session id is not a valid identifier.")), null);

        var found = await sessions.FindAsync(session, cancellationToken);
        if (found is null || found.EngagementId != engagement)
            return (Results.NotFound(new Problem("Shell session does not exist.")), null);

        return (null, new ScopedShell(found, engagement));
    }

    private sealed record ScopedShell(ShellSession Session, EngagementId Engagement);

    private sealed record ShellSessionResponse(
        string SessionId,
        string Status,
        string Os,
        string RemoteAddress,
        DateTimeOffset OpenedAt,
        DateTimeOffset? LastInputAt,
        DateTimeOffset? LastOutputAt,
        DateTimeOffset? EndedAt,
        string? UpgradedImplantId)
    {
        public static ShellSessionResponse From(ShellSession session)
            => new(
                session.Id.ToString(),
                session.Status.ToString().ToLowerInvariant(),
                session.Os.ToString().ToLowerInvariant(),
                session.RemoteAddress,
                session.OpenedAt,
                session.LastInputAt,
                session.LastOutputAt,
                session.EndedAt,
                session.UpgradedImplantId?.ToString());
    }

    private sealed record ShellOutputResponse(long LatestSequence, IReadOnlyList<ShellChunkResponse> Chunks);

    private sealed record ShellChunkResponse(long Sequence, DateTimeOffset At, string Text);
}

/// <summary>
/// Names the stage-2 payload a shell should grow into (omitted, the
/// engagement's newest build stands in) and optionally the web listener
/// whose front the fetch should ride (omitted, the hardened members are
/// preferred).
/// </summary>
public sealed record ShellUpgradeRequest(string? PayloadId, string? ListenerId = null);

/// <summary>
/// The rendered upgrade for one caught shell: the stage-2 fetch URL, the
/// single-use deployment credential it carries, and the paste-ready
/// one-liners per downloader family. The secret rides here exactly once --
/// on the operator answer -- and never on the audit trail.
/// </summary>
public sealed record ShellUpgradeResponse(
    string PayloadId,
    string Url,
    string TokenSecret,
    DateTimeOffset TokenExpiresAt,
    IReadOnlyList<ShellLauncherResponse> Launchers);

/// <summary>One paste-ready launcher, named for the surface it is pasted into.</summary>
public sealed record ShellLauncherResponse(string Id, string Os, string Command);

/// <summary>The submitted input for a caught shell: one line of operator text.</summary>
public sealed record ShellInputRequest(string Text);
