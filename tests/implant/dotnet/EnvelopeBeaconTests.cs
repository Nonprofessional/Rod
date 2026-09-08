using Google.Protobuf;
using Rod.Implant.Internal;
using Rod.V1;

namespace Rod.Implant.Tests;

// Pins the envelope check-in client's wire-facing halves (architecture.md
// Sec 8): the URL shape that selects it (a web URL -- http(s):// -- is the
// envelope's; a bare host:port stays the mTLS gRPC stream's), the check-in
// URL the cycle POSTs, and the delimited-frame codec both directions ride.
// The check-in loop itself is exercised end to end against the real
// teamserver by the integration suite (DotNetImplantTests).
public class EnvelopeBeaconTests
{
    [Theory]
    [InlineData("http://front.example.test", true)]
    [InlineData("https://front.example.test", true)]
    [InlineData("HTTPS://front.example.test", true)]
    [InlineData("https://front.example.test/with/a/path", true)]
    [InlineData("front.example.test:8443", false)]
    [InlineData("10.0.0.5:8443", false)]
    [InlineData("", false)]
    public void IsWebBeaconUrl_ClassifiesByScheme(string beaconUrl, bool expected)
    {
        // The bake's discriminator: an entry carrying the web scheme checks
        // in over the envelope POST cycle; a bare host:port dials the mTLS
        // gRPC stream.
        Assert.Equal(expected, EnvelopeBeacon.IsWebBeaconUrl(beaconUrl));
    }

    [Theory]
    [InlineData("https://front.example.test", "https://front.example.test/implants/beacon")]
    [InlineData("http://127.0.0.1:5080", "http://127.0.0.1:5080/implants/beacon")]
    [InlineData("https://front.example.test/some/front/path", "https://front.example.test/implants/beacon")]
    [InlineData("HTTP://front.example.test:80", "HTTP://front.example.test:80/implants/beacon")]
    public void CheckInUrl_AppendsTheFixedRouteToTheAuthority(string beaconUrl, string expected)
    {
        // The route is fixed on every web listener; whatever path a front
        // carried, the check-in lands on the authority plus the route.
        Assert.Equal(expected, EnvelopeBeacon.CheckInUrl(beaconUrl));
    }

    [Fact]
    public void Codec_RoundTripsAFramedSequence()
    {
        // The wire shape: each Frame marshaled and prefixed with its byte
        // length as an unsigned varint -- kind and payload both survive.
        var frames = new List<Frame>
        {
            new()
            {
                Payload = ByteString.CopyFrom(new HandshakeRequest
                {
                    Version = new ProtocolVersion { Major = 1, Minor = 0 },
                    ImplantId = "implant-id",
                }.ToByteArray()),
            },
            new()
            {
                Kind = FrameKind.TaskResult,
                Payload = ByteString.CopyFrom(new TaskResult
                {
                    TaskId = "task-id",
                    Outcome = TaskOutcome.Succeeded,
                    Output = "done",
                }.ToByteArray()),
            },
            new()
            {
                Kind = FrameKind.StagedPull,
                Payload = ByteString.CopyFrom(new StagedPull { TaskId = "task-id" }.ToByteArray()),
            },
        };

        var parsed = EnvelopeCodec.Parse(EnvelopeCodec.Encode(frames));

        Assert.Equal(frames.Count, parsed.Count);
        Assert.Equal(FrameKind.Unspecified, parsed[0].Kind);
        Assert.Equal(frames[0].Payload, parsed[0].Payload);
        Assert.Equal(FrameKind.TaskResult, parsed[1].Kind);
        Assert.Equal(TaskResult.Parser.ParseFrom(frames[1].Payload).Output,
            TaskResult.Parser.ParseFrom(parsed[1].Payload).Output);
        Assert.Equal(FrameKind.StagedPull, parsed[2].Kind);
    }

