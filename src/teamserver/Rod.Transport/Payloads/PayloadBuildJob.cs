using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Operators;

namespace Rod.Transport.Payloads;

/// <summary>
/// Where a background payload build sits in its arc. A build runs outside the
/// request that asked for it, so the job's state is the operator's only view
/// of progress: <see cref="Queued"/> (accepted, waiting for the single build
/// worker), <see cref="Running"/> (the build unit is compiling), and the two
/// terminal states carrying the artifact or the failure.
/// </summary>
public enum PayloadBuildJobState
{
    /// <summary>Accepted; waiting for the build worker.</summary>
    Queued,

    /// <summary>The build unit is compiling the artifact.</summary>
    Running,

    /// <summary>The artifact is stored and recorded; downloadable.</summary>
    Completed,

    /// <summary>The build refused the request or the unit failed.</summary>
    Failed,
}

/// <summary>
/// One background payload build: the request, the arc timestamps, and -- when
/// the arc ends well -- the stored artifact summary. The job record is the
/// server-side state a browser refresh loses, which is exactly why it lives
/// here rather than in the page: the build outlives any single view of it.
/// Jobs are disposable operational state (the artifact and its audit fact are
/// the durable records), so the registry keeps only the most recent jobs per
/// engagement and a teamserver restart drops in-flight ones.
/// </summary>
public sealed class PayloadBuildJob
{
    public PayloadBuildJob(Guid engagementId, OperatorId requestedBy, BuildRequest request, DateTimeOffset requestedAt)
    {
        JobId = Guid.NewGuid();
        EngagementId = engagementId;
        RequestedBy = requestedBy;
        Request = request;
        RequestedAt = requestedAt;
    }

    public Guid JobId { get; }
    public Guid EngagementId { get; }
    public OperatorId RequestedBy { get; }
    public BuildRequest Request { get; }
    public DateTimeOffset RequestedAt { get; }

    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public PayloadBuildJobState State { get; private set; } = PayloadBuildJobState.Queued;

    /// <summary>The failure text on a failed job; null otherwise.</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// The artifact summary on a completed job -- the same shape the
    /// synchronous build returns, download link included.
    /// </summary>
    public Endpoints.PayloadEndpoints.BuildPayloadResponse? Artifact { get; private set; }

    public bool IsTerminal => State is PayloadBuildJobState.Completed or PayloadBuildJobState.Failed;

    internal void MarkRunning(DateTimeOffset at)
    {
        StartedAt = at;
        State = PayloadBuildJobState.Running;
    }

    internal void MarkCompleted(Endpoints.PayloadEndpoints.BuildPayloadResponse artifact, DateTimeOffset at)
    {
        Artifact = artifact;
        CompletedAt = at;
        State = PayloadBuildJobState.Completed;
    }

    internal void MarkFailed(string error, DateTimeOffset at)
    {
        Error = error;
        CompletedAt = at;
        State = PayloadBuildJobState.Failed;
    }
}
