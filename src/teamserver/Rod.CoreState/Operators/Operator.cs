namespace Rod.CoreState.Operators;

/// <summary>
/// A global human identity and authorized user of the platform (glossary). An
/// operator authenticates with a handle and password. The account carries no
/// global permission: what an operator may see and do lives per engagement,
/// as the memberships its owners granted (architecture.md Sec 3). A disabled
/// operator authenticates nowhere (login, cookie session, API token all
/// refuse) until enabled again.
/// </summary>
public sealed class Operator
{
    public OperatorId Id { get; }
    public string Handle { get; }
    public string DisplayName { get; }
    public DateTimeOffset CreatedAt { get; }
    public bool Disabled { get; }

    public Operator(
        OperatorId id,
        string handle,
        string displayName,
        DateTimeOffset createdAt,
        bool disabled = false)
    {
        if (string.IsNullOrWhiteSpace(handle))
            throw new ArgumentException("Operator handle is required.", nameof(handle));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Operator display name is required.", nameof(displayName));

        Id = id;
        Handle = handle.Trim();
        DisplayName = displayName.Trim();
        CreatedAt = createdAt;
        Disabled = disabled;
    }

    /// <summary>Factory for a newly registered operator.</summary>
    public static Operator Register(OperatorId id, string handle, string displayName, DateTimeOffset createdAt)
        => new(id, handle, displayName, createdAt);

    /// <summary>
    /// This operator with its disabled flag flipped -- the immutable-entity
    /// way a disable or enable lands in the store. The flag gates
    /// authentication only; the engagements an operator can reach ride on
    /// their memberships, untouched by the switch.
    /// </summary>
    public Operator WithDisabled(bool disabled)
        => new(Id, Handle, DisplayName, CreatedAt, disabled);
}
