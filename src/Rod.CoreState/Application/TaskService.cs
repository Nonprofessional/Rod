using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
// The domain entity shares its name with System.Threading.Tasks.Task. Pin it
// here so the service's own Task type wins; the BCL type is reached by its full
// name where the methods below return it.
using Task = Rod.CoreState.Tasks.Task;

namespace Rod.CoreState.Application;

/// <summary>
/// The tasking use cases (roadmap M1.4): an operator issues a task against an
/// implant; the beacon stream pulls the next queued task to dispatch; the
/// implant's result is captured back into the task. Orchestrates the core-state
/// task port; holds no state of its own, and -- by design -- knows nothing of
/// the audit trail. The transport layer composes the audit write on result
/// capture (architecture.md Sec 11); audit wiring arrives properly with the
/// storage &amp; audit layer (roadmap M2.3).
///
/// As of the operator layer (roadmap M2.4), issuing a task also publishes a
/// <see cref="LiveEventKind.TaskIssued"/> event on the live bus so every
/// connected operator session sees new tasking the moment it is queued. The bus
/// is optional at the constructor: the core-state unit tests construct this
/// service without one, and the absence simply skips the publish (the task
/// itself is the source of truth; the bus is the transient fan-out).
///
/// Issuance is gated on the implant's class reduced verb set
/// (architecture.md Sec 5.2): a verb outside the class's set is refused before
/// the task is queued, throwing <see cref="TaskRejectedException"/> for the
/// transport to map. The implant is resolved here so the gate reads the class
/// the implant actually enrolled with, and the engagement binding is checked
/// against it (architecture.md Sec 3).
///
/// As with <see cref="EnrollmentService"/> and <see cref="HandshakeService"/>,
/// refusals propagate as exceptions the transport maps to wire status.
/// </summary>
public sealed class TaskService
{
    private readonly ITaskRepository _tasks;
    private readonly IImplantRepository _implants;
    private readonly TimeProvider _clock;
    private readonly ILiveEventBus? _bus;

    public TaskService(ITaskRepository tasks, IImplantRepository implants, TimeProvider clock)
        : this(tasks, implants, clock, bus: null)
    {
    }

    /// <summary>
    /// Constructs the service with a live-event bus. The composition root wires
    /// the bus (roadmap M2.4); the three-argument constructor above keeps the
    /// core-state unit tests bus-free.
    /// </summary>
    public TaskService(ITaskRepository tasks, IImplantRepository implants, TimeProvider clock, ILiveEventBus? bus)
    {
        _tasks = tasks;
        _implants = implants;
        _clock = clock;
        _bus = bus;
    }

    /// <summary>
    /// Issues a task: resolves and validates the implant against the engagement,
    /// gates the verb on the implant's class reduced verb set
    /// (architecture.md Sec 5.2), creates it in <see cref="TaskStatus.Queued"/>
    /// for the implant and persists it, then publishes a live event so connected
    /// operators see the new tasking in real time. Returns the created task.
    /// Throws <see cref="TaskRejectedException"/> (unknown implant, engagement
    /// mismatch, or an unsupported verb) for the transport to map to a wire
    /// status -- the class's reduced set is the authority for what the implant
    /// may run.
    /// </summary>
    public async System.Threading.Tasks.Task<TaskIssued> IssueAsync(
        IssueTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        // Resolve the implant and check its engagement binding (architecture.md
        // Sec 3) before anything is queued. The class the implant enrolled with
        // is what the verb gate below reads.
        var implant = await _implants.FindAsync(command.ImplantId, cancellationToken);
        if (implant is null)
        {
            throw new TaskRejectedException(
                TaskRejectionReason.UnknownImplant,
                $"Implant {command.ImplantId} is not enrolled.");
        }
        if (implant.EngagementId != command.EngagementId)
        {
            throw new TaskRejectedException(
                TaskRejectionReason.ImplantEngagementMismatch,
                $"Implant {implant.Id} belongs to engagement {implant.EngagementId}, " +
                $"not {command.EngagementId}.");
        }

        // A retired implant is out of operation and untaskable (architecture.md
        // Sec 7, M4.4). Checked after the binding resolves and before the verb
        // gate, so a retirement is refused regardless of the verb.
        if (implant.IsRetired)
        {
            throw new TaskRejectedException(
                TaskRejectionReason.ImplantRetired,
                $"Implant {implant.Id} was retired at {implant.RetiredAt:O}.");
        }

        // The per-class reduced verb set is the authority for what this implant
        // may run (architecture.md Sec 5.2). A verb outside it never reaches the
        // queue, so a reduced-class implant cannot be tasked past its purpose.
        if (!ImplantClassCapabilities.Allows(implant.Class, command.Verb))
        {
            throw new TaskRejectedException(
                TaskRejectionReason.UnsupportedVerbForClass,
                $"Verb '{command.Verb}' is not in the {implant.Class} reduced verb set.");
        }

        var task = Task.Create(
            TaskId.New(),
            command.EngagementId,
            command.ImplantId,
            command.IssuedBy,
            command.Verb,
            command.Arguments,
            now);
        await _tasks.SaveAsync(task, cancellationToken);

        if (_bus is not null)
        {
            await _bus.PublishAsync(
                LiveEvent.TaskIssued(
                    task.EngagementId,
                    task.IssuedBy,
                    task.ImplantId,
                    task.Id,
                    payload: $"{task.Verb} {task.Arguments}".TrimEnd(),
                    now),
                cancellationToken);
        }

        return new TaskIssued(
            task.Id,
            task.EngagementId,
            task.ImplantId,
            task.IssuedBy,
            task.Verb,
            task.Arguments,
            task.CreatedAt);
    }

