namespace Rod.CoreState.Engagements;

/// <summary>
/// A member's role within an engagement (architecture.md Sec 3, glossary).
/// Roles gate what a member may do in that engagement; access derives entirely
/// from membership, never from a global grant.
/// </summary>
public enum Role
{
    /// <summary>
    /// Creates the engagement and cannot be removed; the single point of
    /// accountability for the operation.
    /// </summary>
    Owner = 0,

    /// <summary>Leads day-to-day operation of the engagement.</summary>
    Lead = 1,

    /// <summary>Tasks implants and performs operational work.</summary>
    Operator = 2,

    /// <summary>Read-only visibility into the engagement.</summary>
    Observer = 3,
}
