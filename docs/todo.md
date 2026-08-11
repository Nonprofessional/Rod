# Rod -- Todo

Work that is out of scope for the [archived roadmap](roadmap.md). The roadmap
delivered the framework and the capability contracts; the items here fill in
concrete behavior, harden the system for real use, and close gaps between
[architecture.md](architecture.md) and the implementation.

Add items freely; check them off as they ship. Each item carries a one-line
acceptance criterion. Keep the [repository conventions](../AGENTS.md): small
focused commits, English only, sensitive-capability discipline (concrete
evasion/exploit tradecraft stays out-of-tree), and reference the architecture
section, not the roadmap, from commit bodies.

## Implant verb coverage

The capability registry (M2.5/M8.1) registers a placeholder per verb that fails
on dispatch; the reference implants run the core verbs end to end. These items
give the non-sensitive categories real implant-side handlers so a tasked verb
executes and returns output, not just a Failed result.

- [ ] **recon handlers.** `recon.portscan`, `recon.hostenum`, `recon.service`
      execute on the Go and .NET reference implants and return structured
      output. _AC:_ a tasked recon verb completes with captured output against
      authorized targets.
- [ ] **lateral handlers.** `lateral.move` derives and enrolls a child
      (parentage round-trip already ships from M9.1); `lateral.token` and
      `lateral.exec_remote` run in scope. _AC:_ `lateral.move` from a parent
      yields a child whose server-side record matches, and the token/exec verbs
      complete.
- [ ] **persist handlers.** `persist.install`, `persist.remove`, `persist.list`
      against the reference implant's supported surfaces. _AC:_ install, list,
      and remove round-trip within the engagement.
- [ ] **collect/exfil handlers.** `collect.file`, `collect.cred`,
      `collect.keylog`, `exfil.push`, `exfil.stage` move data over the C2
      channel into scoped storage. _AC:_ collected data exfils and is stored
      scoped to the engagement, retrievable as artifacts.

## Production hardening

The walking-skeleton defaults are fine for development and tests but not for
real deployments. architecture.md names these; they were deliberately out of
roadmap scope.

- [ ] **Operator authentication.** Replace the browser self-assigned identity
      with real operator auth. _AC:_ an operator session is established by
      authenticated credentials, not a client-generated id.
- [ ] **Real implant CA.** Replace the dev self-signed CA
      (`DevCertificateAuthority`) with a production CA path. _AC:_ enrollment
      binds certificates to a non-dev CA chain.
- [ ] **Redirector deployment story.** Document and (where needed) tooling for
      real redirector hosts behind the listener's public endpoint. _AC:_ a
      burned redirector is swapped end to end, not just in the registry.
- [ ] **Observability.** Structured logs, metrics, and health beyond
      `GET /health`. _AC:_ operator and implant activity is observable in a
      production target's telemetry stack.

## Architecture audit and gaps

Keep architecture.md as the source of truth. These items audit the
implementation against it and record decisions.

- [ ] **Audit architecture.md vs. implementation.** Walk every section; record
      where the code diverges and whether each gap is intentional. _AC:_ a
      written audit noting every divergence and its resolution.
- [ ] **ADRs for deferred decisions.** Capture decisions that were made
      implicitly during the roadmap (e.g. arg shape staying a single string,
      catalog endpoint placement, placeholder-only verbs). _AC:_ each deferred
      decision has an ADR under docs/decisions/.
