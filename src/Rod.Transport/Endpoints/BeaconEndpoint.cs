using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Presence;
using Rod.V1;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The implant-initiated beacon stream (roadmap M1.3). An implant opens a
/// long-lived reverse connection; the first frame it sends is the handshake
/// (payload = <see cref="HandshakeRequest"/>), and the first frame the server
/// writes back is the <see cref="HandshakeResponse"/>. On a successful handshake
/// the implant is recorded online in its engagement; when the stream closes the
/// implant is marked offline.
///
/// mTLS is terminated at Kestrel before this handler runs: the presenting client
/// certificate has already chained to the CA. The application-layer identity
/// check (architecture.md Sec 9) -- that the certificate's
/// <c>(implant_id, engagement_id)</c> binding matches what the handshake
/// advertises and what the implant enrolled with -- happens in
/// <see cref="HandshakeService"/>.
/// </summary>
internal sealed class BeaconEndpoint : Beacon.BeaconBase
{
    private readonly HandshakeService _handshake;
    private readonly IPresenceRegistry _presence;

    public BeaconEndpoint(HandshakeService handshake, IPresenceRegistry presence)
    {
        _handshake = handshake;
        _presence = presence;
    }

    public override async Task CheckIn(
        IAsyncStreamReader<Frame> requestStream,
        IServerStreamWriter<Frame> responseStream,
        ServerCallContext context)
    {
        var httpContext = context.GetHttpContext();

        // 1. Await the handshake frame. The implant must speak first.
        if (!await requestStream.MoveNext(context.CancellationToken))
            return; // Empty stream; nothing to handshake with.

        var firstFrame = requestStream.Current;
        HandshakeRequest handshakeRequest;
        try
        {
            handshakeRequest = HandshakeRequest.Parser.ParseFrom(firstFrame.Payload);
        }
        catch (InvalidProtocolBufferException)
        {
            // The first payload was not a recognizable handshake request.
            await WriteAsync(responseStream, Response(HandshakeStatus.Unspecified, engagementId: null));
            return;
        }

        // 2. Run the handshake. HandshakeService performs the version check, the
        //    implant lookup, and the mTLS identity check (certificate engagement
        //    == enrolled engagement); refusals come back as HandshakeException.
        var response = await TryHandshakeAsync(httpContext, handshakeRequest);
        await WriteAsync(responseStream, response);
        if (response.Status != HandshakeStatus.Ok)
            return;

        // 3. Presence is now live. Hold the stream open for the implant's
        //    session and mark it offline when the connection ends -- whether the
        //    implant closed cleanly or the stream was aborted.
        try
        {
            // Drain remaining frames; tasking flows over these in later
            // milestones. For M1.3 the implant holds the stream open and the
            // server keeps presence recorded while it does. MoveAsync returns
            // false on a clean client close and throws on an abort; both reach
            // the finally below.
            while (await requestStream.MoveNext(context.CancellationToken))
            {
                // No tasking yet (M1.4); presence stays online for the life of
                // the connection. Advancing the reader is enough to keep the
                // stream alive and observe client-side close.
            }
        }
        finally
        {
            await _presence.SetOfflineAsync(
                ResolveImplantId(handshakeRequest, httpContext),
                CancellationToken.None);
        }
    }

    private async Task<HandshakeResponse> TryHandshakeAsync(
        HttpContext httpContext,
        HandshakeRequest request)
    {
        // The certificate identity is authoritative (read off the mTLS-presented
        // cert), not the wire -- an implant cannot name another engagement by
        // editing its handshake. The implant id from the cert is what we look up.
        var certIdentity = ClientCertificateIdentity.Read(httpContext);

        try
        {
            var result = await _handshake.HandshakeAsync(
                new HandshakeCommand(
                    ImplantId: certIdentity?.ImplantId
                        ?? ParseImplantId(request.ImplantId)
                        ?? default,
                    MajorVersion: request.Version?.Major ?? -1,
                    MinorVersion: request.Version?.Minor ?? -1,
                    Capabilities: request.Capabilities,
                    CertificateEngagementId: certIdentity?.EngagementId),
                CancellationToken.None);

            return Response(HandshakeStatus.Ok, result.EngagementId.ToString());
        }
        catch (HandshakeException ex)
        {
            var status = ex.Reason switch
            {
                HandshakeReason.UnknownImplant => HandshakeStatus.UnknownImplant,
                HandshakeReason.VersionMismatch => HandshakeStatus.VersionMismatch,
                HandshakeReason.IdentityMismatch => HandshakeStatus.IdentityMismatch,
                _ => HandshakeStatus.Unspecified,
            };
            return Response(status, engagementId: null);
        }
    }

    // The implant id used to mark presence offline on disconnect. Prefer the
    // certificate binding (authoritative); fall back to the handshake field only
    // when no certificate was presented.
    private static ImplantId ResolveImplantId(HandshakeRequest request, HttpContext httpContext)
        => ClientCertificateIdentity.Read(httpContext)?.ImplantId
           ?? ParseImplantId(request.ImplantId)
           ?? default;

    private static ImplantId? ParseImplantId(string? text)
        => ImplantId.TryParse(text, out var id) ? id : null;

    private static HandshakeResponse Response(HandshakeStatus status, string? engagementId)
        => new()
        {
            Status = status,
            Version = new ProtocolVersion { Major = ProtocolVersions.Major, Minor = ProtocolVersions.Minor },
            EngagementId = engagementId ?? string.Empty,
        };

    private static Task WriteAsync(IServerStreamWriter<Frame> stream, HandshakeResponse response)
    {
        var frame = new Frame { Payload = ByteString.CopyFrom(response.ToByteArray()) };
        return stream.WriteAsync(frame);
    }
}
