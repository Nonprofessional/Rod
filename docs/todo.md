# Rod -- Todo

Work that is out of scope for the [archived roadmap](roadmap.md). The roadmap
delivered the framework and the capability contracts; the items here fill in
concrete behavior, harden the system for real use, and close gaps between
[architecture.md](architecture.md) and the implementation.

Add items freely; check them off as they ship. Each item carries a one-line
acceptance criterion. Keep the [repository conventions](../AGENTS.md): small
focused commits, English only, the offensive-tradecraft boundary (architecture.md Sec 13;
standard, mainstream, documented techniques in-repo; in-the-wild 0days,
weaponized PoCs, novel evasion, LSASS memory dumping, and keyboard capture stay
out-of-tree), and reference the architecture section, not the roadmap, from
commit bodies.

## Implant verb coverage

The capability registry (M2.5/M8.1) registers a placeholder per verb that fails
on dispatch; the reference implant runs the core verbs end to end. These items
give the non-sensitive categories real implant-side handlers so a tasked verb
executes and returns output, not just a Failed result.

- [x] **in-repo verb handlers (recon / lateral / persist / collect / exfil).**
      The reference implant implements every non-sensitive category (architecture.md Sec 13)
      and a tasked verb completes with captured output. _AC:_ the
      recon (`portscan`/`hostenum`/`service`), lateral
      (`move`/`token`/`exec_remote`), persist (`install`/`remove`/`list`),
      collect (`file`/`cred`), and exfil (`push`/`stage`) verbs round-trip end
      to end. _(Shipped within the Sec 13 boundary; see [architecture.md §10.1](architecture.md)
      for the per-verb surface. Novel or stealth techniques, LSASS dumping, and
      input capture stay out-of-tree.)_
- [x] **`collect.keylog` stays out-of-tree.** Keyboard capture has no benign-
      system-tool side and stays contract-only by the Sec 13 boundary. An out-of-tree
      module can register a handler against the existing capability descriptor
      without touching the reference implants. _(Resolved: the descriptor ships
      with its `reads-input`/`persists` OPSEC attributes and the reference
      implant carries no handler; the M8.1 registry-and-dispatch seam lets an
      out-of-tree module register against it and remain the authority, covered
      by `CollectCapabilitiesTests` and `CapabilityRegistryTaskResolverTests`.
      The boundary itself is restated in
      [architecture.md §10.1, §13](architecture.md).)_

## Production hardening

The walking-skeleton defaults are fine for development and tests but not for
real deployments. architecture.md names these; they were deliberately out of
roadmap scope.

- [x] **Operator authentication.** Replace the browser self-assigned identity
      with real operator auth. _AC:_ an operator session is established by
      authenticated credentials, not a client-generated id. _(Shipped: cookie
      sessions over a verified handle and password, identity derived from the
      session principal on every operator endpoint, a config-seeded first
      operator, and a durable `operator_credentials` store (architecture.md Sec 4.3).
      Per-engagement RBAC is deliberately out of scope: Rod follows the mainstream C2 trusted-operators model -- named operators get full access, held accountable through the attributed audit trail.)_
- [x] **Real implant CA.** Replace the dev self-signed CA
      (`DevCertificateAuthority`) with a production CA path. _AC:_ enrollment
      binds certificates to a non-dev CA chain. _(Shipped:
      `FileBackedCertificateAuthority` consumes an externally provisioned
      engagement CA (PEM cert + RSA key on disk) and signs implant leaves with
      the same leaf construction as the dev authority, so only the issuer
      changes; `AddRodTransport` selects it by the `Pki` config section the way
      it selects the audit store, and constructs it eagerly so a bad CA fails
      the host at startup. An integration test enrolls an implant under the
      configured CA and completes the mTLS handshake. See architecture.md Sec 9. A proper TLS server
      leaf + SAN stays a documented follow-on.)_
- [x] **Redirector deployment story.** Document and (where needed) tooling for
      real redirector hosts behind the listener's public endpoint; the in-tree
      direction is a .NET Native AOT forwarder (architecture.md Sec 12.2). _AC:_ a burned
      redirector is swapped end to end, not just in the registry. _(Shipped:
      the in-tree reference redirector -- an opaque L4 TCP forwarder published
      as a Native AOT single binary (`src/redirector/dotnet/`) -- fronts a
      listener and splices the byte stream without inspecting or altering it,
      so the mTLS beacon channel and HTTPS enroll request carry through end to
      end. Together with the M4.4 listener repoint it swaps a burned redirector
      end to end: deploy a fresh forwarder, `POST /listeners/{id}:repoint`,
      decommission the old host. See the deploy/rotate runbook ([operations/redirectors.md](operations/redirectors.md)).)_

## Architecture audit and gaps

Keep architecture.md as the source of truth. These items audit the
implementation against it and record decisions.

- [x] **Audit architecture.md vs. implementation.** Walk every section; record
      where the code diverges and whether each gap is intentional. _AC:_ a
      written audit noting every divergence and its resolution. _(Shipped:
      [audits/2026-08-11-architecture-vs-implementation.md](audits/2026-08-11-architecture-vs-implementation.md)
      walks Sec 1--14 with 17 findings and recommended edits; the follow-up
      commit reconciled the doc with the implementation.)_
- [x] **Capture deferred decisions.** Record decisions that were made
      implicitly during the roadmap (e.g. arg shape staying a single string,
      catalog endpoint placement, placeholder-only verbs). _AC:_ each deferred
      decision is written into architecture.md. _(Shipped: task-argument shape,
      capability-catalog endpoint placement, and placeholder-only verbs are all
      captured in [architecture.md](architecture.md).)_
- [ ] **Implant-side capability pluggability.** Make the reference implant
      class-aware and handler-registry-driven per
      [architecture.md Sec 5.3](architecture.md): derive the
      handshake capability set from the baked class verbs intersected with the
      compiled handlers (not a hardcoded list), and route dispatch through an
      implant-side handler registry so a new verb is a handler plus a
      registration rather than an edit to the runner. _AC:_ an implant
      advertises exactly the verbs its build permits and its compiled handlers
      implement -- never a verb it cannot run -- and the reference registry
      contains no verb excluded by the Sec 13 boundary.
