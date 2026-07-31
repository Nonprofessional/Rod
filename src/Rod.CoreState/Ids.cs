namespace Rod.CoreState;

// Strongly typed identifiers for the engagement core (architecture.md Sec 3).
// Wrapping the underlying Guid in a distinct type stops an OperatorId being
// passed where an EngagementId is expected, and keeps the idiom uniform across
// entities added in later milestones.

/// <summary>Identifies a global operator identity (one human user).</summary>
public readonly record struct OperatorId(Guid Value)
{
    public static OperatorId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

/// <summary>Identifies an engagement -- the unit of isolation and authorization.</summary>
public readonly record struct EngagementId(Guid Value)
{
    public static EngagementId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

/// <summary>Identifies a stager token minted for an engagement.</summary>
public readonly record struct StagerTokenId(Guid Value)
{
    public static StagerTokenId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Identifies an implant -- a short-lived, disposable payload enrolled into one
/// engagement (architecture.md Sec 5). Ephemeral per engagement; disposable with
/// the operation.
/// </summary>
public readonly record struct ImplantId(Guid Value)
{
    public static ImplantId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}
