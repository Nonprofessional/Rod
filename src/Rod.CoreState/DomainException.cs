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
public sealed class StagerTokenException : DomainException
{
    public StagerTokenException(string message)
        : base(message)
    {
    }
}
