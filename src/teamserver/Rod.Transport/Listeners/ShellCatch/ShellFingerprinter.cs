using Rod.CoreState.ShellSessions;

namespace Rod.Transport.Listeners.ShellCatch;

/// <summary>
/// The server-side guess at a caught shell's host and shell program, read
/// from the connection's first output (architecture.md Sec 8, the
/// shellcatch transport). A dumb shell announces nothing itself, so the
/// fingerprint is what the banner gives up: the welcome text a shell prints,
/// or a prompt shape. Deliberately a pure function of that text -- the
/// service decides when and whether to nudge the connection for more; this
/// type only interprets what arrived, which keeps the rules pinned by direct
/// tests.
///
/// The rules are the same ones the classic catchers use: Windows shells
/// identify themselves in their banner, PowerShell adds its <c>PS</c>
/// prompt, and a Unix shell shows a prompt ending in <c>$</c> or
/// <c>#</c>. A guess picks the upgrade launcher family and nothing more --
/// an Unknown never blocks interaction, it only leaves the choices open.
/// </summary>
public static class ShellFingerprinter
{
    /// <summary>
    /// Interprets the connection's first output. Returns
    /// <see cref="ShellOsGuess.Unknown"/> when nothing recognizable is
    /// there.
    /// </summary>
    public static ShellOsGuess Guess(ReadOnlySpan<char> banner)
    {
        if (banner.IsEmpty)
            return ShellOsGuess.Unknown;

        // The Windows shells say who they are before any prompt.
        var saysWindows = banner.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
            || banner.Contains("Microsoft Windows", StringComparison.OrdinalIgnoreCase)
            || banner.Contains("Windows PowerShell", StringComparison.OrdinalIgnoreCase);
        var saysPowerShell = banner.Contains("PowerShell", StringComparison.OrdinalIgnoreCase)
            || PromptLooksLikePowerShell(banner);
        if (saysWindows || saysPowerShell)
            return saysPowerShell ? ShellOsGuess.WindowsPowerShell : ShellOsGuess.WindowsCmd;

        // A Unix shell's last line is a prompt ending in $ or # (the root
        // marker); the classic zsh decoration says the same thing louder.
        if (banner.Contains('㉿'))
            return ShellOsGuess.UnixShell;
        var lastLine = LastNonEmptyLine(banner);
        if (lastLine.Length > 0)
        {
            var endsPrompt = lastLine[^1] is '$' or '#' || lastLine.EndsWith(">@".AsSpan(), StringComparison.Ordinal);
            var namesShell = banner.Contains("bash", StringComparison.OrdinalIgnoreCase)
                || banner.Contains("zsh", StringComparison.OrdinalIgnoreCase)
                || banner.Contains("sh-", StringComparison.Ordinal);
            if (endsPrompt || namesShell)
                return ShellOsGuess.UnixShell;
        }

        return ShellOsGuess.Unknown;
    }

    // PowerShell's prompt is "PS <drive>:<path>>" -- a line starting "PS "
    // followed by a drive letter is its shape; cmd's banner is caught by the
    // Microsoft rules above and never reaches here.
    private static bool PromptLooksLikePowerShell(ReadOnlySpan<char> banner)
    {
        foreach (var line in banner.EnumerateLines())
        {
            if (line.StartsWith("PS ", StringComparison.Ordinal) && line.Length > 3
                && char.IsAsciiLetter(line[3]) && line.Length > 4 && line[4] == ':')
                return true;
        }

        return false;
    }

    private static ReadOnlySpan<char> LastNonEmptyLine(ReadOnlySpan<char> banner)
    {
        ReadOnlySpan<char> last = [];
        foreach (var line in banner.EnumerateLines())
        {
            var trimmed = line.Trim();
            if (!trimmed.IsEmpty)
                last = trimmed;
        }

        return last;
    }
}
