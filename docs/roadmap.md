# Rod -- Roadmap

The ordered implementation plan for Rod. Work proceeds in small, verifiable
increments. This file sequences work; it is not design truth. Design lives in
[architecture.md](architecture.md); if a task contradicts it, the design wins.

> **Status: complete and archived (M0.1 through M11.1).** Every milestone below
> is delivered. This file is kept as the historical record of what each
> milestone's acceptance criteria were: source comments and past commits still
> reference these ids (e.g. `roadmap M1.4`), and they resolve here. New work is
> tracked in [todo.md](todo.md); do not add to this file.

Legend: _AC_ = acceptance criteria (in addition to the repository Definition of
Done).

The milestones follow the six internal layers of the teamserver and the
operational lifecycle (see [architecture.md](architecture.md)).

## Milestone 0 -- Tooling and guardrails

- [x] **M0.1 Central package management.** `Directory.Packages.props` at the repo
      root with `ManagePackageVersionsCentrally=true`; all teamserver package
      versions tracked centrally, versionless `PackageReference` in projects.
      _AC:_ `dotnet build`/`test` green; no `Version=` on any `PackageReference`.
- [x] **M0.2 Wire protocol bindings.** The `rod` wire protocol `.proto`
      definitions and generated bindings, plus a frame round-trip smoke test.
      _AC:_ regeneration is part of the build; build/test green.
- [x] **M0.3 Architecture tests.** Encode the layered dependency rules: core
      state and audit depend on nothing in-house; transport, build pipeline, and
      tradecraft depend inward only; protocol types never leak into core.
      _AC:_ adding a forbidden reference fails a test.
- [x] **M0.4 CI.** Build, test, format check, and secret scanning on every
      change. _AC:_ pipeline is green on a clean tree.

## Milestone 1 -- Walking skeleton

Vertical slice proving the end-to-end shape. In-memory implementations behind
ports; no Postgres yet.

- [x] **M1.1 Engagement core + first use cases.** Entities `Engagement`,
      `Operator`, `EngagementMembership`, `Role`; value objects; create an
      engagement and mint a stager token over HTTP.
      _AC:_ create an engagement and mint a token in an integration test.
- [x] **M1.2 Enrollment slice.** `Implant` entity; CA port + self-signed dev CA
      adapter; enrollment service mapping to protocol status codes; token
      semantics (bounded use, expiry).
      _AC:_ enroll a fake implant and receive a certificate bound to
      `(implant_id, engagement_id)` plus the CA chain.
- [x] **M1.3 Handshake and presence.** Bidirectional stream; mTLS certificate vs
      identity check; version/capability advertisement; presence/online state.
      _AC:_ a connecting implant appears online in its engagement.
- [x] **M1.4 First task round-trip.** `Task` lifecycle; a core verb
      (`shell.exec` one-shot) dispatched and its result captured; an audit event
      written. _AC:_ task an implant, see output and an audit event.
- [x] **M1.5 Minimal operator UI.** List engagements/sessions; issue a task; view
      results. _AC:_ the whole M1 slice is demoable in a browser.

## Milestone 2 -- Monolithic kernel, six layers

- [x] **M2.1 Core state layer.** Implant/session registry, task queue and
      history, engagement/operator state, behind ports. _AC:_ round-trips in
      unit tests; layer has no in-house dependencies.
- [x] **M2.2 Transport layer.** Listener abstraction; at least HTTP(S) and mTLS
      listeners; listener decoupled from the public endpoint.
      _AC:_ a listener accepts an implant connection end-to-end.
- [x] **M2.3 Storage and audit layer.** Append-only, hash-chained `AuditEvent`
      store; artifact store. _AC:_ tampering breaks the chain; artifacts attach
      to tasks.
- [x] **M2.4 Operator layer.** Multiplayer sessions; shared live state; task
      ownership/attribution. _AC:_ two operators see each other's actions live.
- [x] **M2.5 Tradecraft layer skeleton.** Capability-module registration and
      dispatch; core verbs load through it. _AC:_ a stub module registers and is
      dispatched.

## Milestone 3 -- Payload build pipeline and polyglot implants

- [x] **M3.1 Build contract.** The payload-build message schema and the
      teamserver-side orchestrator that drives build units.
      _AC:_ requesting a payload invokes a (stub) build unit and returns an
      artifact, fingerprinted and recorded.
- [x] **M3.2 Reference Go implant + build unit.** A Go implant that enrolls,
      beacons, and runs core verbs, built via its own build unit.
      _AC:_ Go implant checks in and tasks end-to-end. _(Superseded by
      [ADR 0009](decisions/0009-single-in-tree-toolchain-dotnet.md): the in-tree
      Go implant and build unit were removed; .NET (M3.3) is the sole in-tree
      reference implant. Go and other languages remain available as out-of-tree
      community units through the build contract.)_
- [x] **M3.3 Reference .NET implant + build unit.** A C#/.NET implant for
      Windows; in-memory execution path. _AC:_ .NET implant checks in and tasks
      end-to-end.
- [x] **M3.4 Stager / web-shell / ephemeral / pivot classes.** The other implant
      classes on their transports and reduced verb sets.
      _AC:_ each class enrolls and runs its reduced verb set.

## Milestone 4 -- OPSEC infrastructure

