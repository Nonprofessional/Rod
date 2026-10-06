namespace Rod.CoreState.Implants;

/// <summary>
/// Decides whether a task verb is sensitive -- one that never fires without a
/// human in the loop. The task-issuance gates (<see cref="ITaskCapabilityResolver"/>
/// for dispatchability, ROE for scope) are separate concerns; this is the
/// sensitivity axis, consulted by the surfaces that act unattended: the
/// automation engine refuses to build a rule on a sensitive verb
/// (architecture.md Sec 10.4), and the sensitive-verb approval workflow the
/// roadmap carries will queue human tasking on the same answer.
/// </summary>
/// <remarks>
/// The port lives in core state (the inner ring every layer may reach) and
/// ships with a static fallback default, <see cref="DefaultSensitiveVerbPolicy"/>.
/// The authoritative implementation is registry-backed and lives in the
/// tradecraft layer, where each verb's descriptor carries its OPSEC metadata;
/// the composition root swaps it in. Registry-backed implementations may only
/// widen the sensitive set relative to the fallback, never narrow it -- the
/// same one-direction rule the registry-backed capability resolver follows on
/// the dispatch axis, in reverse.
/// </remarks>
public interface ISensitiveVerbPolicy
{
    /// <summary>
    /// Whether <paramref name="verb"/> is sensitive. Matching is
    /// case-insensitive, the rule every verb gate honors. A verb with no
    /// registration and no fallback listing is not sensitive: it is simply a
    /// verb nobody described yet, and the dispatch gate is what stops it.
    /// </summary>
    bool IsSensitive(string verb);
}
