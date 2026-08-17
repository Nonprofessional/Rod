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

## Security (architecture.md Sec 9)

The designed-but-deferred items, promoted here now that the functional and
test groundwork has shipped. Order matters: signing before sealing, sealing
before ROE, revocation last since it builds on the cert story both depend on.

- [x] **Command signing.** Shipped: the beacon endpoint signs each dispatched
      `TaskRequest` with the tasking CA's RSA key (RSASSA-PSS/SHA-256 over the
      canonical length-prefixed implant/task tuple documented in rod.proto --
      the implant id binds the task to its executor, so captured tasking does
      not verify on another implant), and the implant verifies against the CA
      certificate it already holds from enrollment before any handler runs; a
      rejected task reports `Failed` with the cause, so the operator sees it
      on the task (architecture.md Sec 9). _AC:_ an unsigned or wrongly
      signed command is rejected by the implant and the rejection is visible
      on the operator console.
- [ ] **Sealing.** Tasked payloads are sealed to the target session key so
      artifacts in the stager and audit trail carry no plaintext command
      material (architecture.md Sec 9).
      _AC:_ a stager blob and an audit record for the same task cannot be
      decoded without the session key.
- [ ] **ROE guardrails.** A per-engagement rules-of-engagement profile gates
      which capabilities and targets are taskable, enforced server-side
      before a task is queued (architecture.md Sec 9).
      _AC:_ a task outside the engagement's ROE profile is refused at queue
      time with an audit entry naming the violated rule.
- [ ] **Certificate revocation.** Operator and implant credentials gained a
      revocation path: a revoked credential fails authentication on its next
      use rather than living out its natural expiry (architecture.md Sec 9).
      _AC:_ revoking an operator credential or implant identity takes effect
      on the next authentication attempt without a server restart.

## Tests

- [ ] **End-to-end integration path.** Unit and hammer coverage is in place,
      but no test drives the full implant-to-teamserver-to-operator loop.
      One integration test host runs a real beacon stream, a teamserver, and
      an operator client, and walks the engagement-critical loop: handshake,
      task dispatch, staged artifact retrieval, staleness re-handshake after
      a silent stream death, and paginated list walking over a seeded
      history (architecture.md Sec 4.3, Sec 10.3, Sec 11).
      _AC:_ the green path is asserted end-to-end in one test run without
      sleeps coordinating the parties.
- [ ] **Green-board sweep.** A periodic gate rather than new coverage: run
      `dotnet format Rod.slnx --verify-no-changes` and the full test suite
      over the solution before opening each new work item, so formatting
      and test debt never accumulates behind feature work.
      _AC:_ the sweep runs clean on demand with no manual fix-up steps.
