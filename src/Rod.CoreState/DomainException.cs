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
