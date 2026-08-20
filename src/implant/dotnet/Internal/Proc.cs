using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Rod.V1;

namespace Rod.Implant.Internal;

// Holds the process verbs (architecture.md Sec 10.1): recon.ps lists the
// live processes on the local host -- one row per process with pid, ppid,
// user, and image -- and proc.kill terminates one process by pid. Both run
// over the standard OS process APIs: the /proc filesystem on Linux and the
// Win32 toolhelp snapshot and process-token queries on Windows, the surfaces
// every OS administration guide and offensive-security curriculum documents.
//
// Argument shape:
//
//   recon.ps    (no arguments; any text is ignored, like recon.hostenum)
//   proc.kill   <pid>
//
// As with the other reference handlers, these perform no evasion, no
// obfuscation, and no destructive behavior beyond the one process the
// operator names (RESPONSIBLE-USE.md, architecture.md Sec 7). The operator
// is responsible for targeting only systems they are authorized to test.

internal static class Proc
{
    /// <summary>
    /// Lists every live process on the local host as one row per process,
    /// "<c>pid=&lt;n&gt; ppid=&lt;n&gt; user=&lt;name&gt; image=&lt;path&gt;",
    /// sorted by pid. The user is the owning account name when it resolves
    /// (the numeric uid on Linux when it does not) and "-" when the OS refuses
    /// the query, as Windows does for protected processes.
    /// </summary>
    public static (TaskOutcome Outcome, string Output) List(string arguments)
    {
        List<ProcessRow> rows;
        try
        {
            rows = OperatingSystem.IsWindows() ? Win.List() : ProcFs.List();
        }
        catch (Exception ex)
        {
            return (TaskOutcome.Failed, "recon.ps failed to list processes: " + ex.Message);
        }

        rows.Sort((a, b) => a.Pid.CompareTo(b.Pid));
        var sb = new StringBuilder();
        foreach (var row in rows)
            sb.Append("pid=").Append(row.Pid)
                .Append(" ppid=").Append(row.Ppid)
                .Append(" user=").Append(row.User)
                .Append(" image=").AppendLine(row.Image);
        return (TaskOutcome.Succeeded, sb.ToString().TrimEnd('\n'));
    }

    /// <summary>
    /// Terminates the process with the given pid. A pid that is gone, or a
    /// process the implant's account cannot signal, is Failed with the cause;
    /// the target's own name is captured before the kill so the operator's
    /// record says what ended.
    /// </summary>
    public static (TaskOutcome Outcome, string Output) Kill(string arguments)
    {
        if (!int.TryParse(arguments.Trim(), out var pid) || pid <= 0)
            return (TaskOutcome.Failed, "proc.kill expects '<pid>'");

        try
        {
            using var process = Process.GetProcessById(pid);
            var image = process.ProcessName;
            process.Kill();
            // SIGKILL / TerminateProcess cannot be blocked, but the exit is
            // asynchronous; a bounded wait keeps "terminated" honest.
            process.WaitForExit(5000);
            return (TaskOutcome.Succeeded, $"terminated pid {pid} ({image})");
        }
        catch (ArgumentException)
        {
            return (TaskOutcome.Failed, $"no such process: {pid}");
        }
        catch (Exception ex)
        {
            return (TaskOutcome.Failed, $"kill {pid}: {ex.Message}");
        }
    }

    private sealed record ProcessRow(int Pid, int Ppid, string User, string Image);

