using System.Diagnostics;
using Xunit;

namespace Rod.Integration.Tests;

/// <summary>
/// The QUIC acceptance's environment guard, the shape
/// <see cref="DotNetFactAttribute"/> gives the SDK-dependent tests: the
/// runner's package source floated libmsquic to a release whose TLS
/// handshake System.Net.Quic cannot complete (2.6.0 and 2.6.1 as of
/// 2026-09; local runs against the 2.4 line stay green, and the
/// 24.04 package source carries nothing else to pin), so on a known-broken
/// release the tests skip with the reason instead of reddening a lane.
/// Every other version -- including a future fixed 2.6 -- runs them.
/// </summary>
public sealed class QuicFactAttribute : FactAttribute
{
    public const string SkipReason =
        "libmsquic {0} carries the QUIC TLS handshake regression; the acceptance runs on any other release.";

    public QuicFactAttribute()
    {
        var version = TestSupport.LibMsQuicVersion();
        if (version is not null && IsKnownBroken(version))
            Skip = string.Format(SkipReason, version);
    }

    // The exact releases the regression shipped in, not the whole line: a
    // fixed 2.6 must run these tests the day it installs.
    private static bool IsKnownBroken(string version)
        => version.StartsWith("2.6.0", StringComparison.Ordinal)
            || version.StartsWith("2.6.1", StringComparison.Ordinal);
}
