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
/// Issuance is gated through the capability resolver
/// (<see cref="ITaskCapabilityResolver"/>, architecture.md Sec 5.2/10.3): the
/// per-class reduced verb set is the primary authority, and a verb a registered
/// capability module handles is admitted too -- so the evasion and exploit
/// categories (contract and dispatch only, not class-gated, Sec 10.2) are no
/// longer refused before dispatch. A verb outside both is refused before the task
/// is queued, throwing <see cref="TaskRejectedException"/> for the transport to
/// map. The implant is resolved here so the gate reads the class the implant
/// actually enrolled with, and the engagement binding is checked against it
/// (architecture.md Sec 3).
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
    private readonly ITaskCapabilityResolver _capabilities;

    public TaskService(ITaskRepository tasks, IImplantRepository implants, TimeProvider clock)
        : this(tasks, implants, clock, bus: null, capabilities: new ClassTableCapabilityResolver())
    {
    }

    /// <summary>
    /// Constructs the service with a live-event bus. The composition root wires
    /// the bus (roadmap M2.4); the three-argument constructor above keeps the
    /// core-state unit tests bus-free.
    /// </summary>
    public TaskService(ITaskRepository tasks, IImplantRepository implants, TimeProvider clock, ILiveEventBus? bus)
        : this(tasks, implants, clock, bus, capabilities: new ClassTableCapabilityResolver())
    {
    }

    /// <summary>
    /// Constructs the service with a live-event bus and a capability resolver.
    /// The resolver is the verb-gate authority (architecture.md Sec 5.2/10.3):
    /// the composition root passes the tradecraft-backed resolver once the
    /// capability registry is wired onto the live path, so the evasion and
    /// exploit categories -- contract and dispatch only, not class-gated
    /// (architecture.md Sec 10.2) -- are no longer refused before dispatch. The
    /// simpler constructors above keep the class-table-only default so the
    /// core-state unit tests and any host that does not opt into the tradecraft
    /// layer keep exactly the behavior they had.
    /// </summary>
    public TaskService(
        ITaskRepository tasks,
        IImplantRepository implants,
        TimeProvider clock,
        ILiveEventBus? bus,
        ITaskCapabilityResolver capabilities)
    {
        _tasks = tasks;
        _implants = implants;
        _clock = clock;
        _bus = bus;
        _capabilities = capabilities;
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

        // The capability resolver is the authority for what this implant may run
        // (architecture.md Sec 5.2, Sec 10.3). Its default is the per-class
        // reduced verb set; the composition root swaps in the tradecraft-backed
        // resolver so a verb a registered capability module handles -- including
        // the no-class-gate evasion and exploit categories (architecture.md Sec
        // 10.2) -- is admitted too. A verb outside both never reaches the queue,
        // so a reduced-class implant cannot be tasked past its purpose.
        if (!_capabilities.IsDispatchable(implant.Class, command.Verb))
        {
            throw new TaskRejectedException(
                TaskRejectionReason.UnsupportedVerbForClass,
                $"Verb '{command.Verb}' is not in the {implant.Class} reduced verb set " +
                "and no capability module is registered for it.");
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
    /// Atomically claims the next queued task for <paramref name="implant"/>,
    /// or returns null when nothing is queued. What the beacon stream drains on
    /// each check-in. The claim is atomic inside the repository adapter, so a
    /// reconnect overlap for one implant cannot dispatch the same task twice.
    /// </summary>
    public async System.Threading.Tasks.Task<TaskDispatched?> DispatchNextAsync(
        ImplantId implant,
        CancellationToken cancellationToken = default)
    {
        var task = await _tasks.ClaimNextPendingAsync(implant, _clock.GetUtcNow(), cancellationToken);
        if (task is null)
            return null;

        return new TaskDispatched(
            task.Id,
            task.EngagementId,
            task.ImplantId,
            task.IssuedBy,
            task.Verb,
            task.Arguments,
            task.DispatchedAt!.Value);
    }

    /// <summary>
    /// Returns a claimed task to the queue (architecture.md Sec 10.3): the
    /// transport calls this when the downstream frame write failed, so the task
    /// is redelivered on a later check-in instead of stranding Dispatched.
    /// Throws <see cref="InvalidOperationException"/> when the task is not in
    /// Dispatched -- same refusal shape as <see cref="RecordResultAsync"/>.
    /// </summary>
    public async System.Threading.Tasks.Task RequeueAsync(
        TaskId id,
        CancellationToken cancellationToken = default)
    {
        var task = await _tasks.FindAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Task {id} is not known.");

        task.Requeue();
        await _tasks.SaveAsync(task, cancellationToken);
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

/// <summary>
/// Result of dispatching a task to the implant stream. <see cref="IssuedBy"/> is
/// the operator whose tasking the dispatch carries out -- dispatch is
/// server-driven (the implant pulls the queue), so the event the beacon composes
/// attributes through this rather than through a request body.
/// </summary>
public sealed record TaskDispatched(
    TaskId TaskId,
    EngagementId EngagementId,
    ImplantId ImplantId,
    OperatorId IssuedBy,
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
