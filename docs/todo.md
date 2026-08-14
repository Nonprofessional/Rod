# Rod -- Todo

Open work beyond the [archived roadmap](roadmap.md). The roadmap delivered the
framework and the capability contracts; the items here fill in concrete
behavior, harden the system for real use, and close gaps between
[architecture.md](architecture.md) and the implementation.

Add items freely; check them off as they ship. Each item carries a one-line
acceptance criterion. Keep the [repository conventions](../AGENTS.md): small
focused commits, English only, the offensive-tradecraft boundary
(architecture.md Sec 13), and reference the architecture section, not the
roadmap, from commit bodies. Shipped items keep a one-line outcome; the detail
lives in architecture.md and the commit history.

## Implant verb coverage

- [x] **in-repo verb handlers (recon / lateral / persist / collect / exfil).**
      _AC:_ the recon, lateral, persist, collect, and exfil verbs round-trip end
      to end on the reference implant within the Sec 13 boundary. _(Shipped;
      per-verb surface in architecture.md Sec 10.1.)_
- [x] **`collect.keylog` stays out-of-tree.** _AC:_ the descriptor ships with
      its OPSEC attributes and the reference implant carries no handler.
      _(Shipped; the registry-and-dispatch seam lets an out-of-tree module
      register against it.)_

## Production hardening

- [x] **Operator authentication.** _AC:_ an operator session is established by
      authenticated credentials, not a client-generated id. _(Shipped: cookie
      sessions over a verified handle and password; per-engagement RBAC is
      deliberately out of scope -- the trusted-operators model, Sec 4.1/9.)_
- [x] **Real implant CA.** _AC:_ enrollment binds certificates to a non-dev CA
      chain. _(Shipped: `FileBackedCertificateAuthority` consumes an externally
      provisioned engagement CA, selected by the `Pki` config section,
      architecture.md Sec 9. A proper TLS server leaf + SAN stays a documented
      follow-on.)_
- [x] **Redirector deployment story.** _AC:_ a burned redirector is swapped end
      to end, not just in the registry. _(Shipped: the in-tree .NET Native AOT
      forwarder plus listener repoint; deploy/rotate runbook in
      [operations/redirectors.md](operations/redirectors.md).)_

## Architecture audit and gaps

Keep architecture.md as the source of truth. These items audit the
implementation against it and record decisions.

- [x] **Audit architecture.md vs. implementation.** _AC:_ a written audit
      noting every divergence and its resolution. _(Shipped: Sec 1--14 walked
      with 17 findings; the follow-up commit reconciled the doc and the record
      was removed.)_
- [x] **Capture deferred decisions.** _AC:_ each deferred decision is written
      into architecture.md. _(Shipped: task-argument shape, capability-catalog
      endpoint placement, placeholder-only verbs.)_
- [ ] **Implant-side capability pluggability.** Make the reference implant
      class-aware and handler-registry-driven per
      [architecture.md Sec 5.3](architecture.md): derive the handshake
      capability set from the baked class verbs intersected with the compiled
      handlers (not a hardcoded list), and route dispatch through an
      implant-side handler registry so a new verb is a handler plus a
      registration rather than an edit to the runner. _AC:_ an implant
      advertises exactly the verbs its build permits and its compiled handlers
      implement -- never a verb it cannot run -- and the reference registry
      contains no verb excluded by the Sec 13 boundary.
