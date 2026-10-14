namespace Rod.Operators.Auth;

/// <summary>
/// Operator-authentication options, bound from the <c>Operators</c> configuration
/// section. The only piece today is the initial admin to seed on first start
/// -- the bootstrap account an operator logs in with before any other exists.
/// </summary>
public sealed class OperatorAuthOptions
{
    /// <summary>
    /// The initial admin provisioned at startup when none exists yet. The
    /// handle defaults to <c>admin</c>; the password defaults to
    /// <c>admin</c> in Development and, outside Development, to a generated
    /// password logged exactly once at the moment it is seeded. The display
    /// name is derived from the handle -- a deployment sets an account name
    /// and a password, nothing else.
    /// </summary>
    public InitialOperatorOptions? Initial { get; set; }
}

/// <summary>
/// The handle and plaintext password of the admin to seed. The password is
/// hashed immediately and never stored; this object lives only long enough
/// to read it from configuration and hash it.
/// </summary>
public sealed class InitialOperatorOptions
{
    public string Handle { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
