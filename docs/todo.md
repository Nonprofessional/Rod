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

- [x] **Out-of-tree module loading.** Give out-of-tree capability modules a
      supported registration path (assembly scan or a config-listed type
      list) so adding one never edits the composition root, and settle the
      CapabilityDispatcher contract: registered today, invoked nowhere.
      Shipped: `Tradecraft:Modules` lists `Namespace.Type, AssemblyName`
      entries, loaded at startup and registered over the placeholders (last
      registration wins), with bad entries failing startup loudly; the
      contract is settled as registration-only -- the server gates and
      forwards, dispatch lives on the implant, and the retired server-side
      dispatcher surface is documented as such (architecture.md Sec 10.2).
      _AC:_ a module built against the contract loads and replaces its
      placeholder without touching core code.
- [x] **Session staleness sweep.** Touch is wired on every beacon frame, but
      a stream that dies silently leaves the session active forever. Shipped:
      a hosted sweeper closes sessions older than `Sessions:Staleness:Threshold`,
      fans a `SessionClosed` live event per close, and the beacon stream ends
      itself on the next frame so a recovered implant re-handshakes
      (architecture.md Sec 10.3). _AC:_ a session whose last-seen is older than
      a configured threshold is closed and the implant drops off the online
      roster.
- [x] **List pagination.** Task, audit, and artifact listings return the full
      history; a long engagement grows these without bound. Shipped: the three
      list endpoints accept a limit and an opaque cursor (newest window first),
      every store adapter -- in-memory, file-backed, and Postgres -- pages with
      the same semantics, and the UI walks pages with load-older controls
      (architecture.md Sec 4.3, Sec 11). _AC:_ list endpoints accept a cursor
      or limit and the UI walks pages.

## Tests

- [x] **Protocol layer rule.** The architecture tests assert every layer's
      dependency matrix except Protocol's own "depends on nothing in-house"
      rule, and a dead (unused) project reference passes because the checks
      inspect namespace usage. Shipped:
      `Protocol_Dependencies_PointInwardOnly` asserts the contract project
      depends on nothing in-house like the other layers, and the new
      `ProjectReferenceTests` read the csproj reference edges against the
      allowed matrix, so a forbidden reference fails the build even when no
      code uses it (architecture.md Sec 4.1). _AC:_ Protocol's rule is
      tested and a forbidden csproj reference fails even when no code uses it.
- [x] **Concurrency coverage.** The shared in-memory stores and the live bus
      use locks and Interlocked with no hammer tests. Shipped: real
      multi-threaded hammer tests (gated worker tasks) in
      `Rod.CoreState.Tests`, `Rod.Audit.Tests`, and the new
      `Rod.Operators.Tests` drive the task claim (every task handed out
      exactly once), stager redemption (a single-use token redeems exactly
      once), audit append (a concurrent trail reconstructs as one valid hash
      chain), and live-bus subscribe/publish (duplicate-free, engagement-
      scoped fan-out under churn) paths (architecture.md Sec 4.1, Sec 10.3,
      Sec 11). _AC:_ multi-threaded tests exercise the task claim, stager
      redemption, audit append, and live-bus subscribe/publish paths.