    // The Linux listing: /proc is the documented kernel process API. Each
    // numeric directory is one process; status carries the name, parent pid,
    // and real uid, and cmdline carries the invoked image. A process that
    // exits between the directory listing and the reads is skipped, not
    // reported -- the listing is a snapshot, not a ledger.
    private static class ProcFs
    {
        public static List<ProcessRow> List()
        {
            if (!Directory.Exists("/proc"))
                throw new DirectoryNotFoundException(
                    "no /proc filesystem; recon.ps needs Linux /proc or Windows");

            var users = LoadPasswd();
            var rows = new List<ProcessRow>();
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir.AsSpan()), out var pid))
                    continue;

                string? name = null, uidText = null;
                int ppid = 0;
                foreach (var line in ReadLines(Path.Combine(dir, "status")))
                {
                    if (line.StartsWith("Name:", StringComparison.Ordinal))
                        name = line["Name:".Length..].Trim();
                    else if (line.StartsWith("PPid:", StringComparison.Ordinal))
                        int.TryParse(line["PPid:".Length..].Trim(), out ppid);
                    else if (line.StartsWith("Uid:", StringComparison.Ordinal))
                        uidText = line["Uid:".Length..].Trim().Split(' ')[0];
                    if (name is not null && uidText is not null && ppid > 0)
                        break;
                }
                if (name is null || uidText is null)
                    continue;

                var user = uint.TryParse(uidText, out var uid) && users.TryGetValue(uid, out var userName)
                    ? userName
                    : uidText;
                rows.Add(new ProcessRow(pid, ppid, user, ImageOf(dir, name)));
            }
            return rows;
        }

        // The invoked executable is cmdline's first NUL-separated token (the
        // full path for most binaries); kernel threads have an empty cmdline
        // and keep the comm name from status.
        private static string ImageOf(string dir, string comm)
        {
            try
            {
                var cmdline = File.ReadAllBytes(Path.Combine(dir, "cmdline"));
                var end = Array.IndexOf(cmdline, (byte)0);
                if (end < 0)
                    end = cmdline.Length;
                if (end > 0)
                    return Encoding.UTF8.GetString(cmdline, 0, end);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return comm;
        }

        // uid -> account name from /etc/passwd, the documented account file.
        // Names that live in a directory service stay numeric; the listing
        // reports what the local system can resolve without a network call.
        private static Dictionary<uint, string> LoadPasswd()
        {
            var map = new Dictionary<uint, string>();
            try
            {
                foreach (var line in File.ReadLines("/etc/passwd"))
                {
                    var fields = line.Split(':');
                    if (fields.Length >= 3 && uint.TryParse(fields[2], out var uid))
                        map[uid] = fields[0];
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return map;
        }

        // /proc files report Length 0; ReadLines would misbehave on them, so
        // read the bytes and decode. The files are small (a few KiB).
        private static IEnumerable<string> ReadLines(string path)
        {
            string body;
            try
            {
                body = File.ReadAllText(path);
            }
            catch (IOException)
            {
                yield break;
            }
            catch (UnauthorizedAccessException)
            {
                yield break;
            }
            foreach (var line in body.Split('\n'))
                yield return line;
        }
    }

    // The Windows listing: CreateToolhelp32Snapshot over the process list (the
    // API Task Manager's view is built on), with the owning account resolved
    // per pid through the process token -- OpenProcess +
    // OpenProcessToken + GetTokenInformation(TokenUser) + LookupAccountSid,
    // the documented Win32 way to ask "who runs this".
    [SupportedOSPlatform("windows")]
    private static class Win
    {
        private const uint Th32CsSnapProcess = 2;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint TokenQuery = 0x0008;

        public static List<ProcessRow> List()
        {
            var rows = new List<ProcessRow>();
            var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
                throw new InvalidOperationException("CreateToolhelp32Snapshot failed");

            try
            {
                var entry = new ProcessEntry32W { Size = (uint)Marshal.SizeOf<ProcessEntry32W>() };
                if (!Process32FirstW(snapshot, ref entry))
                    return rows;
                do
                {
                    var pid = (int)entry.ProcessId;
                    rows.Add(new ProcessRow(
                        pid,
                        (int)entry.ParentProcessId,
                        UserOf(pid),
                        ExeFileName(in entry)));
                }
                while (Process32NextW(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }
            return rows;
        }

        // Resolves the account running pid as "DOMAIN\user"; "-" when any step
        // is refused (a protected or elevated process an ordinary token cannot
        // open) so one guarded row never hides the rest of the listing.
        private static string UserOf(int pid)
        {
            var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (process == IntPtr.Zero)
                return "-";
            try
            {
                if (!OpenProcessToken(process, TokenQuery, out var token))
                    return "-";
                try
                {
                    return TokenUserName(token);
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }

        private static string TokenUserName(IntPtr token)
        {
            GetTokenInformation(token, 1, IntPtr.Zero, 0, out var needed);
            if (needed == 0)
                return "-";
            var info = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetTokenInformation(token, 1, info, needed, out _))
                    return "-";
                // TOKEN_USER is SID_AND_ATTRIBUTES: the SID pointer, then the
                // attributes dword.
                var sid = Marshal.ReadIntPtr(info);
                var name = new StringBuilder(256);
                var domain = new StringBuilder(256);
                var nameLen = (uint)name.Capacity;
                var domainLen = (uint)domain.Capacity;
                if (!LookupAccountSidW(IntPtr.Zero, sid, name, ref nameLen, domain, ref domainLen, out _))
                    return "-";
                return domain.Length == 0 ? name.ToString() : domain + "\\" + name;
            }
            finally
            {
                Marshal.FreeHGlobal(info);
            }
        }

        // Reads the entry's NUL-terminated szExeFile out of its inline char
        // buffer. UnmanagedType.ByValTString is gone from this SDK, so the
        // buffer is a fixed InlineArray and the decode is explicit.
        private static string ExeFileName(in ProcessEntry32W entry)
        {
            var sb = new StringBuilder(64);
            for (var i = 0; i < ExeFileChars; i++)
            {
                var c = entry.ExeFile[i];
                if (c == '\0')
                    break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private const int ExeFileChars = 260;

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessEntry32W
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public nuint DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            public ExeFileBuffer ExeFile;

            // MAX_PATH wide chars inline, the native szExeFile tail.
            [InlineArray(ExeFileChars)]
            internal struct ExeFileBuffer
            {
                internal char C0;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32W entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32W entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(
            IntPtr token, int infoClass, IntPtr info, uint infoLength, out uint returnLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupAccountSidW(
            IntPtr system,
            IntPtr sid,
            StringBuilder name,
            ref uint nameLength,
            StringBuilder domain,
            ref uint domainLength,
            out int use);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
