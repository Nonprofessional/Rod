namespace Rod.CoreState;

// Strongly typed identifiers for the engagement core (architecture.md Sec 3).
// Wrapping the underlying Guid in a distinct type stops an OperatorId being
// passed where an EngagementId is expected, and keeps the idiom uniform across
// entities added in later milestones.

/// <summary>Identifies a global operator identity (one human user).</summary>
public readonly record struct OperatorId(Guid Value)
{
    public static OperatorId New() => new(Guid.NewGuid());

    /// <summary>
    /// The null operator, for system-initiated events no human operator
    /// performed (e.g. a staleness sweep closing a session).
    /// </summary>
    public static OperatorId Empty => new(Guid.Empty);

    public override string ToString() => Value.ToString("N");
}

/// <summary>Identifies an engagement -- the unit of isolation and authorization.</summary>
public readonly record struct EngagementId(Guid Value)
{
    public static EngagementId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");

    /// <summary>
    /// Parses an engagement id from its string form. Accepts both the compact
    /// "N" format produced by <see cref="ToString"/> and the hyphenated Guid
    /// form; returns false on anything else.
    /// </summary>
    public static bool TryParse(string? text, out EngagementId id)
    {
        if (Guid.TryParse(text, out var guid))
        {
            id = new EngagementId(guid);
            return true;
        }

        id = default;
        return false;
    }
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

    /// <summary>
    /// Parses an implant id from its string form. Accepts the compact "N"
    /// format produced by <see cref="ToString"/> and the hyphenated Guid form;
    /// returns false on anything else. Used to read the binding back out of a
    /// certificate subject (architecture.md Sec 9).
    /// </summary>
    public static bool TryParse(string? text, out ImplantId id)
    {
        if (Guid.TryParse(text, out var guid))
        {
            id = new ImplantId(guid);
            return true;
        }

        id = default;
        return false;
    }
}

/// <summary>
/// Identifies a task -- a single dispatched verb to an implant, owned by an
/// operator and scoped to an engagement (architecture.md Sec 10.3). The task is
/// the unit operators task, implants execute, and the audit trail attributes.
/// </summary>
public readonly record struct TaskId(Guid Value)
{
    public static TaskId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");

    /// <summary>
    /// Parses a task id from its string form. Accepts both the compact "N"
    /// format produced by <see cref="ToString"/> and the hyphenated Guid form;
    /// returns false on anything else.
    /// </summary>
    public static bool TryParse(string? text, out TaskId id)
    {
        if (Guid.TryParse(text, out var guid))
        {
            id = new TaskId(guid);
            return true;
        }

        id = default;
        return false;
    }
}

/// <summary>
/// Identifies a session -- one connected implant execution context in its
/// engagement (architecture.md Sec 4.1, Sec 10.3). An implant opens a session on
/// a successful handshake and closes it when the stream ends; an implant may have
/// many sessions over its life (reconnects, flaps). Disposable with the
/// engagement.
/// </summary>
public readonly record struct SessionId(Guid Value)
{
    public static SessionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");

    /// <summary>
    /// Parses a session id from its string form. Accepts both the compact "N"
    /// format produced by <see cref="ToString"/> and the hyphenated Guid form;
    /// returns false on anything else.
    /// </summary>
    public static bool TryParse(string? text, out SessionId id)
    {
        if (Guid.TryParse(text, out var guid))
        {
            id = new SessionId(guid);
            return true;
        }

        id = default;
        return false;
    }
}
