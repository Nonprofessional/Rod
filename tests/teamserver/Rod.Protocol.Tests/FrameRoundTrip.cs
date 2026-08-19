using Google.Protobuf;
using Rod.V1;

namespace Rod.Protocol.Tests;

/// <summary>
/// Smoke test: a Frame survives a serialize/parse round trip with
/// every field intact. This proves the generated bindings are usable end to end.
/// </summary>
public class FrameRoundTrip
{
    [Fact]
    public void Frame_RoundTrips_WithKindAndPayload()
    {
        var original = new Frame
        {
            Kind = FrameKind.TaskResult,
            Payload = ByteString.CopyFrom(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
        };

        Frame restored = Frame.Parser.ParseFrom(original.ToByteArray());

        Assert.Equal(FrameKind.TaskResult, restored.Kind);
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

    // Staged tasking (architecture.md Sec 10, the typed arm): the optional
    // marker on TaskRequest, the demand, and the chunk run. The unset case is
    // the Tier 0 fallback property stated in extending/implants.md -- an
    // ordinary task round-trips with the arm's field absent, not zero or
    // defaulted, so an implant that never reads it sees exactly the task it
    // always saw.

    [Fact]
    public void TaskRequest_StagedBytes_RoundTrips()
    {
        var staged = new TaskRequest
        {
            TaskId = "t-1",
            Verb = "file.push",
            Arguments = "/tmp/tool.bin sha256:abc",
            StagedBytes = 10 * 1024 * 1024,
        };

        var restoredStaged = TaskRequest.Parser.ParseFrom(staged.ToByteArray());
        Assert.True(restoredStaged.HasStagedBytes);
        Assert.Equal(10UL * 1024 * 1024, restoredStaged.StagedBytes);

        var inline = new TaskRequest { TaskId = "t-2", Verb = "shell.exec", Arguments = "id" };
        var restoredInline = TaskRequest.Parser.ParseFrom(inline.ToByteArray());
        Assert.False(restoredInline.HasStagedBytes);
    }

    [Fact]
    public void TaskRequest_TargetImplantId_MarksFrontedTasking()
    {
        // The fronting arm (architecture.md Sec 5.2): set only on a frame a
        // server fronts to a fronting-capable implant; absent on every frame
        // any other implant receives, which is what keeps the arm additive.
        var fronted = new TaskRequest
        {
            TaskId = "t-1",
            Verb = "tunnel.forward",
            Arguments = "host.example 443",
            TargetImplantId = "child",
        };

        var restoredFronted = TaskRequest.Parser.ParseFrom(fronted.ToByteArray());
        Assert.True(restoredFronted.HasTargetImplantId);
        Assert.Equal("child", restoredFronted.TargetImplantId);

        var own = new TaskRequest { TaskId = "t-2", Verb = "shell.exec", Arguments = "id" };
        var restoredOwn = TaskRequest.Parser.ParseFrom(own.ToByteArray());
        Assert.False(restoredOwn.HasTargetImplantId);
    }

    [Fact]
    public void StagedPullAndChunk_RoundTrip()
    {
        var pull = new StagedPull { TaskId = "t-1" };
        var restoredPull = StagedPull.Parser.ParseFrom(pull.ToByteArray());
        Assert.Equal("t-1", restoredPull.TaskId);

        var chunk = new StagedChunk
        {
            TaskId = "t-1",
            Sequence = 19,
            Terminal = true,
            Data = ByteString.CopyFrom(new byte[] { 0x01, 0x02 }),
        };
        var restoredChunk = StagedChunk.Parser.ParseFrom(chunk.ToByteArray());
        Assert.Equal("t-1", restoredChunk.TaskId);
        Assert.Equal(19UL, restoredChunk.Sequence);
        Assert.True(restoredChunk.Terminal);
        Assert.Equal(chunk.Data, restoredChunk.Data);
    }

    // Handshake messages: the first payload exchanged on a
    // CheckIn stream must round-trip with version, identity, and capabilities
    // intact -- these are what the server gates presence on.

    [Fact]
    public void HandshakeRequest_RoundTrips()
    {
        var original = new HandshakeRequest
        {
            Version = new ProtocolVersion { Major = 1, Minor = 0 },
            ImplantId = "imp-42",
            Capabilities = { "shell.exec", "file.push", "file.pull" },
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
