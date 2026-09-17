using System.Diagnostics;
using System.Net;
using System.Net.Security;
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
    /// The CA-pinning server validation every TLS test client uses: chain to
    /// the pinned CA with revocation and unknown-CA relaxed (the dev authority
    /// is self-signed), and the chain's root thumbprint compared against the
    /// pinned CA so a chain to any other root fails.
    /// </summary>
    public static RemoteCertificateValidationCallback PinTo(X509Certificate2 ca)
        => (_, cert, chain, _) =>
        {
            if (cert is not X509Certificate2 leaf || chain is null)
                return false;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
            chain.ChainPolicy.ExtraStore.Add(ca);
            return chain.Build(leaf) && chain.ChainElements[^1].Certificate.Thumbprint == ca.Thumbprint;
        };

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

    /// <summary>
    /// The installed libmsquic's version string, or null when no package
    /// manager reports it (a non-packaged library, or a platform without dpkg
    /// or rpm on PATH) -- null means "cannot judge", and the caller runs
    /// rather than skips. This is what <c>QuicFactAttribute</c> reads to
    /// stand down on the known-broken releases.
    /// </summary>
    internal static string? LibMsQuicVersion()
    {
        foreach (var query in new[]
        {
            "dpkg-query -W -f=${Version} libmsquic",
            "rpm -q --qf %{VERSION} libmsquic",
        })
        {
            try
            {
                var split = query.Split(' ', 2);
                var psi = new ProcessStartInfo(split[0], split[1])
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var process = Process.Start(psi);
                if (process is null)
                    continue;
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                if (process.ExitCode == 0 && output.Length > 0)
                    return output;
            }
            catch
            {
                // Not this package manager; try the next.
            }
        }

        return null;
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

    // Hands out distinct ports for test listeners. The per-file probe this
    // replaces (bind :0, read the port, release, let Kestrel rebind later)
    // handed the same released port to two TestEnvs racing in parallel test
    // classes, and one Kestrel bind then died with "address already in use".
    // A process-wide counter never repeats a port, the probe skips ports
    // something outside this process already holds, and the range stays below
    // the ephemeral zone: the old 20k-60k march ran straight through the
    // range the kernel and Docker (the Postgres fixture's container port
    // mappings) allocate ephemeral ports from, so an ephemeral allocation
    // could land on a handed-out port inside the probe-to-bind window and
    // kill the Kestrel bind with "address already in use" -- exactly the two
    // bind failures the Sep 14 CI run hit in its first Test attempt.
    private static readonly object PortGate = new();
    // Strictly below 32768, where the Linux ephemeral port range begins.
    private const int PortCeiling = 32_760;
    private const int PortFloor = 20_000;
    private static int _nextPort = Random.Shared.Next(PortFloor, PortCeiling);

    internal static int GetFreeTcpPort()
    {
        lock (PortGate)
        {
            // Bounded so a systemic bind failure (descriptor exhaustion, say)
            // surfaces as a loud test error instead of a silent spin.
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var port = _nextPort;
                _nextPort = port >= PortCeiling ? PortFloor : port + 1;

                // Probe the any-address shape the mTLS listener binds: a
                // dual-mode [::] bind claims the IPv4 port too, while the
                // 127.0.0.1 probe this replaces stayed blind to IPv6 holders.
                using var probe = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                probe.DualMode = true;
                try
                {
                    probe.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                    return port;
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    // Held by something outside the test process; take the next.
                }
            }

            throw new InvalidOperationException("No bindable port found in 200 attempts.");
        }
    }

    // The UDP sibling of GetFreeTcpPort: the same process-wide counter and
    // probe discipline (below the ephemeral zone, skip held ports) for the
    // quic listener's datagram socket.
    internal static int GetFreeUdpPort()
    {
        lock (PortGate)
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var port = _nextPort;
                _nextPort = port >= PortCeiling ? PortFloor : port + 1;

                using var probe = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                probe.DualMode = true;
                try
                {
                    probe.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                    return port;
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    // Held by something outside the test process; take the next.
                }
            }

            throw new InvalidOperationException("No bindable UDP port found in 200 attempts.");
        }
    }

    // Pairs an enrolled leaf with its private key for the in-process beacon
    // client. Windows cannot present a certificate whose key exists only as an
    // ephemeral in-memory handle (the same SChannel constraint the teamserver's
    // server leaf works around in Rod.CoreState.Pki, and the implant at its
    // enroll), so the pair travels through a PFX import with a persisted key
    // set there. On Linux this is the plain pairing.
    internal static X509Certificate2 BeaconClientCertificate(X509Certificate2 leaf, ECDsa leafKey)
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

    // A throwaway self-signed client certificate for the no-CertificateRequest
    // probe (architecture.md Sec 8/9): the client offers it over TLS, and the
    // moment a listener asks to see a client certificate it fails the
    // chain-to-CA validation and kills the handshake -- so an exchange that
    // completes with this cert in hand proves the handshake never carried a
    // certificate request. The pair materializes through a PFX import for the
    // same SChannel presentation constraint BeaconClientCertificate documents.
    internal static X509Certificate2 OfferedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=not-an-implant", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.DefaultKeySet | X509KeyStorageFlags.Exportable);
    }
}
