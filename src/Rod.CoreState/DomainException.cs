namespace Rod.CoreState;

/// <summary>
/// Base type for all engagement-domain invariant violations. Domain rules throw
/// a subclass so callers (use-case code, HTTP endpoints) can distinguish a
/// client-correctable error from an unexpected failure.
/// </summary>
public abstract class DomainException : InvalidOperationException
{
    protected DomainException(string message)
        : base(message)
    {
    }

    protected DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>An engagement invariant was violated -- e.g. removing its owner.</summary>
public sealed class EngagementDomainException : DomainException
{
    public EngagementDomainException(string message)
        : base(message)
    {
    }
}

/// <summary>A stager-token operation violated its rules -- e.g. unknown engagement.</summary>
public class StagerTokenException : DomainException
{
    public StagerTokenException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Why a stager-token redeem failed. Carried on
/// <see cref="StagerTokenRedeemException"/> so the enroll endpoint can map each
/// reason to a distinct wire status code (architecture.md Sec 9) without the
/// core depending on the wire protocol.
/// </summary>
public enum StagerTokenRedeemReason
{
    /// <summary>No token matched the presented secret (unknown, malformed, or wrong).</summary>
    Unknown,

    /// <summary>The matched token had passed its hard expiry.</summary>
    Expired,

    /// <summary>The matched token had no remaining uses.</summary>
    Spent,
}

/// <summary>
/// A stager-token redeem was refused -- the secret matched no token, or the
/// matched token was expired or spent. <see cref="Reason"/> is the actionable
/// cause; the caller maps it to a wire status.
/// </summary>
public sealed class StagerTokenRedeemException : StagerTokenException
{
    public StagerTokenRedeemReason Reason { get; }

    public StagerTokenRedeemException(StagerTokenRedeemReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }
}

/// <summary>
/// Why a handshake was refused. Carried on <see cref="HandshakeException"/> so
/// the transport endpoint can map each reason to a distinct wire status
/// (architecture.md Sec 9) without the core depending on the wire protocol --
/// mirroring <see cref="StagerTokenRedeemReason"/>.
/// </summary>
public enum HandshakeReason
{
    /// <summary>The implant id matched no enrolled implant.</summary>
    UnknownImplant,

    /// <summary>The presented protocol version is not one the server speaks.</summary>
    VersionMismatch,

    /// <summary>
    /// The certificate binding did not match the implant's enrolled engagement
    /// (architecture.md Sec 9 mTLS identity check).
    /// </summary>
    IdentityMismatch,

    /// <summary>
    /// The implant's baked-in kill date has passed (architecture.md Sec 7). A
    /// lost implant self-terminates at its kill date; the teamserver mirrors that
    /// here by refusing to open a session for an implant past its kill date.
    /// </summary>
    KillDateExpired,
}

/// <summary>
/// A handshake was refused -- the implant is unknown, the protocol version is
/// unsupported, or the certificate-vs-identity check failed.
/// <see cref="Reason"/> is the actionable cause; the caller maps it to a wire
/// status.
/// </summary>
public sealed class HandshakeException : DomainException
{
    public HandshakeReason Reason { get; }

    public HandshakeException(HandshakeReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }
}

/// <summary>
/// Why a task issuance was refused. Carried on <see cref="TaskRejectedException"/>
/// so the task endpoint can map each reason to a distinct HTTP status
/// (architecture.md Sec 10.3) without the core depending on the transport --
/// mirroring <see cref="HandshakeReason"/> and <see cref="StagerTokenRedeemReason"/>.
/// </summary>
public enum TaskRejectionReason
{
    /// <summary>
    /// The verb is not in the implant's class reduced verb set
    /// (architecture.md Sec 5.2). The implant's class is fixed at enrollment;
    /// this is the per-class capability gate.
    /// </summary>
    UnsupportedVerbForClass,

    /// <summary>The implant id matched no enrolled implant.</summary>
    UnknownImplant,

    /// <summary>
    /// The implant belongs to a different engagement than the task
    /// (architecture.md Sec 3 -- cross-engagement access is impossible by
    /// construction).
    /// </summary>
    ImplantEngagementMismatch,
}

/// <summary>
/// A task issuance was refused -- the verb is outside the implant's class
/// reduced verb set, the implant is unknown, or it belongs to another
/// engagement. <see cref="Reason"/> is the actionable cause; the caller maps it
/// to a wire status.
/// </summary>
public sealed class TaskRejectedException : DomainException
{
    public TaskRejectionReason Reason { get; }

    public TaskRejectedException(TaskRejectionReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }
}