    [Theory]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 })] // varint never terminates in uint32
    [InlineData(new byte[] { 0x05, 0x01, 0x02 })] // declared length past the body
    [InlineData(new byte[] { 0x02, 0xff })] // declared length past the body, one byte short
    public void Codec_RefusesMalformedBodies(byte[] body)
    {
        // A malformed body refuses whole rather than returning a partial
        // read -- the caller drops the cycle instead of acting on half the
        // response.
        Assert.Throws<InvalidOperationException>(() => EnvelopeCodec.Parse(body));
    }

    [Fact]
    public void SealedCheckIn_RoundTripsUnderTheBakedKey_AndDistinguishesDirections()
    {
        // The sealed wire shape (architecture.md Sec 8/9): the baked key's
        // id and key halves seal counter || framed-frames as the R1 envelope,
        // and the same key opens what came back -- but only under the same
        // purpose tag: a request body never opens as a response and vice
        // versa, so neither direction's ciphertext can be reflected.
        var baked = Convert.ToBase64String(
            Guid.NewGuid().ToByteArray().Concat(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToArray());
        var key = EnvelopeBeacon.ParseBakedKey(baked);
        Assert.NotNull(key);

        var plaintext = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x2a, 0x05, 0x68, 0x65, 0x6c, 0x6c, 0x6f };

        var request = EnvelopeBeacon.SealCheckInBody(plaintext, key!.KeyId, key.Key, "rod-checkin-v1");
        var response = EnvelopeBeacon.SealCheckInBody(plaintext, key.KeyId, key.Key, "rod-checkin-response-v1");

        Assert.Equal(plaintext, EnvelopeBeacon.TryOpenCheckInBody(request, key.KeyId, key.Key, "rod-checkin-v1"));
        Assert.Equal(plaintext, EnvelopeBeacon.TryOpenCheckInBody(response, key.KeyId, key.Key, "rod-checkin-response-v1"));
        Assert.Null(EnvelopeBeacon.TryOpenCheckInBody(request, key.KeyId, key.Key, "rod-checkin-response-v1"));
        Assert.Null(EnvelopeBeacon.TryOpenCheckInBody(response, key.KeyId, key.Key, "rod-checkin-v1"));

        // Tampered bytes never open, and a foreign key never opens the seal.
        var tampered = (byte[])request.Clone();
        tampered[^2] = (byte)(tampered[^2] ^ 0x01);
        Assert.Null(EnvelopeBeacon.TryOpenCheckInBody(tampered, key.KeyId, key.Key, "rod-checkin-v1"));
        var other = EnvelopeBeacon.ParseBakedKey(Convert.ToBase64String(
            Guid.NewGuid().ToByteArray().Concat(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToArray()));
        Assert.Null(EnvelopeBeacon.TryOpenCheckInBody(request, other!.KeyId, other.Key, "rod-checkin-v1"));
    }

    [Fact]
    public void ParseBakedKey_RejectsMalformedMaterial()
    {
        // A bad bake falls back to the plaintext frame rather than checking
        // in undecodably: the key parse refuses the wrong length and junk
        // base64 alike.
        Assert.Null(EnvelopeBeacon.ParseBakedKey(""));
        Assert.Null(EnvelopeBeacon.ParseBakedKey("not-base64!!"));
        Assert.Null(EnvelopeBeacon.ParseBakedKey(Convert.ToBase64String(new byte[8])));
    }

    [Fact]
    public void TransportProfile_SealsCheckIns_OnlyWithShapeAndKey()
    {
        // The posture rule mirrors the enroll envelope's: the "aesgcm" shape
        // seals only when the baked key rides with it -- anything else falls
        // back to the plaintext frame rather than an undecodable check-in.
        var bakedKey = Convert.ToBase64String(new byte[48]);
        Assert.True(new TransportProfile { CheckInEnvelope = "aesgcm", EnvelopeKey = bakedKey }.SealsCheckIns);
        Assert.False(new TransportProfile { CheckInEnvelope = "aesgcm" }.SealsCheckIns);
        Assert.False(new TransportProfile { CheckInEnvelope = "none", EnvelopeKey = bakedKey }.SealsCheckIns);
        Assert.False(new TransportProfile().SealsCheckIns);
    }
}
