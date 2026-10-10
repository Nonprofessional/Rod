namespace Rod.CoreState.Operators;

/// <summary>
/// A global human identity and authorized user of the platform (glossary). An
/// operator authenticates with a handle and password; the scopes it holds
/// (architecture.md Sec 4.5) name what it may do on the engagement surface --
/// every provisioned operator holds all three by default, the peer model.
/// A disabled operator authenticates nowhere (login, cookie session, API
/// token all refuse) but keeps its scopes, so enabling restores exactly the
/// reach it had -- disable is the administrative off switch, not the parked
/// account shape (scopes none, still loginable).
/// </summary>
public sealed class Operator
{
    public OperatorId Id { get; }
    public string Handle { get; }
    public string DisplayName { get; }
    public OperatorScope Scopes { get; }
    public DateTimeOffset CreatedAt { get; }
    public bool Disabled { get; }

    public Operator(
        OperatorId id,
        string handle,
        string displayName,
        DateTimeOffset createdAt,
        OperatorScope scopes = OperatorScope.All,
        bool disabled = false)
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
        Disabled = disabled;
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
        => new(Id, Handle, DisplayName, CreatedAt, scopes, Disabled);

    /// <summary>
    /// This operator with its disabled flag flipped -- the immutable-entity
    /// way a disable or enable lands in the store. The flag gates
    /// authentication only; scopes ride along untouched so an enable restores
    /// the account exactly as it stood.
    /// </summary>
    public Operator WithDisabled(bool disabled)
        => new(Id, Handle, DisplayName, CreatedAt, Scopes, disabled);
}
