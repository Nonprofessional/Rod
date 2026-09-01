using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Rod.Integration.Tests;

/// <summary>
/// Shared test support. The in-tree .NET build unit and the .NET reference
/// implant end-to-end test drive the real dotnet toolchain and skip (not fail)
/// when dotnet is not on PATH, so the suite stays green in environments without
/// it while exercising the real slice where it is present.
/// </summary>
internal static class TestSupport
{
    /// <summary>
    /// True when the dotnet SDK is reachable on PATH. The in-tree .NET build/test
    /// path requires it to publish and run the reference .NET implant; tests that
    /// do skip via this check.
    /// </summary>
    public static bool DotNetAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet", "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            process.WaitForExit(15000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // Builds a "<start>-<end>" port range for a recon.portscan argument that
    // covers a tight window around the given open port, so the scan finishes
    // promptly while still reporting the port as open. Clamped to [1, 65535].
    // (Relocated from the Go reference implant tests when that implant moved
    // out-of-tree, ADR 0009.)
    internal static string PortScanRangeAround(int port)
    {
        var start = Math.Max(1, port - 2);
        var end = Math.Min(65535, port + 2);
        return $"{start}-{end}";
    }

    // A deadline for beacon-stream waits. A lost dispatch frame must fail
    // the test with a stack in ninety seconds, not suspend it forever: the
    // Sep 1 hangs were exactly that -- a test awaiting a server frame no
    // thread would ever produce, invisible to stacks and fatal to the run.
    // Ninety seconds sits far above any healthy exchange and far below the
    // blame-hang window; the per-call token is never disposed, which is fine
    // at test scale (a timer per await, collected with its token).
    internal static CancellationToken BeaconDeadline()
        => new CancellationTokenSource(TimeSpan.FromSeconds(90)).Token;

    // Hands out distinct loopback ports for test listeners. The per-file probe
    // this replaces (bind :0, read the port, release, let Kestrel rebind later)
    // handed the same released port to two TestEnvs racing in parallel test
    // classes, and one Kestrel bind then died with "address already in use".
    // A process-wide counter never repeats a port; the probe only skips ports
    // something outside this process already holds.
    private static readonly object PortGate = new();
    private static int _nextPort = Random.Shared.Next(20_000, 40_000);

    internal static int GetFreeTcpPort()
    {
        lock (PortGate)
        {
            // Bounded so a systemic bind failure (descriptor exhaustion, say)
            // surfaces as a loud test error instead of a silent spin.
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var port = _nextPort;
                _nextPort = port >= 60_000 ? 20_000 : port + 1;

                var listener = new TcpListener(IPAddress.Loopback, port);
                try
                {
                    listener.Start();
                    return port;
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    // Held by something outside the test process; take the next.
                }
                finally
                {
                    listener.Stop();
                }
            }

            throw new InvalidOperationException("No bindable loopback port found in 200 attempts.");
        }
    }

    // Pairs an enrolled leaf with its private key for the in-process beacon
    // client. Windows cannot present a certificate whose key exists only as an
    // ephemeral in-memory handle (the same SChannel constraint the teamserver's
    // server leaf works around in Rod.CoreState.Pki, and the implant at its
    // enroll), so the pair travels through a PFX import with a persisted key
    // set there. On Linux this is the plain pairing.
    internal static X509Certificate2 BeaconClientCertificate(X509Certificate2 leaf, RSA leafKey)
    {
        if (leaf.HasPrivateKey)
            return leaf;

        var paired = leaf.CopyWithPrivateKey(leafKey);
        if (!OperatingSystem.IsWindows())
            return paired;

        using (paired)
        {
            return X509CertificateLoader.LoadPkcs12(
                paired.Export(X509ContentType.Pfx),
                (string?)null,
                X509KeyStorageFlags.DefaultKeySet | X509KeyStorageFlags.Exportable);
        }
    }
}