- [x] **M4.1 Beacon profiles.** Per-implant sleep + jitter baked in at generation.
      _AC:_ generated artifacts carry the configured profile.
- [x] **M4.2 Kill dates and per-implant keys.** Self-termination timestamp and a
      unique key per implant, server-generated. _AC:_ an implant refuses to run
      past its kill date; keys differ per implant.
- [x] **M4.3 Malleable transport profiles.** Configurable URIs, headers, timing,
      payload shape per implant. _AC:_ a profile changes the wire shape.
- [x] **M4.4 Redirectors and burn handling.** Decoupled public endpoints; key/
      endpoint rotation; implant retirement; redirector severing.
      _AC:_ swap a redirector without backend change; retire an implant cleanly.

## Milestone 5 -- Offensive capability modules

- [x] **M5.1 Recon.** `recon.portscan`, `recon.hostenum`, `recon.service`.
      _AC:_ scan results captured as task output against authorized targets.
- [x] **M5.2 Lateral movement.** `lateral.move`, `lateral.token`,
      `lateral.exec_remote`, recorded with parent linkage.
      _AC:_ a child implant enrols from a parent within scope.
- [x] **M5.3 Persistence.** `persist.install/remove/list`.
      _AC:_ install, list, and remove within the engagement.
- [x] **M5.4 Collect and exfil.** `collect.file/cred/keylog`, `exfil.push/stage`.
      _AC:_ collected data exfils over the C2 channel and is stored scoped.

## Milestone 6 -- Evidence and reporting

- [x] **M6.1 Operational event log.** Per-engagement, append-only, attributed
      event stream. _AC:_ every action produces an attributed, immutable event.
- [x] **M6.2 Artifact management.** Artifacts linked to tasks as first-class
      objects. _AC:_ attach, list, and retrieve artifacts per task.
- [x] **M6.3 Timeline and report export.** Built-in consumers of the event +
      task + artifact store. _AC:_ export a reproducible engagement timeline and
      report.
- [x] **M6.4 Post-operation retention.** Audit trail survives infrastructure
      teardown. _AC:_ tear down an engagement's infra; its audit trail remains.

## Milestone 7 -- Evasion and exploit module frameworks

These milestones deliver the **contract and dispatch** for sensitive categories,
not tradecraft. Concrete avoidance techniques and PoC integration are supplied
as out-of-tree, opt-in `CapabilityModule`s.

- [x] **M7.1 Evasion contract.** `evasion.avoid`/`evasion.unload` interfaces,
      registration, dispatch points, and data shapes in the core.
      _AC:_ an out-of-tree module can register and be dispatched through the
      contract.
- [x] **M7.2 Exploit contract.** `exploit.invoke`/`exploit.module` as an external
      module integration point; load and dispatch flow.
      _AC:_ an out-of-tree module integrates and runs through the contract.

## Milestone 8 -- Wiring the tradecraft layer onto the task path

The tradecraft layer (M2.5 skeleton) and every offensive-capability contract
(M5.x, M7.x) are exercised only in tests; the live task path still gates verbs
on the static per-class table and ships the verb string straight to the implant.
This milestone connects them.

- [x] **M8.1 Tradecraft dispatch on the live task path.** Resolve task verbs
      through the capability registry and dispatcher instead of the static
      per-class table alone; give the no-class-gate categories (evasion,
      exploit) a dispatch path a registered module satisfies.
      _AC:_ a registered capability module is reached from the live task path,
      and an evasion/exploit verb is no longer refused before dispatch.

## Milestone 9 -- End-to-end child-implant enrollment

M5.2 delivered the server-side parentage model and enrollment path; the
implant-side handlers and the round-trip were deferred (architecture.md Sec
10.1). This milestone closes that follow-up.

- [x] **M9.1 Lateral child-implant round-trip.** Implant enroll clients carry
      parentage; a `lateral.move` handler derives a child that enrolls back; the
      binary wire surface gains parentage.
      _AC:_ a parent implant derives a child whose `ParentImplantId` is recorded
      server-side, verified by an implant-driven integration test.

## Milestone 10 -- Durable teamserver state

Core state is in-memory today; the audit trail is file-based only when
configured. Architecture Sec 12 names PostgreSQL as the authoritative store.

- [x] **M10.1 PostgreSQL core state.** Replace the in-memory adapters with a
      durable PostgreSQL store for core state (engagements, operators, implants,
      sessions, tasks, stager tokens); move audit/artifact stores off JSON Lines.
      Preceded by an ADR on data access.
      _AC:_ a teamserver restart leaves every engagement, operator, implant,
      session, task, and token in place; the audit chain still verifies.

## Milestone 11 -- Operator surface for the full capability set

The UI implements only M1.5 plus live presence; M5/M6/M7 features are not
reflected.

- [x] **M11.1 Operator UI coverage.** Surface every capability category as
      tasking; add audit-trail, artifact, timeline, and report views; let an
      operator task any implant; expose M4 OPSEC controls.
      _AC:_ recon through exploit are issuable from the UI, and the M6 evidence
      views (audit, artifacts, timeline, report) are reachable.

## Notes

- Each milestone leaves the system demoable or testable.
- Sensitive verbs always require engagement authorization and are always audited.
- Runtimes and toolchains track the latest LTS/stable; pin via `global.json` /
  `go.mod` / `rust-toolchain.toml` as applicable.
