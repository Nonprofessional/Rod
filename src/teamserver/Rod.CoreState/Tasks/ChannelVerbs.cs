using Rod.CoreState.Implants;

namespace Rod.CoreState.Tasks;

/// <summary>
/// The verbs whose tasks run as live channels rather than one-shot round
/// trips (architecture.md Sec 10.3, the streaming task shape). A channel task
/// is dispatched like any other -- the same signed TaskRequest, the same
/// queue -- but its TaskRequest opens a channel instead of a completion:
/// output streams back as ChannelOutput frames and operator input flows down
/// as ChannelInput frames on the beacon stream that carried the task, until
/// the final TaskResult closes it.
///
/// The set lives in core state because two outer layers need the same answer:
/// the DNS bridge must not claim a channel task (a datagram poll carries no
/// stream to run a channel on), and the operator input route admits input
/// only for a task whose verb is one of these. It is the wire-contract
/// companion of <see cref="ImplantClassCapabilities"/> -- the class table
/// says which implant may run a verb, this says how the verb's task behaves
/// once issued.
/// </summary>
public static class ChannelVerbs
{
    /// <summary>
    /// The interactive shell: <c>shell.exec</c>'s streaming shape. The
    /// arguments string is an optional initial command; the channel then
    /// carries the operator's input and the shell's output until the operator
    /// closes stdin or the shell exits.
    /// </summary>
    public const string ShellInteract = "shell.interact";

    private static readonly string[] All = { ShellInteract };

    /// <summary>
    /// Whether <paramref name="verb"/>'s tasks run as live channels. Verb
    /// matching is case-insensitive, the same rule the class table and the
    /// registries honor.
    /// </summary>
    public static bool IsChannelVerb(string? verb)
    {
        if (string.IsNullOrWhiteSpace(verb))
            return false;
        foreach (var candidate in All)
        {
            if (string.Equals(candidate, verb, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
