using Rod.CoreState.Operators;

namespace Rod.CoreState.Snippets;

/// <summary>
/// One step of a <see cref="TaskSnippet"/>: a verb and the arguments
/// issued verbatim. The step carries no target -- the target is chosen at
/// run time, so one snippet serves the whole fleet.
/// </summary>
public sealed record TaskSnippetStep(string Verb, string Arguments);

/// <summary>
/// One engagement-scoped task snippet: a named sequence of issue commands
/// the operator console saves and replays (architecture.md Sec 4.1, the
/// operator layer's console ergonomics). A snippet is keyboard shorthand,
/// not automation -- it holds no trigger and no schedule, and running it
/// issues every step through <c>TaskService.IssueAsync</c> so the
/// issuance gates hold exactly as if the steps were typed. Save-time
/// validation is structural only (shape and bounds); the run-time gates
/// are the authority.
/// </summary>
public sealed class TaskSnippet
{
    public TaskSnippetId Id { get; }
    public EngagementId EngagementId { get; }

    /// <summary>The operator-facing name ("triage-sweep"), unique per engagement.</summary>
    public string Name { get; }

    /// <summary>The sequence, in issue order.</summary>
    public IReadOnlyList<TaskSnippetStep> Steps { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>The operator who saved the snippet.</summary>
    public OperatorId CreatedBy { get; }

    /// <summary>
    /// The full-state constructor (the persistence mirror rehydrates through
    /// it); creation goes through <see cref="Create"/>, which enforces the
    /// bounds.
    /// </summary>
    public TaskSnippet(
        TaskSnippetId id,
        EngagementId engagementId,
        string name,
        IReadOnlyList<TaskSnippetStep> steps,
        DateTimeOffset createdAt,
        OperatorId createdBy)
    {
        Id = id;
        EngagementId = engagementId;
        Name = name;
        Steps = steps;
        CreatedAt = createdAt;
        CreatedBy = createdBy;
    }

    /// <summary>
    /// Creates a snippet, enforcing the structural bounds (the boundary
    /// values live on <see cref="TaskSnippetLimits"/>). Throws
    /// <see cref="ArgumentException"/> on out-of-bounds shape; anything
    /// the run-time gates judge is deliberately not judged here.
    /// </summary>
    public static TaskSnippet Create(
        TaskSnippetId id,
        EngagementId engagementId,
        string name,
        IReadOnlyList<TaskSnippetStep> steps,
        DateTimeOffset createdAt,
        OperatorId createdBy)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A snippet needs a name.", nameof(name));
        if (name.Trim().Length > TaskSnippetLimits.MaxNameBytes)
            throw new ArgumentException($"The name exceeds {TaskSnippetLimits.MaxNameBytes} bytes.");
        if (steps.Count == 0)
            throw new ArgumentException("A snippet needs at least one step.", nameof(steps));
        if (steps.Count > TaskSnippetLimits.MaxStepsPerSnippet)
            throw new ArgumentException($"A snippet holds at most {TaskSnippetLimits.MaxStepsPerSnippet} steps.");
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Verb))
                throw new ArgumentException("Every step needs a verb.", nameof(steps));
            if (step.Verb.Length > TaskSnippetLimits.MaxVerbBytes)
                throw new ArgumentException($"A step's verb exceeds {TaskSnippetLimits.MaxVerbBytes} bytes.");
            if (step.Arguments.Length > TaskSnippetLimits.MaxArgumentBytes)
                throw new ArgumentException($"A step's arguments exceed {TaskSnippetLimits.MaxArgumentBytes} bytes.");
        }

        return new TaskSnippet(
            id,
            engagementId,
            name.Trim(),
            steps.Select(s => new TaskSnippetStep(s.Verb.Trim(), s.Arguments)).ToArray(),
            createdAt,
            createdBy);
    }
}
