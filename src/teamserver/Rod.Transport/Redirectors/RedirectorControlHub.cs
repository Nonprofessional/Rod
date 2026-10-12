using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
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

    public RedirectorControlHub(IListenerStore definitions)
    {
        _definitions = definitions;
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
    /// Registers a session. The advertised host is the face this node's rules
    /// name as upstream: the configured override when one exists, else the
    /// local address its control connection arrived on.
    /// </summary>
    public void Attach(RedirectorNodeId node, WebSocket socket, string advertisedHost)
        => _sessions[node] = new NodeSession(socket, advertisedHost);

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

        var rules = await RedirectorRules.BuildAsync(_definitions, node, session.AdvertisedHost, cancellationToken);
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

    /// <summary>Closes a node's live session (retirement's reachability half).</summary>
    public async Task CloseAsync(RedirectorNodeId node, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryRemove(node, out var session))
            await session.CloseAsync(cancellationToken);
    }

    /// <summary>One node's open control socket and its serialized send side.</summary>
    private sealed record NodeSession(WebSocket Socket, string AdvertisedHost)
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

        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "retired", cancellationToken);
            }
            catch (WebSocketException)
            {
                // The socket was already going away; retirement owes no
                // handshake.
            }
        }
    }
}
