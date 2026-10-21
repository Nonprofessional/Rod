using Microsoft.Extensions.Options;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Collaboration;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
// The domain entity shares its name with System.Threading.Tasks.Task; the
// BCL type is the only one used by name here.
using Task = System.Threading.Tasks.Task;

namespace Rod.Operators.Collaboration;

/// <summary>
/// The comment use cases (architecture.md Sec 15.3, ADR-005): add, edit,
/// delete, and read the engagement's mutable process knowledge. The
/// service judges what it can -- the engagement open, the subject real and
/// engagement-bound, task references resolving, the author the same
/// operator, the edit inside the window -- and every mutation publishes on
/// the live bus as an ordinary event. Nothing here writes the audit
/// trail: a comment is deliberately not an operational fact, and mutable
/// content never enters the chain.
/// </summary>
public sealed class CommentService
{
    private readonly ICommentStore _comments;
    private readonly IEngagementRepository _engagements;
    private readonly ITaskRepository _tasks;
    private readonly IImplantRepository _implants;
    private readonly IOperatorRepository _operators;
    private readonly IArtifactStore _artifacts;
    private readonly TaskReferenceService _references;
    private readonly ILiveEventBus? _bus;
    private readonly TimeProvider _clock;
    private readonly CollaborationOptions _options;

    public CommentService(
        ICommentStore comments,
        IEngagementRepository engagements,
        ITaskRepository tasks,
        IImplantRepository implants,
        IOperatorRepository operators,
        IArtifactStore artifacts,
        TaskReferenceService references,
        TimeProvider clock,
        IOptions<CollaborationOptions> options,
        ILiveEventBus? bus = null)
    {
        _comments = comments;
        _engagements = engagements;
        _tasks = tasks;
        _implants = implants;
        _operators = operators;
        _artifacts = artifacts;
        _references = references;
        _bus = bus;
        _clock = clock;
        _options = options.Value;
    }

    /// <summary>The window the author may still edit within, from creation.</summary>
    public TimeSpan EditWindow => _options.CommentEditWindow;

    public async Task<Comment> CreateAsync(
        EngagementId engagement,
        CommentSubjectKind subjectKind,
        Guid subjectId,
        OperatorId author,
        string body,
        CancellationToken cancellationToken = default)
    {
        await AssertOpenAsync(engagement, cancellationToken);
        await AssertSubjectAsync(engagement, subjectKind, subjectId, cancellationToken);
        await AssertReferencesAsync(engagement, body, cancellationToken);

        var comment = Comment.Create(CommentId.New(), engagement, subjectKind, subjectId, author, body, _clock.GetUtcNow());
        await _comments.SaveAsync(comment, cancellationToken);

        if (_bus is not null)
        {
            await _bus.PublishAsync(
                LiveEvent.Comment(
                    engagement,
                    LiveEventKind.CommentAdded,
                    author,
                    subjectKind,
                    subjectId,
                    $"{SubjectLabel(subjectKind, subjectId)} {comment.Body}",
                    comment.CreatedAt),
                cancellationToken);
        }

        return comment;
    }

    /// <summary>
    /// The author's edit of their comment. Returns null when the comment is
    /// unknown or belongs to another engagement; throws
    /// <see cref="CommentRejectedException"/> when the window closed, the
    /// caller is not the author, the engagement closed, or the new body's
    /// references dangle.
    /// </summary>
    public async Task<Comment?> EditAsync(
        EngagementId engagement,
        CommentId id,
        OperatorId author,
        string body,
        CancellationToken cancellationToken = default)
    {
        var comment = await _comments.FindAsync(id, cancellationToken);
        if (comment is null || comment.EngagementId != engagement)
            return null;

        await AssertOpenAsync(engagement, cancellationToken);
        AssertAuthorAndWindow(comment, author);
        await AssertReferencesAsync(engagement, body, cancellationToken);

        comment.Edit(body, _clock.GetUtcNow());
        await _comments.SaveAsync(comment, cancellationToken);

        if (_bus is not null)
        {
            await _bus.PublishAsync(
                LiveEvent.Comment(
                    engagement,
                    LiveEventKind.CommentEdited,
                    author,
                    comment.SubjectKind,
                    comment.SubjectId,
                    $"{SubjectLabel(comment.SubjectKind, comment.SubjectId)} {comment.Body}",
                    comment.EditedAt!.Value),
                cancellationToken);
        }

        return comment;
    }

