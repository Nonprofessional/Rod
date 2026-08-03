namespace Rod.Transport.Listeners;

/// <summary>
/// One bound C2 ingress the teamserver terminates (architecture.md Sec 8). A
/// listener is global teamserver infrastructure -- it is shared across engagements,
/// and tenancy is enforced where the architecture puts it (the stager token at
/// enrollment, the <c>(implant_id, engagement_id)</c> client certificate at mTLS),
/// never at the listener. A redirector fronts a listener; a burned redirector is
/// replaced without touching the backend.
///
/// The listener decouples the address Kestrel opens (<see cref="BindAddress"/>) from
/// the address implants are told to dial (<see cref="PublicEndpoint"/>). The bind
/// address is operational plumbing (which interface and port the process opens);
/// the public endpoint is the redirector or host-header the implant carries in its
/// profile. They are independent so the public endpoint can move -- a redirected
/// domain, a different host -- without reconfiguring or restarting the listener.
/// </summary>
public sealed class Listener
{
    public ListenerId Id { get; }
    public string Name { get; }
    public ListenerTransport Transport { get; }
    public string BindAddress { get; }
    public string PublicEndpoint { get; }
    public DateTimeOffset CreatedAt { get; }
    public ListenerState State { get; private set; }

    private Listener(
        ListenerId id,
        string name,
        ListenerTransport transport,
        string bindAddress,
        string publicEndpoint,
        DateTimeOffset createdAt,
        ListenerState state)
    {
        Id = id;
        Name = name;
        Transport = transport;
        BindAddress = bindAddress;
        PublicEndpoint = publicEndpoint;
        CreatedAt = createdAt;
        State = state;
    }

    /// <summary>
    /// Factory for a listener at startup. The listener begins
    /// <see cref="ListenerState.Stopped"/>; <see cref="Start"/> moves it to
    /// <see cref="ListenerState.Running"/> once the host has bound its socket.
    /// </summary>
    public static Listener Define(
        ListenerId id,
        string name,
        ListenerTransport transport,
        string bindAddress,
        string publicEndpoint,
        DateTimeOffset at)
        => new(id, name, transport, bindAddress, publicEndpoint, at, ListenerState.Stopped);

    /// <summary>
    /// Marks the listener as bound and accepting connections. Only legal from
    /// <see cref="ListenerState.Stopped"/>; a re-call on a running listener is a
    /// no-op. The host drives this from <c>UseRodListeners</c> after Kestrel opens
    /// the socket, so the registry reflects what is actually listening.
    /// </summary>
    public void Start()
    {
        if (State == ListenerState.Running)
            return;
        if (State != ListenerState.Stopped)
            throw new InvalidOperationException($"Listener {Id} cannot start from {State}.");

        State = ListenerState.Running;
    }
}
