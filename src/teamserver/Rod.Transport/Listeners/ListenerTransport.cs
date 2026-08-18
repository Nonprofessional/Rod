namespace Rod.Transport.Listeners;

/// <summary>
/// The C2 transport a <see cref="Listener"/> terminates (architecture.md Sec 8).
///  ships <see cref="Http"/>, <see cref="Mtls"/>, <see cref="HttpsEnvelope"/>,
/// and <see cref="Dns"/>; SMB and TCP are the remaining transports the
/// architecture calls out, added when a transport needs them.
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

    /// <summary>
    /// The plain-HTTP envelope (architecture.md Sec 8, the implant-reach
    /// escape hatch): the same rod.v1 frames the gRPC stream carries, as
    /// varint-length-delimited sequences in ordinary HTTPS request/response
    /// bodies over the same client certificates. One POST is one poll
    /// check-in. The bind and the mTLS termination are identical to
    /// <see cref="Mtls"/> -- the difference is purely which check-in shape an
    /// implant uses against it, and both are served on either; the listener
    /// entry exists so a deployment can name an endpoint whose purpose is
    /// envelope-only reach (a target language with an HTTP client and a
    /// protobuf codec but no gRPC stack).
    /// </summary>
    HttpsEnvelope,

    /// <summary>
    /// DNS over UDP: TXT-record check-ins for egress-restricted targets
    /// (architecture.md Sec 8). The entry's public endpoint is the zone the
    /// listener answers for; the bind address is the UDP socket. The wire
    /// grammar is the DNS check-in contract (extending/implants.md) -- a poll
    /// refreshes presence and fetches the next signed tasking, result chunks
    /// report outcomes. No handshake and no mTLS ride this transport: a
    /// session opened on a handshake-capable transport is refreshed, and the
    /// implant is identified by its id alone.
    /// </summary>
    Dns,
}
