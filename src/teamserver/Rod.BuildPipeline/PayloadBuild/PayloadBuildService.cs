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
/// <see cref="BuildParams"/>, asks the unit to build, and runs the post-build
/// <see cref="PayloadTransformChain"/> over the result -- the transform seam
/// (Sec 6): the stored bytes are the transformed bytes, the fingerprint covers
/// them, and the applied transform names ride the artifact into the audit
/// trail. Returns the fingerprinted artifact for the transport layer to record.
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
    private readonly PayloadTransformChain _transforms;

    public PayloadBuildService(IBuildUnitRegistry buildUnits, TimeProvider clock)
        : this(buildUnits, clock, PayloadTransformChain.Empty)
    {
    }

    /// <summary>
    /// Constructs the service with a post-build transform chain (architecture.md
    /// Sec 6, the transform seam): the composition root passes the config-loaded
    /// chain; the simpler constructor keeps the empty default so direct
    /// constructions (the unit tests, bare hosts) see bytes exactly as the build
    /// unit produced them.
    /// </summary>
    public PayloadBuildService(
        IBuildUnitRegistry buildUnits,
        TimeProvider clock,
        PayloadTransformChain transforms)
    {
        _buildUnits = buildUnits;
        _clock = clock;
        _transforms = transforms;
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
            new BeaconProfile(request.Sleep, request.Jitter, ResolveKillDate(now, request.KillDate), request.Mode),
            request.Stage2);

        var built = await unit.BuildAsync(@params, cancellationToken);

        // The transform seam (architecture.md Sec 6): the chain runs over the
        // unit's output before the artifact is recorded, so the fingerprint
        // the operator sees and the trail stores cover exactly the bytes the
        // target will run. An empty chain passes the bytes through untouched.
        var (content, applied) = await _transforms.ApplyAsync(@params, built.Content, cancellationToken);
        if (applied.Count == 0)
            return built;

        return BuildArtifact.Of(unit.Language, built.ArtifactId, @params, content, built.ContentType, built.BuiltAt)
            with
        {
            Transforms = applied
        };
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
/// <see cref="Stage2"/> carries the stage-2 payload reference a stager-class
/// build bakes in; it is required for the stager class and ignored elsewhere.
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
    string Mode = "stream",
    Stage2Payload? Stage2 = null);
