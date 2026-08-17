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
test groundwork has shipped. Sealing was cut from this list back to Sec 9's
future pool: the L4 opaque redirector leaves it without a concrete adversary
today, and it would break the implant contract's no-mandatory-crypto rule.
Order: ROE before revocation, since revocation's denylist pattern follows the
gate shape ROE establishes.

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
- [x] **ROE guardrails.** Shipped: an engagement's ROE profile
      (`PermittedVerbs` with `namespace.*` wildcards, `PermittedImplants`;
      each empty = unrestricted) gates task issuance server-side after the
      class gate and before queuing, applied over `PUT /engagements/{id}/roe`;
      refusals answer `422` and append a `TaskRoeRefused` audit event naming
      the violated rule, and the scope change is recorded as `RoeUpdated`
      (architecture.md Sec 9). _AC:_ a task outside the engagement's ROE
      profile is refused at queue time with an audit entry naming the
      violated rule.
- [x] **Certificate revocation.** Shipped application-layer, both halves:
      the implant half is retirement itself (next handshake refused, pinned
      by HandshakeServiceTests), and the operator half is
      `POST /operators/{id}/credentials:revoke`, which deletes the stored
      password verifier so the next login fails -- login reads the verifier
      fresh per attempt, so no restart is involved; re-provisioning restores
      login (architecture.md Sec 9). CRL/OCSP was rejected as heavier than
      the threat. _AC:_ revoking an operator credential or implant identity
      takes effect on the next authentication attempt without a server
      restart.

## Tests

- [x] **End-to-end integration path.** Shipped: `EngagementLoopTests` walks
      the engagement-critical loop against a real mTLS Kestrel host in one
      test run -- handshake, signed task dispatch (signature verified the way
      the implant verifies), captured result with its three-event audit arc,
      exfil chunk capture into an artifact read back through the API, seeded
      history walked through the paginated task and artifact listings, and a
      silently abandoned stream swept closed so the recovered implant
      re-handshakes and drains the queued tasking. Coordination is polling
      readback, never sleeps (architecture.md Sec 4.3, Sec 10.3, Sec 11).
      _AC:_ the green path is asserted end-to-end in one test run without
      sleeps coordinating the parties.
- [x] **Green-board sweep.** Adopted as the working gate: before opening each
      item this round the sweep ran (`dotnet format Rod.slnx
      --verify-no-changes` plus the full solution test run) and stayed clean;
      it is the habit, not a deliverable. _AC:_ the sweep runs clean on demand
      with no manual fix-up steps.
