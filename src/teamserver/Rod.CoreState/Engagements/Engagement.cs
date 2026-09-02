namespace Rod.CoreState.Engagements;

/// <summary>
/// An engagement -- the unit of isolation and evidence for one authorized
/// operation (architecture.md Sec 3). Created by one operator (the owner, held
/// in <see cref="OwnerId"/> for accountability); any authenticated operator can
/// operate on it.
/// </summary>
public sealed class Engagement
{
    public EngagementId Id { get; }
    public string Name { get; private set; }

    /// <summary>
    /// The engagement's free-text description: the working record the crew
    /// carries through the engagement, set at creation and editable while the
    /// engagement operates. Optional and never interpreted by the framework.
    /// </summary>
    public string? Description { get; private set; }

    public OperatorId OwnerId { get; }
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// The engagement's rules-of-engagement scope (architecture.md Sec 9).
    /// Unrestricted until an operator applies a profile; see
    /// <see cref="ApplyRoe"/>.
    /// </summary>
    public RoeProfile Roe { get; private set; } = RoeProfile.Unrestricted;

    /// <summary>
    /// When the engagement was frozen for close-out (architecture.md Sec 2
    /// step 10): null while it is open. Freezing is the first half of the
    /// close-out path -- the engagement stops accepting new tasking and new
    /// deployments so its trail can be exported as final evidence. In-flight
    /// results still land: they are evidence of actions already taken.
    /// </summary>
    public DateTimeOffset? FrozenAt { get; private set; }

    /// <summary>
    /// When the engagement was retired (architecture.md Sec 2 step 10): null
    /// until the close-out completes. Retiring is terminal and requires a freeze
    /// first; the engagement stays readable as evidence but is operational no
    /// further.
    /// </summary>
    public DateTimeOffset? RetiredAt { get; private set; }

    /// <summary>
    /// True once the engagement is frozen or retired: it accepts no new tasking
    /// and no new deployments. The close-out state; the trail stays append-only
    /// either way.
    /// </summary>
    public bool IsClosed => FrozenAt is not null;

    /// <summary>True once the close-out has completed; terminal.</summary>
    public bool IsRetired => RetiredAt is not null;

    private Engagement(
        EngagementId id,
        string name,
        string? description,
        OperatorId ownerId,
        DateTimeOffset createdAt,
        RoeProfile? roe = null,
        DateTimeOffset? frozenAt = null,
        DateTimeOffset? retiredAt = null)
    {
        Id = id;
        Name = name;
        Description = description;
        OwnerId = ownerId;
        CreatedAt = createdAt;
        Roe = roe ?? RoeProfile.Unrestricted;
        FrozenAt = frozenAt;
        RetiredAt = retiredAt;
    }

    /// <summary>
    /// Creates a new engagement owned by <paramref name="ownerId"/>. The owner is
    /// recorded for accountability; access is not role-gated today (any
    /// authenticated operator can operate on any engagement).
    /// </summary>
    public static Engagement Create(
        EngagementId id,
        string name,
        OperatorId ownerId,
        DateTimeOffset createdAt,
        string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Engagement name is required.", nameof(name));

        var trimmedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        return new Engagement(id, name.Trim(), trimmedDescription, ownerId, createdAt);
    }

    /// <summary>
    /// Edits the engagement's working record: the name and the free-text
    /// description. Refused on a retired engagement -- retirement seals the
    /// engagement as evidence, and its identifying record stops moving with it.
    /// A frozen engagement stays editable: the freeze locks tasking and
    /// deployments, not the crew's own notes about what the engagement is.
    /// </summary>
    public void Edit(string name, string? description)
    {
        if (RetiredAt is not null)
            throw new InvalidOperationException($"Engagement {Id} is retired; its record is sealed.");

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Engagement name is required.", nameof(name));

        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    /// <summary>
    /// Applies the engagement's rules-of-engagement scope (architecture.md
    /// Sec 9). Replaces any prior profile; an empty profile is the unrestricted
    /// scope, so applying it reopens the engagement. The caller records the
    /// change in the engagement's audit trail.
    /// </summary>
    public void ApplyRoe(RoeProfile roe)
    {
        ArgumentNullException.ThrowIfNull(roe);
        Roe = roe;
    }

    /// <summary>
    /// Freezes the engagement for close-out (architecture.md Sec 2 step 10):
    /// from here it accepts no new tasking or deployments, so the trail can be
    /// exported as final evidence. Throws <see cref="InvalidOperationException"/>
    /// when the engagement is already frozen or retired -- the close-out path
    /// starts once.
    /// </summary>
    public void Freeze(DateTimeOffset at)
    {
        if (RetiredAt is not null)
            throw new InvalidOperationException($"Engagement {Id} is retired; its close-out already completed.");
        if (FrozenAt is not null)
            throw new InvalidOperationException($"Engagement {Id} is already frozen (at {FrozenAt:O}).");

        FrozenAt = at;
    }

    /// <summary>
    /// Retires the engagement, completing the close-out (architecture.md Sec 2
    /// step 10). Terminal, and only reachable from the frozen state -- retiring
    /// an open engagement would skip the evidence export the close-out exists
    /// for. Throws <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    public void Retire(DateTimeOffset at)
    {
        if (RetiredAt is not null)
            throw new InvalidOperationException($"Engagement {Id} is already retired (at {RetiredAt:O}).");
        if (FrozenAt is null)
            throw new InvalidOperationException(
                $"Engagement {Id} is open; freeze it and export its evidence package before retiring it.");

        RetiredAt = at;
    }
}
