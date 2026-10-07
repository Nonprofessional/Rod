using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Rod.CoreState.Snippets;

namespace Rod.Operators.Snippets;

/// <summary>
/// The task-snippet use cases: save, list, read, delete. A snippet is
/// keyboard shorthand an operator fires while present (architecture.md
/// Sec 4.1, the operator layer's console ergonomics), so the service
/// judges only what it can -- the engagement open, the name free, the
/// budget unspent, the shape in bounds -- and leaves every capability
/// judgment to the issuance gates at run time, exactly as if the steps
/// were typed. Replay happens in the console, which issues each step
/// through the ordinary tasking path; this service never issues. Every
/// mutation lands in the engagement trail attributed to the mutating
/// operator.
/// </summary>
public sealed class TaskSnippetService
{
    private readonly ITaskSnippetStore _snippets;
    private readonly IEngagementRepository _engagements;
    private readonly IAuditStore _audit;
    private readonly TimeProvider _clock;

    public TaskSnippetService(
        ITaskSnippetStore snippets,
        IEngagementRepository engagements,
        IAuditStore audit,
        TimeProvider clock)
    {
        _snippets = snippets;
        _engagements = engagements;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>What the operator asked to persist (ids are typed; defaults resolve here).</summary>
    public sealed record CreateSnippetCommand(
        EngagementId EngagementId,
        string Name,
        IReadOnlyList<TaskSnippetStep> Steps,
        OperatorId CreatedBy);

    public async Task<TaskSnippet> CreateAsync(CreateSnippetCommand command, CancellationToken cancellationToken = default)
    {
        var engagement = await _engagements.FindAsync(command.EngagementId, cancellationToken)
            ?? throw new TaskSnippetRejectedException("Unknown engagement.");
        if (engagement.IsClosed || engagement.IsRetired)
            throw new TaskSnippetRejectedException("The engagement is closed; it accepts no new snippets.");

        var existing = await _snippets.ListByEngagementAsync(command.EngagementId, cancellationToken);
        if (existing.Count >= TaskSnippetLimits.MaxSnippetsPerEngagement)
            throw new TaskSnippetRejectedException(
                $"The engagement already holds {existing.Count} task snippets (limit {TaskSnippetLimits.MaxSnippetsPerEngagement}).");
        if (existing.Any(s => string.Equals(s.Name, command.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new TaskSnippetRejectedException($"A task snippet named '{command.Name.Trim()}' already exists in this engagement.");

        var snippet = TaskSnippet.Create(
            TaskSnippetId.New(),
            command.EngagementId,
            command.Name,
            command.Steps,
            _clock.GetUtcNow(),
            command.CreatedBy);

        await _snippets.SaveAsync(snippet, cancellationToken);
        await _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: snippet.EngagementId.Value,
                operatorId: snippet.CreatedBy.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "snippet.saved",
                kind: AuditEventKind.TaskSnippetSaved,
                payload: $"saved '{snippet.Name}' ({snippet.Steps.Count} steps: {DescribeSteps(snippet)})",
                output: null,
                outcome: snippet.Id.ToString(),
                at: snippet.CreatedAt),
            cancellationToken);
        return snippet;
    }

    public async Task<IReadOnlyList<TaskSnippet>> ListAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default)
        => await _snippets.ListByEngagementAsync(engagementId, cancellationToken);

    public async Task<TaskSnippet?> FindAsync(
        EngagementId engagementId,
        TaskSnippetId id,
        CancellationToken cancellationToken = default)
    {
        var snippet = await _snippets.FindAsync(id, cancellationToken);
        return snippet is { EngagementId: var scope } && scope == engagementId ? snippet : null;
    }

    /// <summary>Deletes the snippet outright. Returns false when it was not stored in this engagement.</summary>
    public async Task<bool> DeleteAsync(
        EngagementId engagementId,
        TaskSnippetId id,
        OperatorId deletedBy,
        CancellationToken cancellationToken = default)
    {
        var snippet = await FindAsync(engagementId, id, cancellationToken);
        if (snippet is null)
            return false;

        await _snippets.RemoveAsync(id, cancellationToken);
        await _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: snippet.EngagementId.Value,
                operatorId: deletedBy.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "snippet.deleted",
                kind: AuditEventKind.TaskSnippetDeleted,
                payload: $"deleted '{snippet.Name}' ({snippet.Steps.Count} steps: {DescribeSteps(snippet)})",
                output: null,
                outcome: snippet.Id.ToString(),
                at: _clock.GetUtcNow()),
            cancellationToken);
        return true;
    }

    private static string DescribeSteps(TaskSnippet snippet)
        => string.Join(", ", snippet.Steps.Select(s => s.Verb));
}
