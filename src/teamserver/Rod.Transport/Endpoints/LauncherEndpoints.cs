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

// The standalone launcher surface: the operator's "give me the one-liner that
// beacons" (architecture.md Sec 8), without a caught shell to grow from. The
// shell console's Upgrade render and this endpoint share one definition of the
// flow -- resolve the web front and the stage-2 payload, mint the deployment
// credential, render the paste-ready downloader families -- so both surfaces
// answer identically whichever one an operator drives.

/// <summary>
/// The engagement-scoped launcher render: resolves the fetch front and the
/// stage-2 payload (each nameable, else the engagement's own preference),
/// mints the single deployment credential the fetch verifies and the
/// enrollment spends, records the mint on the engagement trail, and renders
/// the one-liners per downloader family. The token policy is the caller's:
/// the shell upgrade always mints single-use for thirty minutes (one paste,
/// one shell), while the standalone render lets the operator widen it for a
/// many-host deployment.
/// </summary>
public static class LauncherEndpoints
{
    public static IEndpointRouteBuilder MapLauncherEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/engagements/{engagementId}/launchers").RequireAuthorization();
        // POST on the collection renders a launcher set: the call creates the
        // one artifact this resource exists to produce (the minted credential
        // plus the one-liners), so the plain collection POST is the render.
        group.MapPost("/", RenderAsync).WithName(nameof(RenderAsync));
        return endpoints;
    }

    // The default token policy and the bounds an operator may widen it to:
    // one paste one beacon, thirty minutes, up to unlimited uses or a day --
    // a credential wider than the engagement's patience does not outlive it.
    private const int DefaultMaxUses = 1;
    private const int MaxUsesBound = 1000;
    private const int DefaultLifetimeMinutes = 30;
    private const int LifetimeMinutesBound = 24 * 60;

    private static async Task<IResult> RenderAsync(
        string engagementId,
        LauncherRenderRequest? body,
        ClaimsPrincipal user,
        IEngagementRepository engagements,
        IListenerStore listenerStore,
        IPayloadStore payloads,
        IStagerTokenService tokens,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // The rendering operator is the authenticated operator, resolved off
        // the session principal rather than named in the body.
        var operatorId = user.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();
        if (!EngagementId.TryParse(engagementId, out var engagement))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        var maxUses = body?.MaxUses ?? DefaultMaxUses;
        if (maxUses < 0 || maxUses > MaxUsesBound)
            return Results.BadRequest(new Problem($"MaxUses must be between 0 (unlimited) and {MaxUsesBound}."));
        var lifetimeMinutes = body?.LifetimeMinutes ?? DefaultLifetimeMinutes;
        if (lifetimeMinutes < 1 || lifetimeMinutes > LifetimeMinutesBound)
            return Results.BadRequest(new Problem($"LifetimeMinutes must be between 1 and {LifetimeMinutesBound}."));

        var (failure, set) = await LauncherRender.ResolveAsync(
            engagement,
            new LauncherSelection(
                body?.PayloadId,
                body?.ListenerId,
                maxUses,
                TimeSpan.FromMinutes(lifetimeMinutes),
                operatorId.Value,
                OriginShellSession: null,
                AuditOrigin: "origin=launcher"),
            engagements,
            listenerStore,
            payloads,
            tokens,
            audit,
            clock,
            cancellationToken);
        if (set is null)
            return failure!;

        return Results.Ok(new LauncherRenderResponse(
            set.Payload.PayloadId.ToString("N"),
            set.Url,
            set.Token.Secret,
            set.Token.ExpiresAt,
            set.Launchers));
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    /// <summary>
    /// Names the stage-2 payload and the web listener the fetch should ride
    /// (either may be omitted for the engagement's own preference: the newest
    /// build, the hardened front), and the deployment credential's policy:
    /// how many redeems it allows (0 = unlimited until expiry) and how long
    /// it lives.
    /// </summary>
    public sealed record LauncherRenderRequest(
        string? PayloadId = null,
        string? ListenerId = null,
        int? MaxUses = null,
        int? LifetimeMinutes = null);

    /// <summary>
    /// The rendered launcher set: the payload it grows into, the stage-2
    /// fetch URL, the deployment credential (shown exactly once -- here, never
    /// on the audit trail), and the paste-ready one-liners per downloader
    /// family.
    /// </summary>
    public sealed record LauncherRenderResponse(
        string PayloadId,
        string Url,
        string TokenSecret,
        DateTimeOffset TokenExpiresAt,
        IReadOnlyList<ShellLauncherResponse> Launchers);
}

/// <summary>What one render asked for, beyond the engagement it targets.</summary>
internal sealed record LauncherSelection(
    string? PayloadId,
    string? ListenerId,
    int MaxUses,
    TimeSpan Lifetime,
    OperatorId RequestedBy,
    ShellSessionId? OriginShellSession,
    string AuditOrigin);

