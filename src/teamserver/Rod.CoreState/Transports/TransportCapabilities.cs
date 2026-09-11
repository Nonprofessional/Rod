using System.Collections.Concurrent;
using Rod.CoreState.Tasks;

namespace Rod.CoreState.Transports;

// The tasking-capability truth for the check-in carriers (architecture.md
// Sec 8, Sec 10.3). The set lives in core state for the same reason
// ChannelVerbs does: the transport layer's claim gates, the build pipeline's
// beacon validation, and the operator UI all need the same answer without a
// cross-layer dependency. The in-tree carriers declare themselves here; a
// carrier added beyond the in-tree set registers the same way, so the table
// stays open where a closed enumeration would force an edit per protocol.

/// <summary>
/// Whether a check-in carrier can run a task that behaves as a live channel
/// (architecture.md Sec 10.3): a channel's input half needs a stream the
/// server can write to while the task runs, so only a carrier that holds one
/// may claim the channel verbs.
/// </summary>
public enum ChannelSupport
{
    /// <summary>
    /// The carrier holds a live stream for the connection's life (the gRPC
    /// beacon stream): channel tasks claim natively, their input and output
    /// riding the stream that carried the task.
    /// </summary>
    Native,

    /// <summary>
    /// The carrier is a poll shape -- one request, response, or datagram per
    /// check-in, no writer to push on: a channel task is requeued untouched
    /// for a native carrier to claim.
    /// </summary>
    None,
}

/// <summary>
/// The tasking capabilities one check-in carrier declares. The per-response
/// frame budget stays on the carrier itself (a wire-format property), while
/// the claim policy is the table's -- the two decisions every poll path
/// makes, split where each half's truth lives.
/// </summary>
/// <param name="Channels">The carrier's channel support level.</param>
public sealed record CarrierCapabilities(ChannelSupport Channels);

/// <summary>
/// The outcome of the claim evaluation a poll carrier runs before taking a
/// dispatched task into its check-in response.
/// </summary>
public enum ClaimDecision
{
    /// <summary>The carrier claims the task into this check-in.</summary>
    Claim,

    /// <summary>
    /// The verb runs as a live channel and this carrier holds no stream to
    /// run one on: the task is requeued untouched for a native carrier to
    /// claim (architecture.md Sec 10.3).
    /// </summary>
    NeedsLiveChannel,

    /// <summary>
    /// The marshaled task exceeds the carrier's per-response budget: the
    /// task is requeued for a carrier whose framing can carry it.
    /// </summary>
    Oversize,
}

/// <summary>
/// The carrier capability table and the claim evaluation shared by every
/// poll path (the envelope check-in, the DNS bridge, the message-pipe
/// bridge). One policy point instead of one copy per carrier: the deferral
/// reasons stay stable, and a carrier arriving later declares itself rather
/// than duplicating the gate.
/// </summary>
public static class TransportCapabilities
{
    /// <summary>The gRPC beacon stream: the native channel carrier.</summary>
    public static readonly CarrierCapabilities BeaconStream = new(ChannelSupport.Native);

    /// <summary>The plain-HTTP envelope POST cycle: poll only.</summary>
    public static readonly CarrierCapabilities Envelope = new(ChannelSupport.None);

    /// <summary>The DNS TXT datagram check-in: poll only, datagram-sized.</summary>
    public static readonly CarrierCapabilities Dns = new(ChannelSupport.None);

    /// <summary>
    /// The self-delimited message framing the named-pipe and raw-TCP
    /// listeners share: one connection is one poll check-in.
    /// </summary>
    public static readonly CarrierCapabilities MessagePipe = new(ChannelSupport.None);

    // Keyed by carrier wire name so a carrier the core does not know can
    // declare itself without editing this file; case-insensitive because the
    // name crosses operator input and persistence as a plain string.
    private static readonly ConcurrentDictionary<string, CarrierCapabilities> Carriers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["beacon-stream"] = BeaconStream,
            ["envelope"] = Envelope,
            ["dns"] = Dns,
            ["message-pipe"] = MessagePipe,
        };

    /// <summary>
    /// Declares a carrier's capabilities. An identical re-declaration is a
    /// no-op (a double registration is harmless); a conflicting one throws,
    /// because silently replacing a live carrier's capabilities would hide
    /// the mistake until dispatch.
    /// </summary>
    public static void Register(string carrier, CarrierCapabilities capabilities)
    {
        if (string.IsNullOrWhiteSpace(carrier))
            throw new ArgumentException("A carrier name is required.", nameof(carrier));
        ArgumentNullException.ThrowIfNull(capabilities);

        var existing = Carriers.GetOrAdd(carrier, capabilities);
        if (existing != capabilities)
            throw new InvalidOperationException(
                $"The carrier '{carrier}' is already declared with different capabilities.");
    }

    /// <summary>
    /// Looks a carrier up by wire name. An unknown name falls back to
    /// <see cref="ChannelSupport.None"/>: a carrier that never declared
    /// itself must not silently gain channel claiming -- the conservative
    /// answer is the safe one.
    /// </summary>
    public static CarrierCapabilities Find(string carrier)
    {
        if (!string.IsNullOrWhiteSpace(carrier) && Carriers.TryGetValue(carrier, out var found))
            return found;
        return new CarrierCapabilities(ChannelSupport.None);
    }

    /// <summary>
    /// Whether a dispatched task fits this carrier's check-in: a channel verb
    /// needs a carrier that holds a live stream, and the marshaled task must
    /// fit the carrier's per-response budget. The channel deferral wins over
    /// size, so the reason an operator reads never depends on which bound the
    /// task tripped first.
    /// </summary>
    public static ClaimDecision EvaluateClaim(
        CarrierCapabilities carrier,
        string verb,
        int wireSize,
        int maxBytes)
    {
        if (ChannelVerbs.IsChannelVerb(verb) && carrier.Channels != ChannelSupport.Native)
            return ClaimDecision.NeedsLiveChannel;
        if (wireSize > maxBytes)
            return ClaimDecision.Oversize;
        return ClaimDecision.Claim;
    }
}
