using Google.Protobuf;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Pki;
using Rod.CoreState.Tasks;
using Rod.V1;
using Task = System.Threading.Tasks.Task;

namespace Rod.Transport.Endpoints;

// The shared downstream-tasking composition for every beacon transport
// (architecture.md Sec 8): build the signed TaskRequest frame, record the
// dispatch in the audit trail, and slice a staged payload into its chunk run.
// Extracted from BeaconEndpoint so the gRPC stream and the plain-HTTP envelope
// deliver byte-identical tasking -- the signature, the staged marker, and the
// TaskDispatched audit write have exactly one implementation.

/// <summary>
/// Marshals dispatched tasking for every beacon transport: the signed
/// <see cref="TaskRequest"/> frame, its audit record, and the staged-payload
/// chunk run a StagedPull demands. Singleton: stateless composition over the
/// tasking CA and the audit and artifact stores.
/// </summary>
internal sealed class BeaconTasking
{
    // The downstream chunk size for staged payloads: the same budget the
    // implant's exfil chunker honors, so a marshaled Frame fits the transport
    // message cap with protobuf overhead to spare in both directions.
    public const int StagedChunkSize = 512 * 1024;

    private readonly IImplantCertificateAuthority _ca;
    private readonly IAuditStore _audit;
    private readonly IArtifactStore _artifacts;

    public BeaconTasking(IImplantCertificateAuthority ca, IAuditStore audit, IArtifactStore artifacts)
    {
        _ca = ca;
        _audit = audit;
        _artifacts = artifacts;
    }

    /// <summary>
    /// Builds the signed <see cref="TaskRequest"/> for a dispatched task: the
    /// typed arm's staged marker when the task carries server-side payload, and
    /// the CA's command signature over the canonical
    /// (implant_id, task_id, verb, arguments) tuple (architecture.md Sec 9) --
    /// the implant id binds the task to its intended executor, and the implant
    /// verifies against the CA it already trusts before executing.
    /// </summary>
    public TaskRequest BuildSignedRequest(TaskDispatched dispatched)
    {
        var request = new TaskRequest
        {
            TaskId = dispatched.TaskId.ToString(),
            Verb = dispatched.Verb,
            Arguments = dispatched.Arguments,
        };
        // The typed arm's marker (architecture.md Sec 10): a staged task tells
        // the implant its payload is server-side and demanded, not inline. The
        // sha256 token inside the signed arguments stays the integrity
        // authority; the marker only switches the implant's grammar.
        if (dispatched.StagedBytes is { } stagedBytes)
            request.StagedBytes = (ulong)stagedBytes;
        request.Signature = ByteString.CopyFrom(
            _ca.SignTasking(dispatched.ImplantId.ToString(), request.TaskId, request.Verb, request.Arguments));
        return request;
    }

    /// <summary>
    /// Frames the signed request as the opaque <see cref="Frame"/> every
    /// transport writes downstream.
    /// </summary>
    public Frame MarshalFrame(TaskDispatched dispatched)
        => new() { Payload = ByteString.CopyFrom(BuildSignedRequest(dispatched).ToByteArray()) };

    /// <summary>
    /// Records the dispatch (architecture.md Sec 11). Dispatch is server-driven
    /// (the implant pulls the queue), so the event is attributed to the operator
    /// whose tasking it carries out. The payload is the verb/arguments and the
    /// outcome the dispatched task id -- a task's full attributed arc is
    /// TaskIssued -&gt; TaskDispatched -&gt; TaskCompleted.
    /// </summary>
    public Task RecordDispatchAsync(TaskDispatched dispatched, CancellationToken cancellationToken)
        => _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: dispatched.EngagementId.Value,
                operatorId: dispatched.IssuedBy.Value,
                implantId: dispatched.ImplantId.Value,
                taskId: dispatched.TaskId.Value,
                verb: dispatched.Verb,
                kind: AuditEventKind.TaskDispatched,
                payload: dispatched.Arguments,
                output: null,
                outcome: dispatched.TaskId.ToString(),
                at: dispatched.DispatchedAt),
            cancellationToken);

    /// <summary>
    /// Slices a demanded task's staged payload into its downstream chunk run:
    /// the task-bound artifact the issuer staged, cut at the frame-budget chunk
    /// size, 0-origin sequences, terminal on the last chunk. A demand whose
    /// staged bytes are gone (an expired store, a restart that lost the
    /// in-memory artifacts) is answered with a single empty terminal chunk so
    /// the implant resolves and fails the hash check honestly rather than
    /// waiting on chunks that will never come.
    /// </summary>
    public async Task<IReadOnlyList<Frame>> StagedChunkRunAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var staged = (await _artifacts.ForTaskAsync(taskId, cancellationToken))
            .FirstOrDefault(a => a.Name == StagedArtifacts.NameFor(taskId));
        var content = staged?.Content ?? Array.Empty<byte>();

        var frames = new List<Frame>((content.Length / StagedChunkSize) + 1);
        for (var offset = 0; ; offset += StagedChunkSize)
        {
            var end = Math.Min(offset + StagedChunkSize, content.Length);
            var slice = new byte[end - offset];
            Array.Copy(content, offset, slice, 0, slice.Length);
            var chunk = new StagedChunk
            {
                TaskId = new TaskId(taskId).ToString(),
                Sequence = (ulong)(offset / StagedChunkSize),
                Terminal = end == content.Length,
                Data = ByteString.CopyFrom(slice),
            };
            frames.Add(new Frame { Payload = ByteString.CopyFrom(chunk.ToByteArray()) });
            if (chunk.Terminal)
                break;
        }
        return frames;
    }
}
