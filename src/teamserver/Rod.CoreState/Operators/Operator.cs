namespace Rod.CoreState.Operators;

/// <summary>
/// A global human identity and authorized user of the platform (glossary). An
/// operator authenticates with a handle and password; any authenticated operator
/// can operate on any engagement.
/// </summary>
public sealed class Operator
{
    public OperatorId Id { get; }
    public string Handle { get; }
    public string DisplayName { get; }
    public DateTimeOffset CreatedAt { get; }

    public Operator(OperatorId id, string handle, string displayName, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(handle))
            throw new ArgumentException("Operator handle is required.", nameof(handle));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Operator display name is required.", nameof(displayName));

        Id = id;
        Handle = handle.Trim();
        DisplayName = displayName.Trim();
        CreatedAt = createdAt;
    }

    /// <summary>Factory for a newly registered operator.</summary>
    public static Operator Register(OperatorId id, string handle, string displayName, DateTimeOffset createdAt)
        => new(id, handle, displayName, createdAt);
}