    /// <summary>
    /// Pulls the next queued task for <paramref name="implant"/> and marks it
    /// dispatched, or returns null when nothing is queued. What the beacon
    /// stream drains on each check-in.
    /// </summary>
    public async System.Threading.Tasks.Task<TaskDispatched?> DispatchNextAsync(
        ImplantId implant,
        CancellationToken cancellationToken = default)
    {
        var task = await _tasks.NextPendingAsync(implant, cancellationToken);
        if (task is null)
            return null;

        var now = _clock.GetUtcNow();
        task.MarkDispatched(now);
        await _tasks.SaveAsync(task, cancellationToken);

        return new TaskDispatched(
            task.Id,
            task.EngagementId,
            task.ImplantId,
            task.Verb,
            task.Arguments,
            task.DispatchedAt!.Value);
    }

    /// <summary>
    /// Captures the implant's result into the task and completes it. Throws
    /// <see cref="InvalidOperationException"/> if the task is not in Dispatched.
    /// Returns the completed view so the caller (the transport) can build the
    /// audit event from it.
    /// </summary>
    public async System.Threading.Tasks.Task<TaskCompleted> RecordResultAsync(
        TaskId id,
        string output,
        TaskOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        var task = await _tasks.FindAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Task {id} is not known.");

        var now = _clock.GetUtcNow();
        task.Complete(output, outcome, now);
        await _tasks.SaveAsync(task, cancellationToken);

        return new TaskCompleted(
            task.Id,
            task.EngagementId,
            task.ImplantId,
            task.IssuedBy,
            task.Verb,
            task.Arguments,
            task.Output!,
            task.Outcome!.Value,
            task.CompletedAt!.Value);
    }
}

/// <summary>
/// Request to issue a task. <see cref="EngagementId"/> and <see cref="ImplantId"/>
/// scope it; <see cref="IssuedBy"/> attributes it; <see cref="Verb"/> is the
/// capability verb (e.g. <c>shell.exec</c>); <see cref="Arguments"/> is its input.
/// </summary>
public sealed record IssueTaskCommand(
    EngagementId EngagementId,
    ImplantId ImplantId,
    OperatorId IssuedBy,
    string Verb,
    string Arguments);

/// <summary>Result of issuing a task: its identity, scope, attribution, and verb.</summary>
public sealed record TaskIssued(
    TaskId TaskId,
    EngagementId EngagementId,
    ImplantId ImplantId,
    OperatorId IssuedBy,
    string Verb,
    string Arguments,
    DateTimeOffset CreatedAt);

/// <summary>Result of dispatching a task to the implant stream.</summary>
public sealed record TaskDispatched(
    TaskId TaskId,
    EngagementId EngagementId,
    ImplantId ImplantId,
    string Verb,
    string Arguments,
    DateTimeOffset DispatchedAt);

/// <summary>
/// Result of capturing a task's outcome: the full attributed, scoped record,
/// including the captured output. This is what the transport turns into an
/// audit event.
/// </summary>
public sealed record TaskCompleted(
    TaskId TaskId,
    EngagementId EngagementId,
    ImplantId ImplantId,
    OperatorId IssuedBy,
    string Verb,
    string Arguments,
    string Output,
    TaskOutcome Outcome,
    DateTimeOffset CompletedAt);
