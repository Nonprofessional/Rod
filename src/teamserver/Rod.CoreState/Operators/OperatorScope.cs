namespace Rod.CoreState.Operators;

/// <summary>
/// The three global scopes an operator may hold (architecture.md Sec 4.5):
/// what the operator may do on the engagement surface. <see cref="Read"/> is
/// the viewing scope, <see cref="Task"/> the acting scope, and
/// <see cref="Approve"/> the second-pair-of-eyes scope the sensitive-verb
/// workflow consumes. The scopes are coordination discipline among trusted
/// operators, not a containment boundary; the default is all three, the peer
/// model every console ran on before roles existed.
/// </summary>
[Flags]
public enum OperatorScope
{
    None = 0,
    Read = 1,
    Task = 2,
    Approve = 4,

    /// <summary>Every scope -- the default a provisioned operator holds.</summary>
    All = Read | Task | Approve,
}

public static class OperatorScopes
{
    /// <summary>
    /// The authorization policy that requires <see cref="OperatorScope.Read"/>.
    /// Registered by the operator-auth layer; endpoint groups in transport name
    /// it through this constant, the same way the identity claims cross the
    /// layer boundary (architecture.md Sec 4.3).
    /// </summary>
    public const string ReadPolicy = "rod-scope:read";

    /// <summary>The authorization policy that requires the acting scope.</summary>
    public const string TaskPolicy = "rod-scope:task";

    /// <summary>
    /// Renders the scope set as its canonical claim string: the held scope
    /// names in flag order, comma-joined ("<c>read,task,approve</c>" for the
    /// default). The empty set renders as an empty string -- an operator that
    /// holds nothing.
    /// </summary>
    public static string ToClaimValue(OperatorScope scopes)
    {
        var names = new List<string>(3);
        if (scopes.HasFlag(OperatorScope.Read))
            names.Add("read");
        if (scopes.HasFlag(OperatorScope.Task))
            names.Add("task");
        if (scopes.HasFlag(OperatorScope.Approve))
            names.Add("approve");
        return string.Join(",", names);
    }

    /// <summary>
    /// Parses a claim value produced by <see cref="ToClaimValue"/>. Unknown
    /// names are ignored rather than refused -- a cookie stamped by a newer
    /// server degrades to the scopes this one understands, and the per-request
    /// store comparison replaces it anyway.
    /// </summary>
    public static OperatorScope FromClaimValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return OperatorScope.None;

        var scopes = OperatorScope.None;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            scopes |= part.ToLowerInvariant() switch
            {
                "read" => OperatorScope.Read,
                "task" => OperatorScope.Task,
                "approve" => OperatorScope.Approve,
                _ => OperatorScope.None,
            };
        }

        return scopes;
    }

    /// <summary>
    /// Validates a scope set for assignment (architecture.md Sec 4.5): the
    /// acting and approving scopes each require the viewing scope -- an
    /// operator who cannot see an engagement cannot act or approve on it.
    /// Returns the violation's description, or null when the set is valid.
    /// </summary>
    public static string? Validate(OperatorScope scopes)
    {
        if (scopes.HasFlag(OperatorScope.Task) && !scopes.HasFlag(OperatorScope.Read))
            return "The task scope requires the read scope.";
        if (scopes.HasFlag(OperatorScope.Approve) && !scopes.HasFlag(OperatorScope.Read))
            return "The approve scope requires the read scope.";
        return null;
    }
}
