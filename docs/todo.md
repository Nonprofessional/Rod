# Rod -- Todo

Open work only. An item leaves this file the moment it ships -- its record
is the commit history, and the design it followed lives in
[architecture.md](architecture.md); the one designed-but-deferred item
(sealing) stays in Sec 9. Nothing here is an archive of the done.

Each item names the architecture section it serves and carries a one-line
acceptance criterion (_AC:_), so "done" stays testable. Follow the
[repository conventions](../AGENTS.md).

Lean is the standing default: an addition must say what an engagement
cannot do without it; refactors, deletions, and answering with docs
instead of code are first-class items here, equal to features. New work
starts from a gap an actual engagement surfaces.

## Transports and identity (architecture.md Sec 8, Sec 9)

The transport layer now runs on the published contract
([extending/transports.md](extending/transports.md)): a transport
registers a provider and its carriers, and edits no core. The items below
are the open ends of that surface and the gaps an engagement can hit.

- [ ] **Add the QUIC check-in transport.** An engagement whose egress
      passes UDP/443 (where HTTP/3-era traffic lives) but blocks TCP has
      no shape today: the socket-owning family covers TCP, the pipe, and
      the datagram, not the QUIC stream. Build it as the duplex variant
      of the socket-owning family through the published contract
      (System.Net.Quic listener, the self-delimited message framing over
      its streams), declare its carriers honestly -- duplex means native
      channels -- and give it the web-posture TLS story a QUIC front
      needs. _AC:_ a `quic` listener entry created through the operator
      API carries a check-in end to end, with the provider registration
      as the only core-side change.

- [ ] **Resolve the https enrollment-ingress gap.** The local-port
      lookup that stamps an enrollment's listener matches only `http`
      and `mtls` listeners, so an enrollment arriving on an `https`
      listener resolves no ingress: the implant record carries no
      listener id (the listener-delete guard misses it) and the token's
      engagement-scope check against the socket is skipped. Widen the
      match to the Kestrel family that serves enrollment, or name why
      `https` is excluded. _AC:_ an enrollment through an `https`
      listener records its listener id, and a foreign engagement's token
      is refused on that socket.

- [ ] **Unify or document the two mTLS bind postures.** The
      startup-configured mTLS endpoint requires the client certificate
      at the TLS layer; a runtime-created mTLS listener requests it
      optionally and enforces the binding at the application layer. Two
      postures for one transport is a decision that has not been made:
      pick one (or name both as deliberate tiers) and write the rule
      into Sec 8/9. _AC:_ both bind paths enforce the documented
      posture, and the doc names exactly one rule.

- [ ] **Close the dispatch strand on a dying stream.** A claimed task
      whose frame was written into a closing connection marks
      Dispatched and never redelivers: the requeue covers only the
      failed write (architecture.md Sec 10.3), and below the result no
      delivery evidence exists -- the server cannot tell a frame the
      implant parsed from one that died with the connection. Add a
      receive-ack frame to the wire contract (the implant acks a
      parsed task before executing it), negotiate it at handshake so
      unupgraded implants keep today's semantics, requeue ack-less
      dispatches at stream end, and make duplicate results idempotent
      (first result wins). _AC:_ a task whose frame rides a stream that
      dies before the ack is redelivered on the next check-in, and an
      implant that already held it re-acks without running it twice.
