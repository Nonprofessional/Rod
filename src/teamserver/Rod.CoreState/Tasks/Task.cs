using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;

namespace Rod.CoreState.Tasks;

/// <summary>
/// A task -- a single verb an operator directs at one implant, scoped to an
/// engagement (architecture.md Sec 10.3). Every task is attributed from creation
/// (<see cref="EngagementId"/>, <see cref="ImplantId"/>, <see cref="IssuedBy"/>)
/// and walks <see cref="TaskStatus"/> Queued -&gt; Dispatched -&gt; Completed.
///
/// The verb is a namespaced capability (architecture.md Sec 10), e.g.
/// <c>shell.exec</c>; <see cref="Arguments"/> is its input (one-shot verbs carry
/// a single argument string). <see cref="Output"/> and <see cref="Outcome"/> are
/// set when the implant returns a result. Entity shape only: this type holds the
/// lifecycle and the captured result, nothing more.
/// </summary>
public sealed class Task
{
    public TaskId Id { get; }
    public EngagementId EngagementId { get; }
    public ImplantId ImplantId { get; }
    public OperatorId IssuedBy { get; }
    public string Verb { get; }
    public string Arguments { get; }
    public TaskStatus Status { get; private set; }
    public string? Output { get; private set; }
    public TaskOutcome? Outcome { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? DispatchedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    private Task(
        TaskId id,
        EngagementId engagementId,
        ImplantId implantId,
        OperatorId issuedBy,
        string verb,
        string arguments,
        TaskStatus status,
        DateTimeOffset createdAt)
    {
        Id = id;
        EngagementId = engagementId;
        ImplantId = implantId;
        IssuedBy = issuedBy;
        Verb = verb;
        Arguments = arguments;
        Status = status;
        CreatedAt = createdAt;
    }

    /// <summary>
    /// Factory for a freshly issued task. It enters the queue in
    /// <see cref="TaskStatus.Queued"/>. <paramref name="verb"/> is the capability
    /// verb; <paramref name="arguments"/> is its input (empty for argumentless
    /// verbs).
    /// </summary>
    public static Task Create(
        TaskId id,
        EngagementId engagementId,
        ImplantId implantId,
        OperatorId issuedBy,
        string verb,
        string arguments,
        DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(verb))
            throw new ArgumentException("Task verb is required.", nameof(verb));

        return new Task(
            id,
            engagementId,
            implantId,
            issuedBy,
            verb.Trim(),
            arguments,
            TaskStatus.Queued,
            createdAt);
    }

    /// <summary>
    /// Marks the task handed to the implant stream. Only legal from Queued.
    /// </summary>
    public void MarkDispatched(DateTimeOffset at)
    {
        if (Status != TaskStatus.Queued)
            throw new InvalidOperationException($"Task {Id} cannot be dispatched from {Status}.");

        Status = TaskStatus.Dispatched;
        DispatchedAt = at;
    }

    /// <summary>
    /// Records the implant's result and completes the task. Only legal from
    /// Dispatched. <paramref name="output"/> is the captured stdout/stderr (or
    /// equivalent); <paramref name="outcome"/> is success vs. failure.
    /// </summary>
    public void Complete(string output, TaskOutcome outcome, DateTimeOffset at)
    {
        if (Status != TaskStatus.Dispatched)
            throw new InvalidOperationException($"Task {Id} cannot be completed from {Status}.");

        Output = output;
        Outcome = outcome;
        Status = TaskStatus.Completed;
        CompletedAt = at;
    }
}
