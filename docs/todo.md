# Rod -- Todo

Open work only. An item leaves this file the moment it ships -- its record
is the commit history, and the design it followed lives in
[architecture.md](architecture.md); the one designed-but-deferred item
(sealing) stays in Sec 9. Nothing here is an archive of the done.

Each item names the architecture section it serves and carries a one-line
acceptance criterion (_AC:_), so "done" stays testable. Keep the
[repository conventions](../AGENTS.md): small focused commits, English
only, the offensive-tradecraft boundary (architecture.md Sec 13), and cite
the architecture section, never a historical milestone id, from commit
bodies.

## Tunneling and the pivot class (architecture.md Sec 5.2, Sec 14)

`tunnel.forward` ships as the first tunnel verb -- a live channel bridging
operator traffic to a TCP connection the implant opens from its own vantage,
admitted to the Stage-2 and Pivot class sets, with the reference handler and
end-to-end attribution (Sec 5.2, Sec 10.1, Sec 10.3). The operator-side relay
bind has shipped with it: a teamserver-bound TCP listener bridged onto the
dispatched channel, so unmodified tooling rides the tunnel without per-byte
API posts, its bind and close audited alongside the task's transcript
(Sec 10.1). What stays open is the SOCKS arm and the fronting executor for
unplantable hosts.

- [ ] **SOCKS arm.** `tunnel.socks`: a SOCKS listener the implant bridges
      over one channel -- connection-id framing inside the tunnel channel's
      own byte grammar, so unmodified tooling routes through the pivot to
      arbitrary destinations without per-connection tasking. Extends the
      relay bind's single fixed destination into a proper proxy surface.
      _AC:_ an operator's SOCKS-configured browser reaches arbitrary third
      hosts through one stage-2 tunnel task, every connection and its bytes
      attributed (task, audit trail, operator view).
- [ ] **Pivot fronting executor.** A pivot session enrolled by a parent
      (lateral.move, class Pivot) has no process of its own: its tasking must
      be claimed and executed by the parent's beacon stream. The wire needs a
      target-implant marking on forwarded tasking (the signature already
      binds the target id, Sec 9; the frame does not carry it), the dispatch
      claim widens to the fronted pivots, and channel input routes through
      the fronting stream's sink. _AC:_ a `tunnel.forward` task issued to a
      pivot-child session executes on its parent and reaches the third host,
      attributed to the pivot session end to end.

## Security follow-ups (architecture.md Sec 9)

None open: ending live sessions on credential revoke, the durable
replay-nonce floor, and operator API tokens have shipped. The one
designed-but-deferred item (sealing) stays in Sec 9.
