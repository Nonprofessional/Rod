namespace Rod.CoreState.Operators;

/// <summary>
/// A global human identity and authorized user of the platform (glossary). An
/// operator authenticates with a handle and password; the handle is the
/// account's whole public identity -- every surface (login, rosters, presence,
/// audit) renders it. The account carries one deployment-level distinction --
/// its <see cref="DeploymentRole"/>: the bootstrap-seeded admin holds the
/// deployment's management surface, and every console-created account is a
/// member (Sec 4.5). Everything else an operator may see and do lives per
/// engagement, as the memberships its owners granted (architecture.md Sec 3).
/// A disabled operator authenticates nowhere (login, cookie session, API token
/// all refuse) until enabled again.
/// </summary>
public sealed class Operator
{
    public OperatorId Id { get; }
    public string Handle { get; }
    public DateTimeOffset CreatedAt { get; }
    public bool Disabled { get; }
    public DeploymentRole DeploymentRole { get; }

    public Operator(
        OperatorId id,
        string handle,
        DateTimeOffset createdAt,
        bool disabled = false,
        DeploymentRole deploymentRole = DeploymentRole.Member)
    {
        if (string.IsNullOrWhiteSpace(handle))
            throw new ArgumentException("Operator handle is required.", nameof(handle));
        if (deploymentRole is not (DeploymentRole.Admin or DeploymentRole.Member))
            throw new ArgumentOutOfRangeException(
                nameof(deploymentRole), "Operator deployment role is not a known role.");

        Id = id;
        Handle = handle.Trim();
        CreatedAt = createdAt;
        Disabled = disabled;
        DeploymentRole = deploymentRole;
    }

    /// <summary>
    /// Factory for a newly registered operator. The deployment role is a
    /// required argument so no registration path can land on a default: the
    /// bootstrap seed states admin, the console states member.
    /// </summary>
    public static Operator Register(
        OperatorId id,
        string handle,
        DateTimeOffset createdAt,
        DeploymentRole deploymentRole)
        => new(id, handle, createdAt, deploymentRole: deploymentRole);

    /// <summary>
    /// This operator with its disabled flag flipped -- the immutable-entity
    /// way a disable or enable lands in the store. The flag gates
    /// authentication only: the deployment role rides on the account, and the
    /// engagements an operator can reach ride on their memberships, both
    /// untouched by the switch.
    /// </summary>
    public Operator WithDisabled(bool disabled)
        => new(Id, Handle, CreatedAt, disabled, DeploymentRole);
}
