namespace Rod.CoreState.Operators;

/// <summary>
/// The capability floor an API token carries (architecture.md Sec 15.3,
/// ADR-006): the scopes a minted token may hold, each capping what the
/// token's operator principal may do below what the operator's own session
/// allows. Hierarchical -- <see cref="Admin"/> carries everything
/// <see cref="Operate"/> carries, which carries everything <see cref="Read"/>
/// carries -- so a comparison is a rank comparison. This is a property of the
/// credential, not of the operator: the deployment role and the engagement
/// memberships keep governing as they do for a signed-in operator, and the
/// scope only ever narrows.
/// </summary>
public enum OperatorTokenScope
{
    Read = 1,
    Operate = 2,
    Admin = 3,
}

public static class OperatorTokenScopes
{
    /// <summary>The wire name of <see cref="OperatorTokenScope.Read"/>.</summary>
    public const string ReadName = "read";

    /// <summary>The wire name of <see cref="OperatorTokenScope.Operate"/>.</summary>
    public const string OperateName = "operate";

    /// <summary>The wire name of <see cref="OperatorTokenScope.Admin"/>.</summary>
    public const string AdminName = "admin";

    /// <summary>
    /// The scope's wire name, for the mint request, the claim, and response
    /// rendering.
    /// </summary>
    public static string ToName(OperatorTokenScope scope)
        => scope switch
        {
            OperatorTokenScope.Read => ReadName,
            OperatorTokenScope.Operate => OperateName,
            OperatorTokenScope.Admin => AdminName,
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };

    /// <summary>
    /// Parses a wire name into a scope. The mint route and the claim reader
    /// both refuse unknown values rather than guessing a floor.
    /// </summary>
    public static bool TryFromName(string? name, out OperatorTokenScope scope)
    {
        switch (name)
        {
            case ReadName:
                scope = OperatorTokenScope.Read;
                return true;
            case OperateName:
                scope = OperatorTokenScope.Operate;
                return true;
            case AdminName:
                scope = OperatorTokenScope.Admin;
                return true;
            default:
                scope = OperatorTokenScope.Read;
                return false;
        }
    }
}
