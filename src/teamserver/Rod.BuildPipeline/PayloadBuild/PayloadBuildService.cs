using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;

namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// The teamserver-side orchestrator that drives build units (architecture.md
/// Sec 6). On a payload request it resolves the language's build unit, generates
/// the per-implant material at request time (the key and the kill date are made
/// here so each artifact is unique -- Sec 6/Sec 5.1), assembles the
/// <see cref="BuildParams"/>, and asks the unit to build. Returns the
/// fingerprinted artifact for the transport layer to record.
///
/// Like <see cref="Rod.CoreState.Application.TaskService"/>, this service is
/// audit-agnostic by design: it produces the build, and the transport endpoint
/// composes the audit write (Sec 11). It depends only on core state and the
/// build contract, so the layer rule (BuildPipeline -> CoreState) is unchanged.
/// </summary>
public sealed class PayloadBuildService
{
    private readonly IBuildUnitRegistry _buildUnits;
    private readonly TimeProvider _clock;

    public PayloadBuildService(IBuildUnitRegistry buildUnits, TimeProvider clock)
    {
        _buildUnits = buildUnits;
        _clock = clock;
    }

    /// <summary>
    /// Builds a payload for the request's engagement and class. The kill date is
    /// resolved at request time, so two builds of the same request still never
    /// share an artifact (the baked profile and the compiler see to that).
    /// Throws <see cref="InvalidOperationException"/> when no build unit is
    /// registered for the requested language.
    /// </summary>
    public async Task<BuildArtifact> BuildAsync(
        BuildRequest request,
        CancellationToken cancellationToken = default)
    {
        var unit = _buildUnits.Find(request.Language)
            ?? throw new InvalidOperationException(
                $"No build unit registered for language {request.Language}.");

        var now = _clock.GetUtcNow();

        var @params = new BuildParams(
            request.EngagementId,
            request.RequestedBy,
            request.Class,
            request.Target,
            request.Transport,
            new BeaconProfile(request.Sleep, request.Jitter, ResolveKillDate(now, request.KillDate), request.Mode));

        return await unit.BuildAsync(@params, cancellationToken);
    }

    // The kill date defaults to a window from build time when the caller does not
    // pin one; a pinned date wins. Enforced later as a self-termination check
    //; here it is only baked into the artifact.
    private static DateTimeOffset ResolveKillDate(DateTimeOffset now, DateTimeOffset? requested)
    {
        if (requested is { } pinned && pinned > now)
            return pinned;
        return now + DefaultKillDateOffset;
    }

    private static readonly TimeSpan DefaultKillDateOffset = TimeSpan.FromDays(30);
}

/// <summary>
/// Request to build a payload. <see cref="EngagementId"/> scopes and
/// <see cref="RequestedBy"/> attributes the build; <see cref="Language"/> routes
/// to the build unit; <see cref="Class"/> is the implant class to generate.
/// </summary>
public sealed record BuildRequest(
    EngagementId EngagementId,
    OperatorId RequestedBy,
    Language Language,
    ImplantClass Class,
    TargetProfile Target,
    TransportProfile Transport,
    TimeSpan Sleep,
    TimeSpan Jitter,
    DateTimeOffset? KillDate,
    string Mode = "stream");
