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
end-to-end attribution (Sec 5.2, Sec 10.1, Sec 10.3). The pivot class admits
the tunnel set and stops admitting nothing; what stays open is the second arm
and the fronting executor for unplantable hosts.

- [ ] **SOCKS arm and an operator-side relay bind.** `tunnel.socks` (a SOCKS
      listener the implant bridges, so unmodified tooling routes through the
      pivot without per-connection tasking) and/or a teamserver-bound relay
      port that bridges a local TCP listener into a live channel, so an
      operator's browser or proxy chains onto the tunnel instead of driving
      it by input posts. _AC:_ an unmodified operator-side tool reaches a
      third host through a stage-2 implant's tunnel without a per-byte API
      call, and the flow is attributed (task, audit trail, operator view).
- [ ] **Pivot fronting executor.** A pivot session enrolled by a parent
      (lateral.move, class Pivot) has no process of its own: its tasking must
      be claimed and executed by the parent's beacon stream. The wire needs a
      target-implant marking on forwarded tasking (the signature already
      binds the target id, Sec 9; the frame does not carry it), the dispatch
      claim widens to the fronted pivots, and channel input routes through
      the fronting stream's sink. _AC:_ a `tunnel.forward` task issued to a
      pivot-child session executes on its parent and reaches the third host,
      attributed to the pivot session end to end.

## Transports (architecture.md Sec 8)

SMB and TCP are the remaining planned listener transports; the listener
abstraction is in place, so each is a milestone concern, not an
architectural one (Sec 8).

- [ ] **SMB listener.** Named-pipe check-ins for Windows segments where
  neither HTTP nor DNS egress is available, carrying the same rod.v1
  frames. _AC:_ an implant written from the contract doc completes a
  check-in and a task over a named pipe through the shared frame paths.
- [ ] **TCP listener.** A raw-TCP check-in transport for segment networks
  that allow arbitrary sockets but no HTTP shape, again carrying the same
  frames. _AC:_ an implant written from the contract doc completes a
  check-in and a task over the raw listener.

## Security follow-ups (architecture.md Sec 9)

- [ ] **End live operator sessions on credential revoke.** A revoked
      credential stops new logins, but active cookie sessions outlive the
      credential they were issued from (Sec 9, certificate revocation) --
      ending them on revoke is the recorded separate hardening. _AC:_
      revoking an operator's credential ends its live cookie sessions; the
      next request on that cookie is refused.
- [ ] **Durable replay-nonce floor.** The per-implant nonce counter lives in
      the TaskService process (Sec 9, tasking replay nonces); move it behind
      the task repository so a durable (Postgres) deployment keeps the floor
      across a restart. _AC:_ with the durable store configured, a restarted
      teamserver's next dispatch for a negotiating implant continues past
      the pre-restart count -- the floor does not reset.
- [ ] **Operator API tokens.** The Sec 9 identity model lists API tokens;
      only password logins with cookie sessions ship. Tokens minted per
      operator, honored by the operator API alongside cookies, and revocable
      like credentials. _AC:_ an operator-API call authenticated by a minted
      token (no cookie) succeeds, and a revoked token is refused.

## Polish

- [ ] **Listener transport labels.** The envelope transport stringifies as
      `httpsenvelope` in the listener listing (`ListenerTransport.ToString()`
      lower-cased); give listings and the UI a stable kebab-case name.
      _AC:_ the listener listing and the operator UI render `https-envelope`.
