namespace Rod.CoreState.ShellSessions;

/// <summary>
/// What the teamserver could tell about a caught shell's host and shell
/// program (architecture.md Sec 8, the shellcatch transport). A dumb shell
/// announces nothing, so this is a server-side guess from the first output
/// it produced -- the banner a shell prints, or the answers to a probe --
/// never a claim by the peer. It picks the launcher family an upgrade
/// renders and the encoding the console decodes with; Unknown keeps every
/// choice open until output says more.
/// </summary>
public enum ShellOsGuess
{
    /// <summary>
    /// Nothing recognizable yet: the shell produced no output, or output
    /// no rule matched. Commands still pass through; an upgrade render
    /// waits for a guess or asks the operator.
    /// </summary>
    Unknown,

    /// <summary>Windows running cmd.exe -- the code-page-aware console.</summary>
    WindowsCmd,

    /// <summary>Windows running PowerShell.</summary>
    WindowsPowerShell,

    /// <summary>
    /// A Unix shell (sh/bash/zsh and kin share the handling here: the
    /// byte stream is UTF-8 in practice, and the upgrade launcher family
    /// is the same).
    /// </summary>
    UnixShell,
}
