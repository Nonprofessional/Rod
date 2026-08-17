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
/// The tasking use cases: an operator issues a task against an
/// implant; the beacon stream pulls the next queued task to dispatch; the
/// implant's result is captured back into the task. Orchestrates the core-state
/// task port; holds no state of its own, and -- by design -- knows nothing of
/// the audit trail. The transport layer composes the audit write on result
/// capture (architecture.md Sec 11); audit wiring arrives properly with the
/// storage &amp; audit layer.
///
/// As of the operator layer, issuing a task also publishes a
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
/// (architecture.md Sec 3). The engagement's rules-of-engagement scope is the
/// final issuance gate (architecture.md Sec 9): a task outside the profile the
/// engagement's operators set -- a non-permitted verb or target -- is refused
/// the same way, after the class gate so the refusal names the ROE rule.
///
/// As with <see cref="EnrollmentService"/> and <see cref="HandshakeService"/>,
/// refusals propagate as exceptions the transport maps to wire status.
/// </summary>
public sealed class TaskService
{
    private readonly ITaskRepository _tasks;
    private readonly IImplantRepository _implants;
    private readonly IEngagementRepository _engagements;
    private readonly TimeProvider _clock;
    private readonly ILiveEventBus? _bus;
    private readonly ITaskCapabilityResolver _capabilities;
    private readonly ITaskDispatchWake? _wake;

    public TaskService(
        ITaskRepository tasks,
        IImplantRepository implants,
        IEngagementRepository engagements,
        TimeProvider clock)
        : this(tasks, implants, engagements, clock, bus: null, capabilities: new ClassTableCapabilityResolver(), wake: null)
    {
    }

    /// <summary>
    /// Constructs the service with a live-event bus. The composition root wires
    /// the bus; the four-argument constructor above keeps the
    /// core-state unit tests bus-free.
    /// </summary>
    public TaskService(
        ITaskRepository tasks,
        IImplantRepository implants,
        IEngagementRepository engagements,
        TimeProvider clock,
        ILiveEventBus? bus)
        : this(tasks, implants, engagements, clock, bus, capabilities: new ClassTableCapabilityResolver(), wake: null)
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
    /// <param name="wake">
    /// The per-implant dispatch wake (architecture.md Sec 10.3): released on
    /// every accepted enqueue so the beacon stream's writer pushes the task
    /// downstream instead of waiting for a poll. A real parameter, not an
    /// optional one: the container selects the longest constructor whose
    /// parameters all resolve, so the wake reaches the host only when the
    /// composition root registers it (and the capability-resolver default)
    /// alongside this service. The chaining constructors pass null, which
    /// simply skips the release.
    /// </param>
    public TaskService(
        ITaskRepository tasks,
        IImplantRepository implants,
        IEngagementRepository engagements,
        TimeProvider clock,
        ILiveEventBus? bus,
        ITaskCapabilityResolver capabilities,
        ITaskDispatchWake? wake)
    {
        _tasks = tasks;
        _implants = implants;
        _engagements = engagements;
        _clock = clock;
        _bus = bus;
        _capabilities = capabilities;
        _wake = wake;
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
        // Sec 7). Checked after the binding resolves and before the verb
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

        // The engagement's rules-of-engagement scope is the last gate before
        // the queue (architecture.md Sec 9 -- ROE guardrails). Unlike the class
        // gate it is operator-set per engagement, not baked per implant class:
        // it narrows what the engagement's operators may task regardless of
        // what the implant could run. Checked after the binding and capability
        // gates so the refusal names the ROE rule, the most specific cause. An
        // engagement the implant binding already validated always resolves
        // here; a null engagement leaves the scope unrestricted rather than
        // inventing a refusal the binding checks above did not.
        var engagement = await _engagements.FindAsync(command.EngagementId, cancellationToken);
        var roeViolation = engagement?.Roe.Evaluate(implant.Id.ToString(), command.Verb);
        if (roeViolation is not null)
        {
            throw new TaskRejectedException(
                TaskRejectionReason.RoeViolation,
                $"Task refused by the engagement's rules of engagement: {roeViolation}.");
        }

        var task = Task.Create(
            TaskId.New(),
            command.EngagementId,
            command.ImplantId,
            command.IssuedBy,
            command.Verb,
            command.Arguments,
            now,
            command.StagedBytes);
        await _tasks.SaveAsync(task, cancellationToken);

        // The queue accepted the task: wake the beacon writer so it is pushed
        // downstream the moment it is queued, not on the next poll
        // (architecture.md Sec 10.3). A permit with no open stream simply
        // accumulates for the next connect.
        _wake?.Release(task.ImplantId);

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
            task.StagedBytes,
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

        // A requeue is an enqueue too: a live overlapping stream for the
        // implant picks the task back up immediately instead of waiting for
        // the reconnect (architecture.md Sec 10.3).
        _wake?.Release(task.ImplantId);
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
/// <see cref="StagedBytes"/> is the typed arm's advisory size
/// (architecture.md Sec 10): set (by the transport, which stages the bytes as a
/// task-bound artifact) when the payload is too large for the arguments string,
/// null for the ordinary inline shape.
/// </summary>
public sealed record IssueTaskCommand(
    EngagementId EngagementId,
    ImplantId ImplantId,
    OperatorId IssuedBy,
    string Verb,
    string Arguments,
    long? StagedBytes = null);

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
/// <see cref="StagedBytes"/> echoes the typed arm's marker so the stream writes
/// it onto the TaskRequest (architecture.md Sec 10).
/// </summary>
public sealed record TaskDispatched(
    TaskId TaskId,
    EngagementId EngagementId,
    ImplantId ImplantId,
    OperatorId IssuedBy,
    string Verb,
    string Arguments,
    long? StagedBytes,
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