    /// <summary>
    /// The author's deletion of their comment, under the edit window's rules.
    /// Returns null when the comment is unknown or belongs to another
    /// engagement.
    /// </summary>
    public async Task<Comment?> DeleteAsync(
        EngagementId engagement,
        CommentId id,
        OperatorId author,
        CancellationToken cancellationToken = default)
    {
        var comment = await _comments.FindAsync(id, cancellationToken);
        if (comment is null || comment.EngagementId != engagement)
            return null;

        await AssertOpenAsync(engagement, cancellationToken);
        AssertAuthorAndWindow(comment, author);

        await _comments.RemoveAsync(id, cancellationToken);

        if (_bus is not null)
        {
            await _bus.PublishAsync(
                LiveEvent.Comment(
                    engagement,
                    LiveEventKind.CommentDeleted,
                    author,
                    comment.SubjectKind,
                    comment.SubjectId,
                    SubjectLabel(comment.SubjectKind, comment.SubjectId),
                    _clock.GetUtcNow()),
                cancellationToken);
        }

        return comment;
    }

    /// <summary>The comments attached to one subject, oldest first.</summary>
    public Task<IReadOnlyList<Comment>> ListBySubjectAsync(
        EngagementId engagement,
        CommentSubjectKind subjectKind,
        Guid subjectId,
        CancellationToken cancellationToken = default)
        => _comments.ListBySubjectAsync(engagement, subjectKind, subjectId, cancellationToken);

    /// <summary>
    /// Whether <paramref name="operator"/> may still edit
    /// <paramref name="comment"/>: the author, inside the window. The cheap
    /// read-side hint the listing renders; the mutation path enforces it
    /// authoritatively, engagement state included.
    /// </summary>
    public bool IsEditable(Comment comment, OperatorId @operator)
        => comment.AuthorId == @operator
            && _clock.GetUtcNow() - comment.CreatedAt < _options.CommentEditWindow;

    private async Task AssertOpenAsync(EngagementId engagement, CancellationToken cancellationToken)
    {
        var found = await _engagements.FindAsync(engagement, cancellationToken)
            ?? throw new CommentRejectedException("Unknown engagement.");
        if (found.IsClosed || found.IsRetired)
            throw new CommentRejectedException("The engagement is closed; its process knowledge is read-only.");
    }

    // A comment hangs on a real entity of the same engagement: a dangling
    // subject is refused at publish, exactly like a dangling task reference.
    // The file subject rides the artifact store -- every file the platform
    // holds is an artifact row (Sec 11) -- with its own kind so the console
    // can tell a working-file note from an evidence remark.
    private async Task AssertSubjectAsync(
        EngagementId engagement,
        CommentSubjectKind subjectKind,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        switch (subjectKind)
        {
            case CommentSubjectKind.Task:
                var task = await _tasks.FindAsync(new TaskId(subjectId), cancellationToken);
                if (task is null || task.EngagementId != engagement)
                    throw new CommentRejectedException($"Task {subjectId} does not exist in this engagement.");
                break;
            case CommentSubjectKind.Implant:
                var implant = await _implants.FindAsync(new ImplantId(subjectId), cancellationToken);
                if (implant is null || implant.EngagementId != engagement)
                    throw new CommentRejectedException($"Implant {subjectId} does not exist in this engagement.");
                break;
            case CommentSubjectKind.Artifact:
            case CommentSubjectKind.File:
                var artifact = await _artifacts.FindAsync(subjectId, cancellationToken);
                if (artifact is null || artifact.EngagementId != engagement.Value)
                    throw new CommentRejectedException($"Artifact {subjectId} does not exist in this engagement.");
                break;
            case CommentSubjectKind.Engagement:
                if (subjectId != engagement.Value)
                    throw new CommentRejectedException("The engagement subject must be the comment's own engagement.");
                break;
            case CommentSubjectKind.Operator:
                var subject = await _operators.FindAsync(new OperatorId(subjectId), cancellationToken);
                if (subject is null)
                    throw new CommentRejectedException($"Operator {subjectId} does not exist.");
                break;
            default:
                throw new CommentRejectedException($"Unknown subject kind {subjectKind}.");
        }
    }

    private async Task AssertReferencesAsync(EngagementId engagement, string body, CancellationToken cancellationToken)
    {
        var unresolved = await _references.UnresolvedAsync(engagement, body, cancellationToken);
        if (unresolved.Count > 0)
        {
            throw new CommentRejectedException(
                "The comment references "
                + string.Join(", ", unresolved.Select(t => t.Render()))
                + " -- no such task or credential in this engagement.");
        }
    }

    private void AssertAuthorAndWindow(Comment comment, OperatorId @operator)
    {
        if (comment.AuthorId != @operator)
            throw new CommentRejectedException("Only the comment's author may edit or delete it.");
        if (_clock.GetUtcNow() - comment.CreatedAt >= _options.CommentEditWindow)
            throw new CommentRejectedException(
                $"The comment's edit window ({_options.CommentEditWindowMinutes} minutes) has closed.");
    }

    private static string SubjectLabel(CommentSubjectKind kind, Guid subjectId)
        => $"{kind.ToString().ToLowerInvariant()}/{subjectId}";
}
