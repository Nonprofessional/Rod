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
}
