namespace Rod.CoreState.Operators;

/// <summary>
/// A global human identity and authorized user of the platform (glossary). An
/// operator authenticates with a handle and password; the scopes it holds
/// (architecture.md Sec 4.5) name what it may do on the engagement surface --
/// every provisioned operator holds all three by default, the peer model.
/// </summary>
public sealed class Operator
{
    public OperatorId Id { get; }
    public string Handle { get; }
    public string DisplayName { get; }
    public OperatorScope Scopes { get; }
    public DateTimeOffset CreatedAt { get; }

    public Operator(
        OperatorId id,
        string handle,
        string displayName,
        DateTimeOffset createdAt,
        OperatorScope scopes = OperatorScope.All)
    {
        if (string.IsNullOrWhiteSpace(handle))
            throw new ArgumentException("Operator handle is required.", nameof(handle));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Operator display name is required.", nameof(displayName));
        // Flags semantics: any combination of the defined scopes is a valid
        // set; a bit outside the mask is not (a value from a newer server).
        if ((~OperatorScope.All & scopes) != 0)
            throw new ArgumentException($"Operator scopes '{scopes}' are not a valid scope set.", nameof(scopes));

        Id = id;
        Handle = handle.Trim();
        DisplayName = displayName.Trim();
        Scopes = scopes;
        CreatedAt = createdAt;
    }

    /// <summary>Factory for a newly registered operator.</summary>
    public static Operator Register(OperatorId id, string handle, string displayName, DateTimeOffset createdAt)
        => new(id, handle, displayName, createdAt);

    /// <summary>
    /// This operator with a different scope set -- the immutable-entity way a
    /// scope assignment lands in the store (validate with
    /// <see cref="OperatorScopes.Validate"/> first; the replacement carries no
    /// opinion about the set's validity).
    /// </summary>
    public Operator WithScopes(OperatorScope scopes)
        => new(Id, Handle, DisplayName, CreatedAt, scopes);
}
