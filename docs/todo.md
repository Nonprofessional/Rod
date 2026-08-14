# Rod -- Todo

Open work only: completed items are checked off and trimmed, and their detail
lives in the commit history and [architecture.md](architecture.md). The
designed-but-deferred security items (command signing, sealing, ROE
guardrails, cert revocation) stay in architecture.md Sec 9.

Add items freely; check them off as they ship. Each item carries a one-line
acceptance criterion. Keep the [repository conventions](../AGENTS.md): small
focused commits, English only, the offensive-tradecraft boundary
(architecture.md Sec 13), and reference the architecture section, not a
historical milestone id, from commit bodies.

## Implant

- [x] **Implant-side capability pluggability.** Shipped: the beacon
      advertises the baked class verbs intersected with the compiled handlers,
      and dispatch routes through the implant-side handler registry
      (architecture.md Sec 5.3).

## Teamserver

- [ ] **Out-of-tree module loading.** Give out-of-tree capability modules a
      supported registration path (assembly scan or a config-listed type
      list) so adding one never edits the composition root, and settle the
      CapabilityDispatcher contract: registered today, invoked nowhere. _AC:_
      a module built against the contract loads and replaces its placeholder
      without touching core code.
- [ ] **Session staleness sweep.** Touch is wired on every beacon frame, but
      a stream that dies silently leaves the session active forever. _AC:_ a
      session whose last-seen is older than a configured threshold is closed
      and the implant drops off the online roster.
- [ ] **List pagination.** Task, audit, and artifact listings return the full
      history; a long engagement grows these without bound. _AC:_ list
      endpoints accept a cursor or limit and the UI walks pages.

## Tests

- [ ] **Protocol layer rule.** The architecture tests assert every layer's
      dependency matrix except Protocol's own "depends on nothing in-house"
      rule, and a dead (unused) project reference passes because the checks
      inspect namespace usage. _AC:_ Protocol's rule is tested and a
      forbidden csproj reference fails even when no code uses it.
- [ ] **Concurrency coverage.** The shared in-memory stores and the live bus
      use locks and Interlocked with no hammer tests. _AC:_ multi-threaded
      tests exercise the task claim, stager redemption, audit append, and
      live-bus subscribe/publish paths.
