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
/// domain, a different host -- without reconfiguring or restarting the listener:
/// <see cref="Repoint"/> swaps the public endpoint at runtime (architecture.md
/// Sec 7/8, M4.4), leaving the bound socket untouched. Repointing away from an
/// endpoint severs it -- the registry's public-endpoint lookup no longer resolves
/// it -- so a burned redirector is retired by pointing its listener elsewhere.
/// </summary>
public sealed class Listener
{
    public ListenerId Id { get; }
    public string Name { get; }
    public ListenerTransport Transport { get; }
    public string BindAddress { get; }
    public string PublicEndpoint { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? RepointedAt { get; private set; }
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

    /// <summary>
    /// Swaps the public endpoint -- the redirector or host-header implants dial --
    /// without touching the bound socket (architecture.md Sec 7/8, M4.4). The bind
    /// address is unchanged, so a live listener keeps serving; only the address
    /// implants are told to dial moves. This is how a burned redirector is
    /// replaced without backend change: repoint to a fresh redirector and the old
    /// endpoint stops resolving. Severing a redirector outright is the same op --
    /// point its listener at the new endpoint, and the old one no longer maps to
    /// anything. Validates the new endpoint is non-blank.
    /// </summary>
    public void Repoint(string publicEndpoint, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(publicEndpoint))
            throw new ArgumentException("Public endpoint is required.", nameof(publicEndpoint));

        PublicEndpoint = publicEndpoint;
        RepointedAt = at;
    }
}
