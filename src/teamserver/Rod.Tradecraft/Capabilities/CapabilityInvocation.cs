namespace Rod.Tradecraft.Capabilities;

/// <summary>
/// A request to dispatch one capability (architecture.md Sec 10.3): the
/// <see cref="Verb"/> to run and its <see cref="Arguments"/>. This is the
/// in-process analogue of an operator-issued task -- the dispatcher resolves the
/// module registered for <see cref="Verb"/> and hands it this invocation.
/// </summary>
/// <remarks>
/// Arguments stay a single string to match the task shape in core state
/// (<c>IssueTaskCommand.Arguments</c>, one-shot verbs carry a single argument
/// string). Structured arguments arrive when a verb needs them; the contract
/// shape is stable for it.
/// </remarks>
public sealed record CapabilityInvocation(
    string Verb,
    string Arguments)
{
    /// <summary>Builds an argumentless invocation of <paramref name="verb"/>.</summary>
    public static CapabilityInvocation Of(string verb)
        => new(verb, Arguments: string.Empty);
}
