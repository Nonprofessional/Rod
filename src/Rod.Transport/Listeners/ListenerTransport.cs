namespace Rod.Transport.Listeners;

/// <summary>
/// The C2 transport a <see cref="Listener"/> terminates (architecture.md Sec 8).
/// M2.2 ships <see cref="Http"/> and <see cref="Mtls"/>; DNS, SMB, and TCP are the
/// remaining transports the architecture calls out, added in later milestones.
/// </summary>
public enum ListenerTransport
{
    /// <summary>
    /// Plain HTTP(S). The operator API and the implant enrollment endpoint run here;
    /// no client certificate is required. The mTLS identity check
    /// (architecture.md Sec 9) does not apply on this transport.
    /// </summary>
    Http,

    /// <summary>
    /// Mutual TLS. The implant presents a client certificate that must chain to the
    /// engagement CA and bind <c>(implant_id, engagement_id)</c>; the beacon stream
    /// terminates here (architecture.md Sec 9).
    /// </summary>
    Mtls,
}
