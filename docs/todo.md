# Rod -- Todo

Work that is out of scope for the [archived roadmap](roadmap.md). The roadmap
delivered the framework and the capability contracts; the items here fill in
concrete behavior, harden the system for real use, and close gaps between
[architecture.md](architecture.md) and the implementation.

Add items freely; check them off as they ship. Each item carries a one-line
acceptance criterion. Keep the [repository conventions](../AGENTS.md): small
focused commits, English only, the offensive-tradecraft boundary of ADR 0004
(standard, mainstream, documented techniques in-repo; in-the-wild 0days,
weaponized PoCs, novel evasion, LSASS memory dumping, and keyboard capture stay
out-of-tree), and reference the architecture section, not the roadmap, from
commit bodies.

## Implant verb coverage

The capability registry (M2.5/M8.1) registers a placeholder per verb that fails
on dispatch; the reference implant runs the core verbs end to end. These items
give the non-sensitive categories real implant-side handlers so a tasked verb
executes and returns output, not just a Failed result.

- [x] **recon handlers.** `recon.portscan`, `recon.hostenum`, `recon.service`
      execute on the .NET reference implant and return structured
      output. _AC:_ a tasked recon verb completes with captured output against
      authorized targets. _(Shipped: the reference implant implements all
      three recon verbs.)_
- [x] **lateral handlers.** `lateral.move` derives and enrolls a child
      (parentage round-trip already ships from M9.1); `lateral.token` and
      `lateral.exec_remote` run in scope. _AC:_ `lateral.move` from a parent
      yields a child whose server-side record matches, and the token/exec verbs
      complete. _(Shipped under ADR 0004: `lateral.token` enumerates the
      Windows access-token context via `whoami`; `lateral.exec_remote` drives
      `schtasks` on Windows and `ssh` on Linux. The reference implant
      implements all three verbs.)_
- [x] **persist handlers.** `persist.install`, `persist.remove`, `persist.list`
      against the reference implant's supported surfaces. _AC:_ install, list,
      and remove round-trip within the engagement. _(Shipped under ADR 0004:
      the documented Windows mechanisms -- Run registry key, scheduled tasks,
      services -- and Linux mechanisms -- cron, systemd user units -- round-trip
      on the reference implant. Novel or stealth persistence stays
      out-of-tree.)_
- [x] **collect/exfil handlers.** `collect.file`, `collect.cred`,
      `exfil.push`, `exfil.stage` move data over the C2 channel into scoped
      storage. _AC:_ collected data exfils and is stored scoped to the
      engagement, retrievable as artifacts. _(Shipped under ADR 0004: `collect.file`
      reads files with chunked streaming for large files; `collect.cred`
      enumerates SSH key fingerprints, AWS profile names, and the Windows
      `cmdkey` listing without dumping secret material; `exfil.push` streams
      files as ExfilChunk frames reassembled into engagement-scoped artifacts on
      the teamserver; `exfil.stage` reports the local staging manifest. The
      ExfilRoundTripTests exercise the end-to-end path through the real beacon
      stream.)_
- [ ] **`collect.keylog` stays out-of-tree.** Keyboard capture has no benign-
      system-tool side and stays contract-only by ADR 0004. An out-of-tree
      module can register a handler against the existing capability descriptor
      without touching the reference implants.

## Production hardening

The walking-skeleton defaults are fine for development and tests but not for
real deployments. architecture.md names these; they were deliberately out of
roadmap scope.

- [x] **Operator authentication.** Replace the browser self-assigned identity
      with real operator auth. _AC:_ an operator session is established by
      authenticated credentials, not a client-generated id. _(Shipped: cookie
      sessions over a verified handle and password, identity derived from the
      session principal on every operator endpoint, a config-seeded first
      operator, and a durable `operator_credentials` store; see ADR 0008.
      Per-engagement RBAC stays deferred.)_
