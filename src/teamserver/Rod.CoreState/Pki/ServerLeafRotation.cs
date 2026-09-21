using System.Security.Cryptography.X509Certificates;

namespace Rod.CoreState.Pki;

/// <summary>
/// The renewal rule both certificate authorities apply to the listener
/// server leaf: reuse the cached certificate until it enters the renewal
/// window before its NotAfter, then mint a fresh one. The authority lives
/// as long as the process and an engagement can outlast a 30-day leaf,
/// while the implant's rustls client (full webpki validation, the baked CA
/// as the only root) refuses an expired certificate -- without the rule a
/// teamserver that runs past the leaf's lifetime would lose every https
/// contact.
/// </summary>
internal static class ServerLeafRotation
{
    /// <summary>How far before NotAfter a cached leaf is replaced.</summary>
    internal static readonly TimeSpan RenewalWindow = TimeSpan.FromDays(1);

    /// <summary>True when the leaf is past or inside the renewal window.</summary>
    internal static bool Due(X509Certificate2 leaf, DateTimeOffset now)
        => now >= leaf.NotAfter - RenewalWindow;
}
