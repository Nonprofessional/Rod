# Adding a transport

The transport extension contract: how a new C2 carrier joins Rod, in-tree
or out. The design authority is [architecture.md Sec 8](../architecture.md);
this document is the builder's manual for the seams that section names.

## The model

A transport is three declarations, not a switch arm:

1. **The bind behavior** — `ITransportProvider`
   (`Rod.Transport.Listeners.Providers`): how a listener of this transport
   opens its socket, validates its bind address, reserves its port, and
   shapes its public endpoint.
2. **The carriers it serves** — wire names from the core-state capability
   table (`Rod.CoreState.Transports.TransportCapabilities`): what check-in
   shapes the transport can carry, which is what the build pipeline and the
   issuance gate read.
3. **The registry entries** — `TransportProviders.Register(provider)` for
   the bind, `TransportCapabilities.Register` for any new carrier. Both are
   name-keyed, conflict-refusing, and conservative toward what never
   registered: an unregistered name answers "no channels" and binds
   nothing.

The in-tree seven (`http`, `https`, `mtls`, `dns`, `smb`, `tcp`, `quic`)
register in the static constructors and are the reference implementations.
The listener record, the operator API, the persistence, and the restore path
all speak the wire name -- a registered transport is nameable the moment it
registers, with no core edits.

## The provider contract

```csharp
public interface ITransportProvider
{
    string Transport { get; }                     // the wire name
    IReadOnlyList<string> Carriers { get; }       // carriers served, by wire name
    bool ServesNativeChannel { get; }             // may a build name this as its beacon?
    string PublicEndpointScheme { get; }          // scheme for endpoint completion
    bool AcceptsPublicEndpoint(string text);      // the dial shape's validation
    string DescribePublicEndpointRule(string got);// the refusal that teaches
    void Validate(ListenerConfig config);         // 400-class bind-address checks
    void ReserveBind(ListenerConfig config);      // 409-class port reservation
    Task<BoundListener> BindAsync(                // bind, register, return
        TransportBindContext context, CancellationToken cancellationToken);
}
```

Two shared shapes cover the current family, and a transport that fits
neither implements the interface directly:

- **`KestrelEndpointProvider`** — the HTTP family: the listener rides
  Kestrel's endpoint-configuration reloader under a declared
  `ListenerTlsPosture` (scheme + client-certificate mode as data: plain,
  server-TLS, or the certificate-asking mTLS shape). The per-transport
  differences are constructor parameters: the posture, the carriers, and
  (optionally) a public-endpoint shape other than the web dial.
- **`HostedServiceTransportProvider`** — the socket-owning family: each
  listener runs a hosted service built by a factory, with the bind shape
  (pipe-name vs host:port, UDP vs TCP reservation) and the public-endpoint
  dial shape as data.

## The carrier table

```csharp
ChannelSupport: Native | Degraded | None
```

- **Native** — the transport holds a live stream; channel verbs claim on
  it, and a build may name a listener of this transport as its beacon.
- **Degraded** — a poll shape whose unit can carry channel traffic both
  ways; channel verbs claim only against a session that advertised the
  opt-in (the handshake capability `channels.poll` from a
  `degradedChannels` bake).
- **None** — a datagram-shaped poll; channel verbs never claim.

Registering a new carrier (`TransportCapabilities.Register(name, new
CarrierCapabilities(...))`) with `Native` support is what makes a transport
beacon-nameable; everything downstream -- the parser's beacon rule, the
issuance gate, the enrollment's baked-carrier stamp -- reads the table, so
no other edit is needed for that half.

## Identity and auth

A transport declares which of the three identity models its check-ins
carry, by construction rather than by registry entry:

- **Client certificate** (the mTLS shape): the TLS layer asks -- a presented
  certificate must chain to the CA, and none is demanded in-handshake
  (enrollment precedes the leaf) -- so the handshake's
  `(implant_id, engagement_id)` check is the enforcement: a certificate-less
  connection completes TLS but opens no session.
- **Application-layer key** (the web family): the per-artifact key seals
  the check-in bodies; possession is the authentication.
- **Id alone** (the DNS/SMB/TCP/QUIC family): the egress-restricted
  tradeoff, documented in the contract per transport -- QUIC pairs it with
  server-side TLS (chain-to-CA pinned), so the transport is encrypted even
  though the implant itself is not certificate-authenticated.

A new transport picks one and says so in its XML docs and its
`extending/implants.md` section; the wire contract is the product, and the
contract doc is where an implant author reads what your transport promises.

## In-tree or out

The same registration serves both. In-tree transports follow the
sensitive-capability discipline (architecture.md Sec 13): standard,
documented, mainstream techniques ship in the core; anything novel --
protocol mimicry, unpublished evasion -- registers out-of-tree against
this same contract and never touches the repository. An out-of-tree
provider links `Rod.Transport` (or reimplements against the registry
semantics), registers at startup, and is nameable: the string spine makes
the core indifferent to who registered the name.

## Checklist

1. Pick the wire name and the carriers it serves.
2. Implement or parameterize a provider; register it in
   `TransportProviders` (in-tree) or at host start (out-of-tree).
3. Register any new carrier in `TransportCapabilities` with its honest
   `ChannelSupport`.
4. Write the wire contract section in `extending/implants.md` -- the
   frames, the auth, the bounds. The transport changes; the frame paths
   never do.
5. Pin it: a registry test for the carriers, a listener round-trip for
   the bind, and -- if the carrier is native -- a build/issuance test
   that names a listener of the new transport as its beacon.