- [x] **Real implant CA.** Replace the dev self-signed CA
      (`DevCertificateAuthority`) with a production CA path. _AC:_ enrollment
      binds certificates to a non-dev CA chain. _(Shipped:
      `FileBackedCertificateAuthority` consumes an externally provisioned
      engagement CA (PEM cert + RSA key on disk) and signs implant leaves with
      the same leaf construction as the dev authority, so only the issuer
      changes; `AddRodTransport` selects it by the `Pki` config section the way
      it selects the audit store, and constructs it eagerly so a bad CA fails
      the host at startup. An integration test enrolls an implant under the
      configured CA and completes the mTLS handshake. See
      [ADR 0010](decisions/0010-production-implant-ca.md). A proper TLS server
      leaf + SAN stays a documented follow-on.)_
- [x] **Redirector deployment story.** Document and (where needed) tooling for
      real redirector hosts behind the listener's public endpoint; the in-tree
      direction is a .NET Native AOT forwarder (ADR 0009). _AC:_ a burned
      redirector is swapped end to end, not just in the registry. _(Shipped:
      the in-tree reference redirector -- an opaque L4 TCP forwarder published
      as a Native AOT single binary (`src/redirector/dotnet/`) -- fronts a
      listener and splices the byte stream without inspecting or altering it,
      so the mTLS beacon channel and HTTPS enroll request carry through end to
      end. Together with the M4.4 listener repoint it swaps a burned redirector
      end to end: deploy a fresh forwarder, `POST /listeners/{id}:repoint`,
      decommission the old host. See [ADR 0011](decisions/0011-redirector-design.md)
      and the deploy/rotate runbook ([operations/redirectors.md](operations/redirectors.md)).)_
- [ ] **Teamserver observability.** Structured logs, metrics, and health
      beyond `GET /health` on the server side. _AC:_ operator activity and the
      ingested C2 event stream (beacon check-ins, task results, exfil) are
      queryable in the teamserver's own telemetry surface.
- [x] **No implant-side telemetry (non-goal).** The reference implant carries
      no logging or metrics SDK and no exporter; its activity is observable
      only through the C2 stream the teamserver records. _AC:_ the implant
      artifact stays at its current 3-package footprint and opens no second
      network destination beyond the C2 channel. _(Resolved: implant-side
      telemetry is excluded on footprint and OPSEC grounds -- an exporter
      phoning a metrics backend is a detection signal. Out-of-tree implants may
      ship their own; the reference .NET implant does not.)_

## Architecture audit and gaps

Keep architecture.md as the source of truth. These items audit the
implementation against it and record decisions.

- [x] **Audit architecture.md vs. implementation.** Walk every section; record
      where the code diverges and whether each gap is intentional. _AC:_ a
      written audit noting every divergence and its resolution. _(Shipped:
      [audits/2026-08-11-architecture-vs-implementation.md](audits/2026-08-11-architecture-vs-implementation.md)
      walks Sec 1--14 with 17 findings and recommended edits; the follow-up
      commit reconciled the doc with the implementation.)_
- [x] **ADRs for deferred decisions.** Capture decisions that were made
      implicitly during the roadmap (e.g. arg shape staying a single string,
      catalog endpoint placement, placeholder-only verbs). _AC:_ each deferred
      decision has an ADR under docs/decisions/. _(Shipped: [ADR 0005](decisions/0005-task-arguments-single-string.md)
      task-argument shape, [ADR 0006](decisions/0006-capability-catalog-endpoint.md)
      catalog endpoint placement, [ADR 0007](decisions/0007-placeholder-verbs.md)
      placeholder-only verbs -- all Accepted.)_
- [ ] **Implant-side capability pluggability.** Make the reference implant
      class-aware and handler-registry-driven per
      [ADR 0012](decisions/0012-implant-capability-pluggability.md): derive the
      handshake capability set from the baked class verbs intersected with the
      compiled handlers (not a hardcoded list), and route dispatch through an
      implant-side handler registry so a new verb is a handler plus a
      registration rather than an edit to the runner. _AC:_ an implant
      advertises exactly the verbs its build permits and its compiled handlers
      implement -- never a verb it cannot run -- and the reference registry
      contains no ADR 0004 verb.
