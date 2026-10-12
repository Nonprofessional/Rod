using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rod.CoreState;
using Rod.CoreState.Listeners;
using Rod.CoreState.Redirectors;

namespace Rod.Transport.Redirectors;

/// <summary>
/// One forwarding rule the control plane provisions a node with
/// (architecture.md Sec 8): bind <see cref="ListenHost"/>:
/// <see cref="ListenPort"/> and splice to <see cref="UpstreamHost"/>:
/// <see cref="UpstreamPort"/>. The rule is the whole data plane -- opaque
/// bytes, no protocol knowledge -- so <see cref="Transport"/> names only the
/// carriage ("tcp" today; the DNS front's datagram relay will add "udp").
/// </summary>
public sealed record RedirectorForwardingRule(
    string RuleId,
    string Transport,
    string ListenHost,
    int ListenPort,
    string UpstreamHost,
    int UpstreamPort,
    string? AllowList);

/// <summary>
/// The full rule set for one node: every front attached to it across every
/// listener of every engagement (a node is deployment capacity; machines are
/// shared, isolation is not). <see cref="Digest"/> is the content hash of the
/// canonical rule list -- the idempotence key the node reconciles against, so
/// a re-delivered set with the same digest is a no-op.
/// </summary>
public sealed record RedirectorRuleSet(string Digest, IReadOnlyList<RedirectorForwardingRule> Rules)
{
    public static RedirectorRuleSet Empty { get; } = new("", []);

    public bool IsEmpty => Rules.Count == 0;
}

/// <summary>
/// Derives a node's rule set from the listener definitions: each front the
/// node carries becomes one forwarding rule whose upstream is the listener's
/// own bind (the node splices to what the teamserver terminates). The
/// upstream host is the deployment's advertised face -- a wildcard bind
/// names no address a node could dial, so the control tier's configured
/// override wins, and absent one, the local address the node's control
/// connection arrived on stands in (the node dialed this host; the implant
/// listeners bind it too in the monolithic kernel).
/// </summary>
public static class RedirectorRules
{
    public static async Task<RedirectorRuleSet> BuildAsync(
        IListenerStore definitions,
        RedirectorNodeId node,
        string advertisedUpstreamHost,
        CancellationToken cancellationToken = default)
    {
        var rules = new List<RedirectorForwardingRule>();
        foreach (var definition in await definitions.ListAsync(cancellationToken))
        {
            foreach (var front in definition.FrontList)
            {
                if (front.NodeId != node)
                    continue;

                var (listenHost, listenPort) = SplitEndpoint(front.ListenEndpoint);
                var (bindHost, bindPort) = SplitEndpoint(definition.BindAddress);
                var upstreamHost = advertisedUpstreamHost.Length > 0
                    ? advertisedUpstreamHost
                    : bindHost;
                rules.Add(new RedirectorForwardingRule(
                    $"{definition.Id:N}-{front.FrontId:N}",
                    "tcp",
                    listenHost,
                    listenPort,
                    upstreamHost,
                    bindPort,
                    string.IsNullOrWhiteSpace(front.AllowList) ? null : front.AllowList));
            }
        }

        rules.Sort((a, b) => string.CompareOrdinal(a.RuleId, b.RuleId));
        return new RedirectorRuleSet(DigestOf(rules), rules);
    }

    public static string DigestOf(IReadOnlyList<RedirectorForwardingRule> rules)
    {
        // The canonical form the digest hashes: the same camelCase JSON the
        // wire carries, rules in id order. Same rules, same digest -- the
        // node's reconcile key.
        var json = JsonSerializer.Serialize(rules, WireJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    // Splits "host:port" (bracketed IPv6 allowed). A malformed endpoint is a
    // bug upstream (attach validated the shape), not a runtime condition to
    // handle here; the throw surfaces it in the push where it belongs.
    private static (string Host, int Port) SplitEndpoint(string endpoint)
    {
        if (endpoint.StartsWith('['))
        {
            var close = endpoint.IndexOf(']');
            return (endpoint[1..close], int.Parse(endpoint[(close + 2)..]));
        }

        var colon = endpoint.LastIndexOf(':');
        return (endpoint[..colon], int.Parse(endpoint[(colon + 1)..]));
    }
}
