using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;

namespace Rod.CoreState.Live;

/// <summary>
/// One realtime operational change pushed to connected operator sessions
/// (architecture.md Sec 4.1, layer 4). Every event is engagement-scoped: the bus
/// never delivers an event for one engagement to a subscriber on another
/// (architecture.md Sec 3). The wire serializer (transport) maps each kind onto
/// an SSE <c>event:</c> name and turns <see cref="Payload"/> into its
/// <c>data:</c> block, so this type carries only domain meaning, not framing.
///
/// The bus is best-effort: a dropped subscriber does not lose durable history --
/// the audit trail (Sec 11) is the attributed record. This type is the transient
/// projection operators read while they are connected.
/// </summary>
public sealed record LiveEvent(
    EngagementId EngagementId,
    LiveEventKind Kind,
    OperatorId OperatorId,
    ImplantId? ImplantId,
    TaskId? TaskId,
    string Payload,
    DateTimeOffset At)
{
    /// <summary>
    /// Builds a presence event (operator joined/left). No implant or task is
    /// involved; <paramref name="payload"/> carries the operator handle for the
    /// peers' "who is online" view.
    /// </summary>
    public static LiveEvent Presence(
        EngagementId engagement,
        LiveEventKind kind,
        OperatorId operatorId,
        string payload,
        DateTimeOffset at)
        => new(engagement, kind, operatorId, ImplantId: null, TaskId: null, payload, at);

    /// <summary>
    /// Builds a task-issued event. <paramref name="payload"/> is the verb and
    /// arguments; the operator and implant ids carry the attribution and scope.
    /// </summary>
    public static LiveEvent TaskIssued(
        EngagementId engagement,
        OperatorId operatorId,
        ImplantId implantId,
        TaskId taskId,
        string payload,
        DateTimeOffset at)
        => new(engagement, LiveEventKind.TaskIssued, operatorId, implantId, taskId, payload, at);

    /// <summary>
    /// Builds a task-completed event. <paramref name="payload"/> carries the
    /// captured output and outcome so peers see the result without re-reading.
    /// </summary>
    public static LiveEvent TaskCompleted(
        EngagementId engagement,
        OperatorId operatorId,
        ImplantId implantId,
        TaskId taskId,
        string payload,
        DateTimeOffset at)
        => new(engagement, LiveEventKind.TaskCompleted, operatorId, implantId, taskId, payload, at);
}
