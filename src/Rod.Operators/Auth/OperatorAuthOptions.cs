namespace Rod.Operators.Auth;

/// <summary>
/// Operator-authentication options, bound from the <c>Operators</c> configuration
/// section. The only piece today is the initial operator to seed on first start
/// -- the bootstrap account an operator logs in with before any other exists.
/// </summary>
public sealed class OperatorAuthOptions
{
    /// <summary>
    /// The initial operator provisioned at startup when none exists yet. In
    /// Production this must be supplied via configuration (or an environment
    /// variable override); in Development a built-in dev account is used as a
    /// fallback so <c>dotnet run</c> works out of the box, the same stance the
    /// dev implant CA takes.
    /// </summary>
    public InitialOperatorOptions? Initial { get; set; }
}

/// <summary>
/// The handle, display name, and plaintext password of the operator to seed. The
/// password is hashed immediately and never stored; this object lives only long
/// enough to read it from configuration and hash it.
/// </summary>
public sealed class InitialOperatorOptions
{
    public string Handle { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
