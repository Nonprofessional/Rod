namespace Rod.CoreState.Listeners;

/// <summary>
/// One entry of a listener's ordered front list (architecture.md Sec 8): an
/// attachment to an enrolled redirector node. The listener's
/// <see cref="ListenerDefinition.PublicEndpoint"/> is the primary front's
/// (index zero) public endpoint once any front exists -- repoint becomes
/// re-attaching or promoting the primary. The front's own addresses are the
/// pair the control plane provisions the node with: the node binds
/// <see cref="ListenEndpoint"/> and splices it to the listener's bind, and
/// implants dial <see cref="PublicEndpoint"/> through it.
/// </summary>
/// <param name="FrontId">Unique id of this front (a fresh Guid per attachment).</param>
/// <param name="NodeId">The enrolled node this front attaches to.</param>
/// <param name="ListenEndpoint">
/// The endpoint the node binds, <c>host:port</c>. The host is the node's own
/// bind face (a literal IP or wildcard), not the public address.
/// </param>
/// <param name="PublicEndpoint">
/// The address implants dial through this front -- the same shape the
/// listener's own public endpoint takes for its transport family.
/// </param>
/// <param name="AllowList">
/// Optional comma-separated source CIDRs the node enforces on this front
/// (the forwarding rule carries it); null or empty allows every source, the
/// manual fleet's default.
/// </param>
/// <param name="AttachedAt">When the front was attached.</param>
public sealed record ListenerFront(
    Guid FrontId,
    RedirectorNodeId NodeId,
    string ListenEndpoint,
    string PublicEndpoint,
    string? AllowList,
    DateTimeOffset AttachedAt);
