using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Rod.V1;

namespace Rod.Implant.Internal;

// Dispatches the capability verbs the reference implant advertises
// (architecture.md Sec 10): the shell.exec core verb and the recon.portscan /
// recon.hostenum / recon.service recon verbs. The runner is the dispatch point
// future verbs (file.push, probe.read, ...) extend.
//
// This is a benign reference runner: it shells out to the platform shell for the
// one core verb and reports output. It performs no evasion, no obfuscation, and
// no destructive behavior (RESPONSIBLE-USE.md, architecture.md Sec 7); the
// operator is responsible for targeting only systems they are authorized to test
// (RESPONSIBLE-USE.md).

/// <summary>
/// Dispatches capability verbs. Safe for concurrent use: each Dispatch call runs
/// an independent process or its own socket set.
/// </summary>
internal sealed class Runner
{
    // Per-port dial timeout for the network-touching recon verbs. Short so a
    // wide port range finishes promptly; long enough that a reachable port on a
    // quiet host completes its handshake. Mirrors the Go implant's dial timeout.
    private static readonly TimeSpan DialTimeout = TimeSpan.FromMilliseconds(300);

    // How long a service-probe waits for a banner after connecting.
    private static readonly TimeSpan BannerTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Runs <paramref name="verb"/> against <paramref name="arguments"/> and
    /// returns the wire outcome plus the captured output (combined
    /// stdout/stderr). An unknown verb reports Failed with a clear message rather
    /// than throwing, so the operator sees the cause.
    /// </summary>
    public (TaskOutcome Outcome, string Output) Dispatch(string verb, string arguments)
    {
        return verb switch
        {
            "shell.exec" => ShellExec(arguments),
            "recon.portscan" => PortScan(arguments),
            "recon.hostenum" => HostEnum(arguments),
            "recon.service" => ServiceProbe(arguments),
            _ => (TaskOutcome.Failed, "unknown verb: " + verb),
        };
    }

    // Runs the argument string through the platform shell and returns the combined
    // output. A non-zero exit is a Failed outcome with the output captured so the
    // operator sees the cause; the shell itself failing to start is also Failed.
    private static (TaskOutcome, string) ShellExec(string command)
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

    // recon.portscan dials each TCP port in "start-end" on the host and reports
    // one line per open port ("<host>:<port> open"). Arguments are
    // "<host> <start-end>". Malformed arguments yield Failed; a closed range
    // yields Succeeded with empty output, the same convention as the Go implant.
    private static (TaskOutcome, string) PortScan(string arguments)
    {
        if (!TryParseScanArgs(arguments, out var host, out var startPort, out var endPort))
            return (TaskOutcome.Failed, "recon.portscan expects '<host> <start-end>'");

        var lines = new List<string>();
        for (var port = startPort; port <= endPort; port++)
        {
            if (IsPortOpen(host, port))
                lines.Add($"{host}:{port} open");
        }
        return (TaskOutcome.Succeeded, string.Join("\n", lines));
    }

    // recon.hostenum reports local host facts: hostname, OS/arch, and the
    // non-loopback unicast addresses on each interface. It introspects the
    // running host and never probes a remote one, so the optional argument is
    // informational only.
    private static (TaskOutcome, string) HostEnum(string _)
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
    private static (TaskOutcome, string) ServiceProbe(string arguments)
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
    // use sh -c; Windows uses cmd /c -- the same split as the Go implant's
    // platformShell.
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

