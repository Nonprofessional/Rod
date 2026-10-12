using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Rod.CoreState;
using Rod.CoreState.Listeners;
using Rod.CoreState.Redirectors;

namespace Rod.Transport.Redirectors;

/// <summary>
/// The live control sessions (architecture.md Sec 8): one per connected
/// node, keyed by node id. Presence here is the node's online status -- the
/// connection is the liveness, the fleet record's stamps are the reporting
/// trail. Rule pushes go through the hub so a front mutation anywhere in the
/// operator API reaches the affected node's open socket without any polling.
/// </summary>
public sealed class RedirectorControlHub
{
    private readonly ConcurrentDictionary<RedirectorNodeId, NodeSession> _sessions = new();
    private readonly IListenerStore _definitions;
    private readonly RedirectorControlOptions _options;

    public RedirectorControlHub(IListenerStore definitions, IOptions<RedirectorControlOptions> options)
    {
        _definitions = definitions;
        _options = options.Value;
    }

    /// <summary>
    /// The nodes with a live control connection -- the roster's online
    /// column. A node absent here and stamped hours ago reads offline.
    /// </summary>
    public IReadOnlyCollection<RedirectorNodeId> OnlineNodes
        => _sessions.Keys.ToArray();

    public bool IsOnline(RedirectorNodeId node)
        => _sessions.ContainsKey(node);

    /// <summary>
    /// Registers a session. The connection's local address is the fallback
    /// upstream face for wildcard-bound listeners: the node dialed this host
    /// and proved the address reachable.
    /// </summary>
    public void Attach(RedirectorNodeId node, WebSocket socket, string connectionLocalHost)
        => _sessions[node] = new NodeSession(socket, connectionLocalHost);

    /// <summary>
    /// Unregisters a node's session, but only when the given socket is the
    /// one that attached: a reconnect that already replaced the entry must
    /// not be unseated by the old socket's teardown.
    /// </summary>
    public void Detach(RedirectorNodeId node, WebSocket socket)
    {
        if (_sessions.TryGetValue(node, out var session) && ReferenceEquals(session.Socket, socket))
            _sessions.TryRemove(node, out _);
    }

    /// <summary>
    /// Pushes the node's current full rule set. Full-state, digest-keyed: a
    /// node that reconnects or missed a push converges on the next one, and
    /// a push that changes nothing is a no-op on the wire. Silent when the
    /// node is offline -- the rule set is derived state; the node reads it
    /// at its next reconnect.
    /// </summary>
    public async Task PushRulesAsync(RedirectorNodeId node, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(node, out var session))
            return;

        var rules = await RedirectorRules.BuildAsync(
            _definitions, node, _options.AdvertisedUpstreamHost ?? "", session.ConnectionLocalHost, cancellationToken);
        await session.SendAsync(ControlMessages.RulesPush(rules), cancellationToken);
    }

    /// <summary>
    /// Pushes every connected node's rule set -- the sweep a restore or any
    /// not-per-node mutation takes (today only the front mutations, which
    /// name their node; the sweep is the safety net that keeps a derived
    /// state from drifting).
    /// </summary>
    public async Task PushAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var node in _sessions.Keys)
            await PushRulesAsync(node, cancellationToken);
    }

    /// <summary>
    /// Closes a node's live session (retirement's reachability half). The
    /// socket is aborted, not handshaked closed: the node's reconnect is
    /// where it reads the named refusal, so the retirement owes no graceful
    /// teardown and must not wait on a node that may be gone.
    /// </summary>
    public Task CloseAsync(RedirectorNodeId node, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryRemove(node, out var session))
            session.Socket.Abort();
        return Task.CompletedTask;
    }

    /// <summary>One node's open control socket and its serialized send side.</summary>
    private sealed record NodeSession(WebSocket Socket, string ConnectionLocalHost)
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public async Task SendAsync(ControlMessage message, CancellationToken cancellationToken)
        {
            // One writer at a time: a heartbeat reply and a rule push can
            // race on the same socket; the lock keeps frames whole.
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                if (Socket.State is WebSocketState.Open)
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(message, WireJson.Options);
                    await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }
}
