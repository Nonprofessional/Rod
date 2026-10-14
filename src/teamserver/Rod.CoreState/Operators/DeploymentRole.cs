namespace Rod.CoreState.Operators;

/// <summary>
/// The two deployment roles an account can hold (architecture.md Sec 4.5). The
/// bootstrap-seeded account is <see cref="Admin"/>: the deployment's
/// management surface -- other operators' accounts, the redirector fleet, the
/// runtime settings -- is the admin's alone. Every account created through
/// the console is <see cref="Member"/>, which keeps self-service over its own
/// password and tokens and nothing on anyone else's account. This is
/// deployment administration, not the retired global scope model: what an
/// operator may reach inside an engagement still lives per engagement, as the
/// memberships its owners granted. The role is fixed at registration --
/// there is no promote or demote surface; the bootstrap seed is the
/// deployment's root of trust.
/// </summary>
public enum DeploymentRole
{
    Admin = 1,
    Member = 2,
}

public static class DeploymentRoles
{
    /// <summary>The wire name of <see cref="DeploymentRole.Admin"/>.</summary>
    public const string AdminName = "admin";

    /// <summary>The wire name of <see cref="DeploymentRole.Member"/>.</summary>
    public const string MemberName = "member";

    /// <summary>
    /// The wire name of the role ("admin" / "member"), for the session claim
    /// and response rendering.
    /// </summary>
    public static string ToName(DeploymentRole role)
        => role == DeploymentRole.Admin ? AdminName : MemberName;
}
