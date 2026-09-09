namespace Rod.Transport.Listeners;

/// <summary>
/// The C2 transport a <see cref="Listener"/> terminates (architecture.md Sec 8).
///  ships <see cref="Http"/>, <see cref="Https"/>, <see cref="Mtls"/>,
/// <see cref="Dns"/>, <see cref="Smb"/>, and <see cref="Tcp"/>.
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
    /// The single-port TLS shape (the mainstream C2 listener: one https
    /// socket carries enrollment and check-ins). TLS terminates with the
    /// CA-issued server leaf and never requests a client certificate -- a
    /// TLS CertificateRequest is itself a fingerprint, and the handshake
    /// stays indistinguishable from an ordinary website's. Enrollment rides
    /// the stager token and check-ins ride the sealed envelope under the
    /// per-artifact key, both authenticated at the application layer
    /// (architecture.md Sec 8/9). The interactive gRPC stream does not ride
    /// this transport: over TLS its identity is the client certificate only
    /// <see cref="Mtls"/> asks for.
    /// </summary>
    Https,

    /// <summary>
    /// Mutual TLS. The implant presents a client certificate that must chain to the
    /// engagement CA and bind <c>(implant_id, engagement_id)</c>; the beacon stream
    /// terminates here (architecture.md Sec 9). The envelope POST cycle rides the
    /// same socket alongside the stream, so an implant without a gRPC stack still
    /// checks in against an mTLS front.
    /// </summary>
    Mtls,

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

    /// <summary>
    /// SMB named pipe (architecture.md Sec 8): check-ins over a named pipe for
    /// Windows segments where neither HTTP nor DNS egress exists, carrying the
    /// same rod.v1 frames as the envelope in one self-delimited message. The
    /// bind address is the bare pipe name; the public endpoint is the pipe path
    /// implants dial (<c>\\host\pipe\name</c>). One connection is one poll
    /// check-in. No client certificate rides the pipe (on a Windows host the
    /// SMB session layer authenticates the peer before the pipe is reachable):
    /// the implant is identified by its id alone -- the DNS posture extended
    /// to a handshake-capable transport.
    /// </summary>
    Smb,

    /// <summary>
    /// Raw TCP (architecture.md Sec 8): check-ins over an arbitrary socket for
    /// segment networks that allow sockets but no HTTP shape, carrying the same
    /// self-delimited rod.v1 frame messages as the named pipe. The bind address
    /// is the TCP endpoint; the public endpoint is what implants dial. One
    /// connection is one poll check-in and, like the pipe, no client
    /// certificate rides it: the implant is identified by its id alone.
    /// </summary>
    Tcp,
}

/// <summary>
/// The stable wire name of a listener transport: the enum name in kebab case,
/// a dash at each word break. The listener listing and the operator UI render
/// this name, so a multi-word transport never stringifies as one mashed word
/// (the plain lower-casing the listing once applied produced
/// <c>httpsenvelope</c>, which reads as nothing). Every current entry is
/// single-word and lower-cases as itself.
/// </summary>
public static class ListenerTransportNames
{
    /// <summary>Returns the transport's kebab-case wire name.</summary>
    public static string WireName(this ListenerTransport transport)
    {
        var name = transport.ToString();
        var builder = new System.Text.StringBuilder(name.Length + 4);
        foreach (var letter in name)
        {
            if (char.IsUpper(letter) && builder.Length > 0)
                builder.Append('-');
            builder.Append(char.ToLowerInvariant(letter));
        }
        return builder.ToString();
    }
}