/// <summary>The resolved pieces one render is made of.</summary>
internal sealed record LauncherSet(
    PayloadRecord Payload,
    StagerToken Token,
    string Url,
    IReadOnlyList<ShellLauncherResponse> Launchers);

/// <summary>
/// The shared render flow behind both launcher surfaces: the shell console's
/// Upgrade render (a caught shell growing a beacon) and the standalone
/// launcher endpoint. Everything except the calling endpoint's scoping lives
/// here, so the one-liners, the credential policy, and the mint's audit arc
/// cannot drift between the two.
/// </summary>
internal static class LauncherRender
{
    /// <summary>
    /// Resolves, mints, audits, and renders. Returns a failure result instead
    /// of a set when the engagement has no web listener to serve the fetch,
    /// no payload to grow into, or a name that matches neither.
    /// </summary>
    public static async Task<(IResult? Failure, LauncherSet? Set)> ResolveAsync(
        EngagementId engagement,
        LauncherSelection selection,
        IEngagementRepository engagements,
        IListenerStore listenerStore,
        IPayloadStore payloads,
        IStagerTokenService tokens,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (await engagements.FindAsync(engagement, cancellationToken) is not { } engagementRow)
            return (Results.NotFound(new Problem("Engagement does not exist.")), null);

        // The stage-2 fetch rides the engagement's web listeners, so the URL
        // needs one to exist. The operator may name the front the fetch
        // should use (several listeners, one specific redirector); unnamed,
        // the hardened members are preferred over cleartext.
        var webListeners = (await listenerStore.ListAsync(cancellationToken))
            .Where(l => l.EngagementId == engagement && IsWebTransport(l.Transport))
            .ToList();
        ListenerDefinition? webListener;
        if (selection.ListenerId is { } namedListener)
        {
            webListener = Guid.TryParse(namedListener, out var named)
                ? webListeners.FirstOrDefault(l => l.Id == named)
                : null;
            if (webListener is null)
                return (
                    Results.BadRequest(new Problem(
                        "ListenerId does not name one of this engagement's HTTP(S) listeners.")),
                    null);
        }
        else
        {
            webListener = webListeners
                .OrderByDescending(l => l.Transport == "https")
                .ThenByDescending(l => l.Transport == "mtls")
                .ThenBy(l => l.CreatedAt)
                .FirstOrDefault();
        }
        if (webListener is null)
            return (
                Results.BadRequest(new Problem(
                    "The engagement has no HTTP(S) listener to serve the stage-2 fetch; create one first.")),
                null);

        // The payload to grow into: the operator names one, or the newest
        // build in the engagement stands in.
        var payload = await ResolvePayloadAsync(payloads, engagement, selection.PayloadId, cancellationToken);
        if (payload is null)
            return (
                Results.BadRequest(new Problem(
                    "No stage-2 payload exists in this engagement; build one first, or name an existing payload id.")),
                null);

        // One deployment credential for the fetch: verified at the fetch,
        // spent at the enrollment that follows, and short-lived. The mint is
        // issued by the engagement's owner and recorded on the trail with the
        // rendering operator named; the secret itself never rides the audit.
        var at = clock.GetUtcNow();
        var token = await tokens.MintAsync(
            engagement, engagementRow.OwnerId, at,
            maxUses: selection.MaxUses, lifetime: selection.Lifetime,
            originShellSession: selection.OriginShellSession, cancellationToken: cancellationToken);
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: engagement.Value,
                operatorId: engagementRow.OwnerId.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "mint-stager-token",
                kind: AuditEventKind.StagerTokenMinted,
                payload: $"{selection.AuditOrigin} requestedBy={selection.RequestedBy.Value} uses={(selection.MaxUses == 0 ? "unlimited" : selection.MaxUses)} lifetime={selection.Lifetime.TotalMinutes:0}m",
                output: null,
                outcome: token.Id.ToString(),
                at),
            cancellationToken);

        var url = $"{webListener.PublicEndpoint.TrimEnd('/')}/implants/stage2/{payload.PayloadId:N}";
        return (null, new LauncherSet(
            payload,
            token,
            url,
            ShellUpgradeLaunchers.Render(url, token.Secret)
                .Select(l => new ShellLauncherResponse(l.Id, l.Os, l.Command))
                .ToArray()));
    }

    private static bool IsWebTransport(string transport)
        => transport is "http" or "https" or "mtls";

    private static async Task<PayloadRecord?> ResolvePayloadAsync(
        IPayloadStore payloads,
        EngagementId engagement,
        string? payloadId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(payloadId) && Guid.TryParse(payloadId, out var named))
            return await payloads.FindAsync(named, engagement.Value, cancellationToken);

        var newest = await payloads.ListAsync(engagement.Value, cancellationToken);
        return newest.OrderByDescending(p => p.BuiltAt).FirstOrDefault();
    }
}
