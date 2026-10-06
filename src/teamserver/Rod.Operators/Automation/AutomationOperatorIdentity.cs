using Rod.CoreState;
using Rod.CoreState.Operators;

namespace Rod.Operators.Automation;

/// <summary>
/// The synthetic operator every automation firing attributes to
/// (architecture.md Sec 10.4). A fixed, well-known id so a firing's audit
/// facts read identically across restarts and stores even before the
/// operator row is seeded; the engine seeds the row idempotently at startup
/// (handle <c>automation</c>, no credential -- the account cannot log in,
/// and nothing about it is provisionable by configuration).
/// </summary>
public static class AutomationOperatorIdentity
{
    /// <summary>The stable id automation's tasking and audit attribute to.</summary>
    public static readonly OperatorId OperatorId = new(new Guid("7c5e2a10-94b3-4f6d-a1e8-3b8d0c2f4a97"));

    public const string Handle = "automation";

    public const string DisplayName = "Automation Engine";
}
