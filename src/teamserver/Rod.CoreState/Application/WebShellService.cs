using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.WebShells;

namespace Rod.CoreState.Application;

/// <summary>
/// Registers and manages web-shell endpoints (architecture.md Sec 5.2's
/// Web-shell class). A web-shell is a script an operator placed in a
/// target's web root -- it never enrolls, never handshakes, and has no
/// process, so its identity is created directly here rather than through
/// the enrollment path: a WebShell-class implant row (the anchor tasks,
/// audit, and the roster already hang off) plus the connection profile the
/// adapter reads. The register action itself is the engagement binding --
/// the operator states "this URL belongs to this engagement" -- which is
/// the anonymous-endpoint sibling of a caught shell's listener scoping
/// (architecture.md Sec 3).
///
/// Execution is not here: it rides the normal task lifecycle, issued and
/// claimed synchronously by the transport's web-shell routes (architecture.md
/// Sec 10.3's synchronous exception), so this service stays free of HTTP.
/// </summary>
public sealed class WebShellService
{
    private readonly IImplantRepository _implants;
    private readonly IWebShellProfileRepository _profiles;
    private readonly IEngagementRepository _engagements;
    private readonly TimeProvider _clock;

    public WebShellService(
        IImplantRepository implants,
        IWebShellProfileRepository profiles,
        IEngagementRepository engagements,
        TimeProvider clock)
    {
        _implants = implants;
        _profiles = profiles;
        _engagements = engagements;
        _clock = clock;
    }

    /// <summary>
    /// Registers a web-shell endpoint: creates the WebShell-class implant
    /// row and its profile. The adapter and encoder names are validated by
    /// the transport against its registries before this runs; this service
    /// persists the choice. The implant carries no kill date -- a script's
    /// lifetime is the operator's discipline, not a fuse an implant would
    /// honor -- and reports the URL's host as its hostname so the roster
    /// reads sensibly.
    /// </summary>
    public async Task<(Implant Implant, WebShellProfile Profile)> RegisterAsync(
        EngagementId engagement,
        string url,
        string urlHost,
        string adapterId,
        string password,
        string encoder,
        string decoder,
        OperatorId registeredBy,
        CancellationToken cancellationToken = default)
    {
        if (await _engagements.FindAsync(engagement, cancellationToken) is null)
            throw new InvalidOperationException("Engagement does not exist.");

        var at = _clock.GetUtcNow();
        var implant = Implant.EnrollChild(
            ImplantId.New(),
            engagement,
            killDate: null,
            ImplantClass.WebShell,
            at,
            registeredBy,
            parentImplantId: null,
            hostname: urlHost);
        await _implants.SaveAsync(implant, cancellationToken);

        var profile = new WebShellProfile
        {
            ImplantId = implant.Id,
            EngagementId = engagement,
            Url = url,
            AdapterId = adapterId,
            Password = password,
            Encoder = encoder,
            Decoder = decoder,
            RegisteredBy = registeredBy,
            RegisteredAt = at,
        };
        await _profiles.SaveAsync(profile, cancellationToken);
        return (implant, profile);
    }

    /// <summary>
    /// Removes the endpoint: the profile goes, and the implant row retires
    /// so it drops off the taskable roster while its task history and audit
    /// arc stay readable (architecture.md Sec 7's shape).
    /// </summary>
    public async Task<bool> RemoveAsync(
        EngagementId engagement,
        ImplantId implant,
        CancellationToken cancellationToken = default)
    {
        var profile = await _profiles.FindAsync(implant, cancellationToken);
        if (profile is null || profile.EngagementId != engagement)
            return false;

        await _profiles.RemoveAsync(implant, cancellationToken);
        var row = await _implants.FindAsync(implant, cancellationToken);
        row?.Retire(_clock.GetUtcNow());
        if (row is not null)
            await _implants.SaveAsync(row, cancellationToken);
        return true;
    }

    /// <summary>
    /// Resolves the scoped endpoint pair -- the WebShell-class implant row
    /// and its profile -- refusing anything that is not a web-shell of this
    /// engagement (the same construction every scoped surface follows: a
    /// foreign or unknown id is indistinguishable).
    /// </summary>
    public async Task<(Implant Implant, WebShellProfile Profile)?> FindAsync(
        EngagementId engagement,
        ImplantId implant,
        CancellationToken cancellationToken = default)
    {
        var row = await _implants.FindAsync(implant, cancellationToken);
        if (row is null || row.EngagementId != engagement || row.Class != ImplantClass.WebShell)
            return null;

        var profile = await _profiles.FindAsync(implant, cancellationToken);
        return profile is null ? null : (row, profile);
    }

    /// <summary>
    /// Lists the engagement's endpoints, oldest registration first, joined
    /// with their implant rows (retirement included -- a removed endpoint
    /// stays readable until the engagement closes).
    /// </summary>
    public async Task<IReadOnlyList<(Implant Implant, WebShellProfile Profile)>> ListAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default)
    {
        var profiles = await _profiles.ListByEngagementAsync(engagement, cancellationToken);
        var joined = new List<(Implant, WebShellProfile)>(profiles.Count);
        foreach (var profile in profiles)
        {
            var row = await _implants.FindAsync(profile.ImplantId, cancellationToken);
            if (row is not null)
                joined.Add((row, profile));
        }

        return joined;
    }

    /// <summary>
    /// Records a probe outcome on the profile -- the endpoint's health
    /// answer, the web-shell's stand-in for a beacon's liveness.
    /// </summary>
    public async Task NoteProbeAsync(
        EngagementId engagement,
        ImplantId implant,
        bool ok,
        CancellationToken cancellationToken = default)
    {
        var profile = await _profiles.FindAsync(implant, cancellationToken);
        if (profile is null || profile.EngagementId != engagement)
            return;

        profile.NoteProbe(_clock.GetUtcNow(), ok);
        await _profiles.SaveAsync(profile, cancellationToken);
    }
}
