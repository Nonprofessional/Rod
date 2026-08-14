using Google.Protobuf;
using Rod.V1;

namespace Rod.Protocol.Tests;

/// <summary>
/// Roadmap  smoke test: a Frame survives a serialize/parse round trip with
/// every field intact, and the envelope's application-layer identifiers travel
/// with it. This proves the generated bindings are usable end to end.
/// </summary>
public class FrameRoundTrip
{
    [Fact]
    public void Frame_RoundTrips_WithEnvelopeAndPayload()
    {
        var original = new Frame
        {
            Envelope = new Envelope
            {
                EngagementId = "eng-7",
                ImplantId = "imp-42",
                Sequence = 123456789,
            },
            Payload = ByteString.CopyFrom(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
        };

        Frame restored = Frame.Parser.ParseFrom(original.ToByteArray());

        Assert.Equal("eng-7", restored.Envelope.EngagementId);
        Assert.Equal("imp-42", restored.Envelope.ImplantId);
        Assert.Equal<ulong>(123456789, restored.Envelope.Sequence);
        Assert.Equal(original.Payload, restored.Payload);
    }

    [Fact]
    public void ProtocolVersion_RoundTrips()
    {
        var original = new ProtocolVersion { Major = 1, Minor = 0 };

        var restored = ProtocolVersion.Parser.ParseFrom(original.ToByteArray());

        Assert.Equal(1, restored.Major);
        Assert.Equal(0, restored.Minor);
    }

    [Fact]
    public void Frame_PayloadStaysOpaque()
    {
        // A redirector forwards the inner payload without parsing it; it must
        // survive the trip regardless of content, including non-UTF8 bytes.
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

        var restored = Frame.Parser.ParseFrom(
            new Frame { Payload = ByteString.CopyFrom(bytes) }.ToByteArray());

        Assert.Equal(bytes, restored.Payload.Span.ToArray());
    }

    // Handshake messages (): the first payload exchanged on a
    // CheckIn stream must round-trip with version, identity, and capabilities
    // intact -- these are what the server gates presence on.

    [Fact]
    public void HandshakeRequest_RoundTrips()
    {
        var original = new HandshakeRequest
        {
            Version = new ProtocolVersion { Major = 1, Minor = 0 },
            ImplantId = "imp-42",
            Capabilities = { "shell.exec", "file.push", "probe.read" },
        };

        var restored = HandshakeRequest.Parser.ParseFrom(original.ToByteArray());

        Assert.Equal(1, restored.Version.Major);
        Assert.Equal(0, restored.Version.Minor);
        Assert.Equal("imp-42", restored.ImplantId);
        Assert.Equal(original.Capabilities, restored.Capabilities);
    }

    [Fact]
    public void HandshakeResponse_RoundTrips()
    {
        var original = new HandshakeResponse
        {
            Status = HandshakeStatus.Ok,
            Version = new ProtocolVersion { Major = 1, Minor = 0 },
            EngagementId = "eng-7",
        };

        var restored = HandshakeResponse.Parser.ParseFrom(original.ToByteArray());

        Assert.Equal(HandshakeStatus.Ok, restored.Status);
        Assert.Equal(1, restored.Version.Major);
        Assert.Equal("eng-7", restored.EngagementId);
    }
}
