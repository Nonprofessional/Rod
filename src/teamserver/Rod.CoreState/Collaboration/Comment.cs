namespace Rod.CoreState.Collaboration;

/// <summary>
/// What a collaboration comment attaches to (architecture.md Sec 15.3,
/// ADR-005). The subject kinds are the entities an operator reasons about
/// during an engagement; the id is the subject entity's own id.
/// </summary>
public enum CommentSubjectKind
{
    /// <summary>A tasked unit -- the comment rides the task's arc.</summary>
    Task,

    /// <summary>An enrolled implant.</summary>
    Implant,

    /// <summary>An evidence object in the artifact store (Sec 11).</summary>
    Artifact,

    /// <summary>
    /// A file the engagement works with. Every file the platform holds is an
    /// artifact row; the kind keeps the operator's intent distinct from
    /// evidence attaches, so the UI can label them apart.
    /// </summary>
    File,

    /// <summary>The engagement itself.</summary>
    Engagement,

    /// <summary>An operator identity (deployment-wide; the comment itself is
    /// engagement-scoped process knowledge about that operator's work).</summary>
    Operator,
}

/// <summary>
/// One mutable process-knowledge comment (architecture.md Sec 15.3,
/// ADR-005): free text an operator attaches to an engagement entity,
/// editable by its author within a configured window. Deliberately modeled
/// apart from the audit chain -- the chain carries immutable operational
/// facts and mutable content would break its semantics -- so a comment's
/// only storage is the collaboration store, and it rides the timeline and
/// the live bus as an ordinary, explicitly mutable event.
/// </summary>
public sealed class Comment
{
    public CommentId Id { get; }
    public EngagementId EngagementId { get; }

    /// <summary>The entity the comment attaches to.</summary>
    public CommentSubjectKind SubjectKind { get; }

    /// <summary>The subject entity's id (plain Guid: subjects span several
    /// typed ids and the audit layer's plain-Guid artifacts).</summary>
    public Guid SubjectId { get; }

    /// <summary>The operator who wrote the comment; the only operator who
    /// may edit or delete it.</summary>
    public OperatorId AuthorId { get; }

    /// <summary>The comment text, carried verbatim. Task-reference tokens
    /// (<c>@task-&lt;id&gt;</c>, <c>$cred-&lt;id&gt;</c>) stay in placeholder
    /// form -- the server resolves references at publish, never here.</summary>
    public string Body { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>When the author last edited the body; null until the first
    /// edit. The edit lands as a new state of the same row -- there is no
    /// revision history to keep, the window is the guard.</summary>
    public DateTimeOffset? EditedAt { get; private set; }

    /// <summary>
    /// The full-state constructor (the persistence mirror rehydrates through
    /// it); creation goes through <see cref="Create"/>, which enforces the
    /// bounds.
    /// </summary>
    public Comment(
        CommentId id,
        EngagementId engagementId,
        CommentSubjectKind subjectKind,
        Guid subjectId,
        OperatorId authorId,
        string body,
        DateTimeOffset createdAt,
        DateTimeOffset? editedAt)
    {
        Id = id;
        EngagementId = engagementId;
        SubjectKind = subjectKind;
        SubjectId = subjectId;
        AuthorId = authorId;
        Body = body;
        CreatedAt = createdAt;
        EditedAt = editedAt;
    }

    /// <summary>
    /// Creates a comment, enforcing the structural bounds. Throws
    /// <see cref="ArgumentException"/> on out-of-bounds shape; who may
    /// comment, on what, and when edits close are service rules, not
    /// structural ones.
    /// </summary>
    public static Comment Create(
        CommentId id,
        EngagementId engagementId,
        CommentSubjectKind subjectKind,
        Guid subjectId,
        OperatorId authorId,
        string body,
        DateTimeOffset createdAt)
    {
        if (subjectId == Guid.Empty)
            throw new ArgumentException("A comment needs a subject id.", nameof(subjectId));
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("A comment needs a body.", nameof(body));
        if (body.Length > CollaborationLimits.MaxCommentChars)
            throw new ArgumentException($"The comment exceeds {CollaborationLimits.MaxCommentChars} characters.");
        return new Comment(
            id,
            engagementId,
            subjectKind,
            subjectId,
            authorId,
            body.Trim(),
            createdAt,
            editedAt: null);
    }

    /// <summary>
    /// Replaces the body (the author's edit within the window). Structural
    /// bounds only; the author and window rules are the service's.
    /// </summary>
    public void Edit(string body, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("A comment needs a body.", nameof(body));
        if (body.Length > CollaborationLimits.MaxCommentChars)
            throw new ArgumentException($"The comment exceeds {CollaborationLimits.MaxCommentChars} characters.");
        Body = body.Trim();
        EditedAt = at;
    }
}

/// <summary>
/// The refusable-comment signal the collaboration service throws: a comment
/// the structural bounds did not catch -- an unknown subject, a foreign
/// engagement, a closed engagement, a dangling task reference, or an edit
/// past its window or by another operator. The endpoint maps it to 422.
/// </summary>
public sealed class CommentRejectedException : DomainException
{
    public CommentRejectedException(string message)
        : base(message)
    {
    }
}
