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

The capability bar demands tunneling on par with the best available
(Sec 14), and the pivot class exists only as a reserved empty verb set
(Sec 5.2) -- the one capability area the bar names with nothing shipped.

- [ ] **Tunnel verbs on the wire contract.** Design the tunnel verb set
      (port forward and/or SOCKS), its frame shape (new frame kinds or the
      interactive-channel machinery), and how a pivot enrols and forwards
      for hosts that cannot run their own implant; admit the verbs into the
      pivot class set so the class stops admitting nothing. _AC:_ a task on
      a stage-2 implant reaches a third host through a pivot's tunnel, and
      the traffic is attributed end to end (task, audit trail, operator
      view).

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
