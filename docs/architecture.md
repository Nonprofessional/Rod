# Rod -- Architecture & Design

> **Status:** Design (pre-implementation). This document is the agreed blueprint
> for Rod as an authorized-use red-team command-and-control (C2) platform. The
> repository currently holds only documentation and conventions; no code is
> implemented yet. Sections marked _(future)_ are deliberately out of the initial
> scope.

## 1. Overview

Rod is an **authorized-use offensive-security command-and-control platform** for
red-team operations, penetration tests, and security research. A team of
operators drives a fleet of short-lived, disposable implants on authorized
targets from a central teamserver, reaching hosts behind NAT and firewalls over
implant-initiated connections.

The design follows from a few load-bearing priorities, and the rest of this
document is their consequence. Implants are short-lived and untrusted by default,
each carrying a unique key -- never a global shared secret. OPSEC and evasion are
first-class design axes, not feature flags. Every action is attributed to an
operator and recorded in an immutable, hash-chained audit trail that doubles as
the report source. A lost implant fails safe: a baked-in kill date
self-terminates it. And the **Engagement** -- one authorized operation -- is the
unit of tenancy, isolation, authorization, and evidence.

## 2. Operational lifecycle (the organizing axis)

The architecture is organized around the red-team operational lifecycle, not
around "managed components". Each phase states what the platform must support.

1. **Planning and engagement setup.** Define the engagement: scope, ROE,
   operators, roles. The engagement is created as an isolation boundary.
2. **Infrastructure stand-up.** Provision teamserver, listeners, redirectors,
   domains, certificates. Infrastructure is **disposable and reprovisionable**;
   burn rate is expected, so it is config-driven and tear-down friendly.
3. **Payload generation and staging.** Build per-implant artifacts with baked-in
   C2 endpoint, per-implant key, beacon parameters, and kill date. Emit a stage-1
   stager where useful.
4. **Delivery and initial access.** Delivery (phishing, host interaction, etc.)
   is out of scope for Rod, but the platform must **ingest the first callback**
   and correlate it to the engagement.
5. **Beaconing / check-in.** The implant calls in; the teamserver authenticates
   it, queues tasks, and accepts results. Async beacon and interactive session
   are distinct modes.
6. **Post-exploitation tasking.** Operators issue tasks; the platform captures
   output and artifacts, attributes every action to an operator, and supports
   multiplayer.
7. **Lateral movement and persistence.** Spawn child implants, pivot, establish
   footholds -- treated as more generation plus more listeners, all recorded.
8. **Exfiltration.** Stage and transfer collected data over an audited path,
   every byte tied to a task and operator.
9. **Reporting and evidence.** The operation ends in a deliverable (timeline,
   findings, evidence). The audit trail is the **source for report generation**.
10. **Cleanup.** Retire implants, tear down infrastructure, **retain the
    immutable audit trail** -- it outlives the operation.

Every object (implant, task, artifact, infrastructure node) carries
`engagement_id` and `operator_id` from creation.

## 3. The Engagement model

Everything is organized around an **Engagement** -- the unit of tenancy,
isolation, authorization, and evidence. An engagement models one authorized
operation.

- A high-privilege **Operator** creates an **Engagement**.
- **Implants** enrol into exactly one engagement; an implant's identity is bound
  to its engagement and is disposable with it.
- The engagement owner adds other **Operators** with a **Role**
  (`Owner` / `Lead` / `Operator` / `Observer`); members can view and task
  implants in that engagement, scoped by role.
- All domain data -- implants, sessions, tasks, results, artifacts, modules,
  audit -- is **scoped by engagement**. Cross-engagement access is impossible by
  construction.
- Identities, keys, and endpoints are **ephemeral per engagement**; there is no
  permanent enrollment. Tearing down an engagement severs its implants; the audit
  trail remains.

> Multi-tenancy is per-engagement isolation. An optional Organization layer
> above engagements can be added later without changing this boundary.

## 4. Component architecture -- monolithic kernel, layered

Rod is a **monolithic teamserver with strong internal logical layering**, plus
external build units and implants. A single .NET process (the teamserver) holds
the core; polyglot needs are met by decoupling at the build boundary (Sec. 6),
not by splitting the whole system into microservices.

The rationale and the monolith-vs-microservices trade-off are in
[decisions/0001](decisions/0001-stack-and-architecture.md).

### 4.1 The six internal layers

1. **Core state.** The implant/session registry, the task queue and history, and
   engagement/operator state. Authoritative and in-memory-or-DB-backed.
2. **Transport layer.** Listeners terminate C2 transports; redirectors front
   them. The listener and the public endpoint are decoupled so a burned
   redirector is replaceable without backend change. (Sec. 8.)
3. **Payload build pipeline.** Drives **external build units** to compile
   polyglot implants on demand through a uniform build contract. (Sec. 6.)
4. **Operator layer.** Multiplayer operator sessions over the operator API;
   shared live engagement state; task ownership and attribution.
5. **Storage and audit.** Per-engagement, append-only, hash-chained audit and
   the artifact store. The evidence backbone. (Sec. 11.)
6. **Pluggable tradecraft.** Post-exploitation capability modules, including the
   evasion/exploit category contracts. (Sec. 10.)

Layers depend inward only: tradecraft and operator layers depend on core state
and audit; the build pipeline depends on core state; transport depends on core
state, the wire protocol, audit, and the build pipeline (it composes the audit
write and drives the build orchestrator); core state and audit depend on nothing
in-house. The dependency rule is enforced by architecture tests.

### 4.2 External components

- **Build units.** One per implant language (C#/.NET, Go, C/C++, Nim). Each
  owns its toolchain and compiles artifacts on demand. Coupled to the teamserver
  only by the build contract. The Go build unit (`Rod.BuildPipeline`'s
  `GoBuildUnit`) and the .NET build unit (`DotNetBuildUnit`) are live; the
  others arrive with their implants (M3.4+). (Sec. 6.)
- **Implants.** Target-resident, polyglot, disposable. Speak the wire protocol.
  Independent of the teamserver language. (Sec. 5.) The **reference Go implant**
  lives in the top-level `implant/` tree: a benign, readable stage-2 implant
  that enrolls over HTTP (submitting its own public key), beacons over mTLS,
  and runs the `shell.exec` core verb. Its wire bindings are generated from the
  teamserver proto (`implant/rodpb/`) and committed; the build unit bakes the
  per-implant profile in at compile time. It performs no evasion, no
  obfuscation, no persistence (RESPONSIBLE-USE.md, Sec. 7). The **reference
  .NET implant** lives in the parallel top-level `implant-dotnet/` tree: the
  same benign stage-2 shape in C#/.NET 10, compiling its wire bindings from the
  canonical `src/Rod.Protocol/protos/rod.proto` at build time (no committed
  generated code), built on demand by `DotNetBuildUnit` through the same build
  contract and the same baked-profile encoding as the Go unit.
- **Redirectors.** Near-stateless forwarders (Go, single static binary) for OPSEC
  and infra flexibility. No engagement state, no business logic. (Sec. 8.)
- **Operator UI.** The web front end; lives in the teamserver project.

### 4.3 Source-tree map (`src/`)

The teamserver is a single .NET solution (`Rod.slnx`) split into the projects
below. Six of them are the **internal layers** of §4.1; two are not layers and
sit alongside them -- `Rod.Protocol` (the language-neutral wire contract every
transport speaks) and `Rod.TeamServer` (the single runnable process and
composition root). Each project's role, the layer rule it lives under, and a
note on its current state are listed.

| Project | Role | Layer rule (what it may depend on) | State |
|---------|------|------------------------------------|-------|
| `Rod.CoreState` | The teamserver's authoritative domain core: typed ids, the `Engagement` aggregate, operators, implants, tasks, stager tokens, the implant session registry, the task queue and history, and the per-engagement implant certificate authority. The use cases (`EngagementService`, `EnrollmentService`, `HandshakeService`, `TaskService`, `ImplantService`) orchestrate these ports and define the operational behavior everything else consumes. The per-class reduced verb sets (`ImplantClassCapabilities`, Sec 5.2) live here as the inner-ring authority both the build pipeline and tradecraft read. | Inner ring -- depends on nothing in-house. | Implemented (M2.1 core-state layer; sessions lift the M1.x presence record, ports carry an in-memory adapter. M3.4: per-class reduced verb sets, enforced at task issuance -- an unsupported verb is refused before it is queued, and the implant's engagement binding is checked there too. M4.2: the kill date is enforced at handshake -- an implant past its kill date is refused before a session opens, mapping to `HANDSHAKE_STATUS_KILL_DATE_EXPIRED`. M4.4: an implant can be retired -- `Implant.Retire` is idempotent, a retired implant is refused at handshake (`ImplantRetired`) and untaskable (`TaskRejectionReason.ImplantRetired`), `ImplantService.RetireAsync` closes its active session and publishes an `ImplantRetired` live event. M5.1: the Stage-2 reduced verb set is widened to carry the three recon verbs alongside the core set, so recon is gated to Stage-2 at task issuance -- the other classes are unchanged). M5.2: the Stage-2 reduced verb set is widened again to carry the three lateral verbs alongside the core and recon sets, so lateral movement is gated to Stage-2 at task issuance; the implant entity gains an optional `ParentImplantId` and an `EnrollChild` factory, and `EnrollmentService` resolves and scope-checks a named parent -- it must exist, share the redeemed token's engagement, and not be retired -- recording the linkage on the child). M5.3: the Stage-2 reduced verb set is widened again to carry the three persist verbs alongside the core, recon, and lateral sets, so persistence is gated to Stage-2 at task issuance -- the other classes are unchanged). M5.4: the Stage-2 reduced verb set is widened again to carry the three collect verbs and the two exfil verbs alongside the core, recon, lateral, and persist sets, so collection and exfiltration are gated to Stage-2 at task issuance -- the other classes are unchanged). |
| `Rod.Audit` | The append-only, per-engagement audit trail: hash-chained `AuditEvent` records and the `IAuditStore` port, plus the `IArtifactStore` for first-class evidence objects attached to tasks. The evidence backbone (Sec. 11); the source for timeline and report export. | Inner ring -- depends on nothing in-house (crosses the layer boundary with primitive `Guid` ids, never core-state types). | Implemented (in-memory; M2.3: per-engagement hash chain -- tampering breaks the chain -- and the artifact store. M4.4: `ImplantRetired` audit kind, composed by transport on `:retire` so a retirement is recorded in the engagement trail). |
| `Rod.Protocol` | **Not a layer.** The gRPC/protobuf wire protocol: frames, the enrollment/handshake/tasking messages, and the `Beacon` check-in stream (Sec. 8). The long-lived, language-neutral contract implants of every language build against. | Not a layer -- depends on nothing in-house; never leaks into `Rod.CoreState`. | Implemented (frame + M1.x messages; M4.2: `HANDSHAKE_STATUS_KILL_DATE_EXPIRED` for an implant refused past its kill date. M4.4: `HANDSHAKE_STATUS_IMPLANT_RETIRED` for an implant refused because it has been retired). |
| `Rod.Transport` | Listeners that terminate C2 transports and map core-state use cases onto the operator HTTP API and the implant beacon stream. Owns endpoint routing, mTLS termination, and the mapping of use-case failures to wire status codes. | Layer 2 -- may depend on `Rod.CoreState`, `Rod.Protocol`, `Rod.Audit`, `Rod.BuildPipeline`. | Implemented (M1.x endpoints + M2.2 listener abstraction: HTTP(S) and mTLS listeners, bind address decoupled from the public endpoint; M3.1 payload-build endpoint that drives the build orchestrator and composes the PayloadBuilt audit write. M4.4: `POST /engagements/{engagementId}/implants/{implantId}:retire` retires an implant and composes the `ImplantRetired` audit write, and `POST /listeners/{id}:repoint` repoints a listener's public endpoint at runtime -- the bind is untouched, swapping a burned redirector without backend change). |
| `Rod.BuildPipeline` | Drives the external, per-language build units to compile polyglot implants on demand through the uniform build contract, fingerprinting and recording each artifact (Sec. 6). | Layer 3 -- may depend on `Rod.CoreState`. | Implemented (M3.1: the build-contract schema, the build-unit registry and dispatch, and the PayloadBuilt audit write composed by transport. M3.2: the real Go build unit -- `GoBuildUnit` compiles the reference Go implant per request, baking the per-implant profile via ldflags without leaking the implant key -- replaces the stub in the live registry; the stub stays as the contract-reference unit with its own unit tests. M3.3: the real .NET build unit -- `DotNetBuildUnit` compiles the reference .NET implant per request via `dotnet publish`, baking the per-implant profile into a generated `BakedProfile.cs` in a per-build staging copy with the same encoding and no key leak as the Go unit -- registers alongside the Go unit in the live registry. M3.4: each unit bakes the class's reduced verb set (read from core state, Sec 5.2) into the profile, so an artifact is self-describing and two classes of the same language produce visibly different output; the key is still absent. M4.3: the malleable transport profile (Sec 7, Sec 8) -- enroll path, User-Agent, headers, request timeout, body envelope -- rides in the baked profile, emitted byte-for-byte identically by the Go and .NET units, and the build request DTO carries it so an operator can profile a payload). |
| `Rod.Operators` | Multiplayer operator sessions over the operator API: shared live engagement state, task ownership and attribution, and real-time push to the operator UI. | Layer 4 -- may depend on `Rod.CoreState`, `Rod.Audit`. | Implemented (M2.4: Server-Sent Events stream per engagement, a channel-backed live-event bus fanning task-issued / task-completed / presence events to every connected session, an operator-presence roster, and query-param session identity; real operator auth arrives later). |
| `Rod.Tradecraft` | Pluggable post-exploitation capability modules, including the evasion/exploit category contracts (Sec. 10, Sec. 13). Concrete tradecraft is out-of-tree; this layer holds the contract and dispatch only. | Layer 6 -- may depend on `Rod.CoreState`, `Rod.Audit`. | Implemented skeleton (M2.5: `ICapabilityModule` contract, capability registry + dispatcher, the five core verbs loaded through it; the dispatchable `shell.exec` stub proves the round-trip. Not yet wired onto the live task path -- that arrives with the offensive-capability milestones. M5.1: the recon descriptors -- `recon.portscan`, `recon.hostenum`, `recon.service` -- are loaded through this layer as placeholders alongside the core set, each flagged network-touching except the host-local `recon.hostenum`; the default registry lists both sets. M5.2: the lateral descriptors -- `lateral.move`, `lateral.token`, `lateral.exec_remote` -- are loaded through this layer as placeholders alongside the core and recon sets, each flagged with its OPSEC attribute (`derives-child` / `touches-credential` / `touches-network`); the default registry lists all three sets. M5.3: the persist descriptors -- `persist.install`, `persist.remove`, `persist.list` -- are loaded through this layer as placeholders alongside the core, recon, and lateral sets, install and remove flagged `writes-to-disk` (install also `persists`) and list unflagged as a read; the default registry lists all four sets. M5.4: the collect descriptors -- `collect.file`, `collect.cred`, `collect.keylog` -- and the exfil descriptors -- `exfil.push`, `exfil.stage` -- are loaded through this layer as placeholders alongside the core, recon, lateral, and persist sets, collect.file flagged `reads-filesystem`, collect.cred flagged `reads-credential`, collect.keylog flagged `reads-input` and `persists`, exfil.push flagged `touches-network`, and exfil.stage unflagged as a read; the default registry lists all six sets). |
| `Rod.TeamServer` | **Not a layer.** The single runnable .NET process and composition root: it wires `Rod.Transport`'s services and endpoints, terminates mTLS, and serves the built React operator UI same-origin with an SPA fallback. It is where the layers are assembled for `dotnet run`; the layer dependency tests do not constrain it. | Not a layer -- the composition root; depends inward on `Rod.Transport` and `Rod.Operators` (the latter wired in M2.4, since transport itself cannot reference the operator layer). | Implemented (M1.5 host + UI shell; M2.4 wires the operator layer). |

The dependency column is not aspirational: it is the rule the architecture tests
in `tests/Rod.Architecture.Tests/LayerDependencyTests.cs` enforce. Adding a
forbidden project reference fails the build.

## 5. Implants and profiles

An implant is a short-lived, disposable payload on a target. It is **untrusted by
default** and carries a unique per-implant key -- never a global shared secret.

### 5.1 Profiles are baked in at generation

A **profile** -- beacon parameters (sleep, jitter, kill date), the transport
profile, the per-implant key, and the C2 endpoint -- is embedded into the
artifact at build time, so each implant is self-contained and standalone. This
is what makes per-implant OPSEC possible: no two implants look the same, and a
lost implant self-terminates at its kill date.

The bake-in is verified end-to-end: the configured sleep, jitter, and kill date
land in the decoded artifact across the Go, .NET, and stub build units, so a
profile that is silently dropped or defaulted fails the build-pipeline tests.

The kill date is enforced on both sides of the wire (Sec 7). The teamserver
refuses to open a session for an implant whose kill date has passed, returning
`HANDSHAKE_STATUS_KILL_DATE_EXPIRED` at handshake before any session or tasking
is recorded; the implant itself refuses to start past its kill date and
re-checks it at the top of each beacon cycle, so a long-running implant
self-terminates the moment the date passes rather than waiting for a reconnect
or restart. The per-implant key is generated server-side (a 32-byte
cryptographically random value) at both enrollment and build time, so two
implants -- or two builds of the same request -- never share a key; this is
pinned by tests against both producers.

### 5.2 Implant classes (by operational purpose)

Implants differ by purpose, not by a "managed device flavor":

- **Stage-2 implant** -- the primary long-haul implant; full capability set and
  module support. (e.g. .NET on Windows, Go cross-platform.)
- **Stager** -- a tiny stage-1 loader that fetches a stage-2 implant. Separate
  generation output class.
- **Web-shell class** -- a script placed in a web root, bound to the web
  transport; code execution over HTTP, no interactive PTY.
- **Ephemeral** -- a short-lived, TTL'd implant from a one-liner bootstrap; for
  one-off execution and temporary access.
- **Pivot** -- an implant that represents hosts which cannot run their own
  implant (network/OT gear), enrolling each as its own session and forwarding
  tasking.

Each class carries a **reduced verb set** -- the subset of the verbs its
purpose justifies, defined in `Rod.CoreState.ImplantClassCapabilities` (the
inner ring both the build pipeline and the tradecraft layer read). Stage-2
carries the full core set plus the recon set, the lateral set, the persist set,
the collect set, and the exfil set (recon, lateral movement, persistence,
collection, and exfiltration are long-haul activities that justify a stage-2
footprint); a stager only `file.pull`s the stage-2 it loads; a web-shell and an
ephemeral run `shell.exec` and `probe.read` over their short-lived channels; a
pivot carries `tunnel.open` and `probe.read` and no shell. No class but Stage-2
carries a recon, lateral, persist, collect, or exfil verb. The set
is the server's authority for what a class may do: task issuance gates on it in
core state (a verb outside the set is refused before it is queued, Sec 10.3),
and the build pipeline bakes it into each artifact so a generated payload is
self-describing.

A capable implant can deploy another class on the same host (e.g. a web-shell
deriving a stage-2 implant) via a deployment verb; the child enrols into the same
engagement and records its parent. This is the lateral-movement path (roadmap
M5.2): the `lateral.move` verb is the deployment verb that semantically means
"derive a child," and the child's enrollment records its `ParentImplantId` on
the implant entity. The child enrols through the same enrollment route a
top-level implant takes, naming its parent; the enrollment service resolves and
scope-checks the parent (it must exist, belong to the same engagement the
redeemed token resolved, and not be retired) before binding the child. The
parentage is surfaced on the operator implant listing so the UI can render
lineage; a top-level (stager-derived) implant reports no parent.

## 6. Payload build pipeline (polyglot via decoupled build units)

The flow: **operator build request -> teamserver emits build params -> the
language's build unit compiles -> artifact + stager returned -> fingerprinted and
recorded.**

- **One build unit per language.** C#/.NET, Go, C/C++, and Nim each get an
  independent build unit owning its own toolchain. The teamserver drives them
  through a **uniform build contract** and is coupled to them only by that
  contract -- a .NET teamserver can produce a Go or C implant with no in-language
  coupling.
- **Build params** include implant config, the embedded per-implant key, target
  OS/arch, transport profile, and beacon parameters. They are produced at request
  time so each artifact is unique (per-implant keys, config, obfuscation) -- this
  is essential for OPSEC.
- **Staging** is a separate output: a stage-1 stager that fetches stage-2 has its
  own generation path.
- **Artifact tracking.** Every generated artifact is fingerprinted and recorded
  (who, when, config) into the audit trail.

The build contract is the language-neutrality boundary; it is what lets the wire
protocol be "the product" while implants stay polyglot.

## 7. OPSEC -- a first-class design axis

OPSEC is a design axis, not a feature flag. The architecture bakes in:

- **Per-implant beacon profile.** Configurable sleep with **jitter** (randomized
  delta) to avoid periodic-check-in detection.
- **Kill date.** A hard self-termination timestamp baked in per implant to limit
  exposure if lost. Enforced on both sides: the teamserver refuses a handshake
  past it (`HANDSHAKE_STATUS_KILL_DATE_EXPIRED`, no session opens), and the
  implant refuses to start past it and re-checks it each beacon cycle so a
  long-running implant self-terminates the moment the date passes.
- **Per-implant cryptographic key.** Unique per implant, so compromising one does
  not compromise all. Keys are generated server-side (32-byte
  cryptographically random) at enrollment and at build time, so two implants
  never share a key. Because the key is baked into the artifact (Sec 5.1), key
  rotation is the operational flow *retire the compromised implant, repoint its
  endpoint, and build a fresh artifact with a fresh server-generated key* (Sec
  8) -- there is no live in-place key swap.
- **Malleable transport profiles.** Configurable URIs, a User-Agent, custom HTTP
  headers, a per-request timeout, and a body envelope baked in per implant so the
  enroll wire shape matches legitimate traffic and two implants do not look the
  same (Sec 8). The profile is part of the baked-in profile (Sec 5.1) and is
  applied by the reference implant's enroll client: it enrolls against the
  profile's URI path, presents the profile's User-Agent and headers, honors the
  timeout, and wraps the JSON body as a single base64 string when the envelope is
  set to base64.
- **Disposable infrastructure.** Keys, identities, and endpoints are ephemeral
  per engagement; burned redirectors are swappable at runtime (Sec 8).
- **Redirector decoupling.** Filter by User-Agent / URI / IP / OS; forward only
  real beacon traffic, send the rest to a decoy.
- **Per-command OPSEC metadata.** Commands carry OPSEC flags (e.g. "writes to
  disk") so operators and tradecraft filters can avoid risky actions.
- **Burn handling.** Retire an implant (it is refused at handshake and untaskable
  thereafter, its active session closed, the retire recorded in the audit trail);
  repoint a listener's public endpoint to swap a burned redirector without
  touching the backend, which severs the old endpoint.

> This section defines what the platform must **provide** for OPSEC. It does not
> describe concrete evasion techniques. Those are out-of-tree capability modules
> (Sec. 10, Sec. 13).

## 8. Transports, listeners, and redirectors

- Supported listener transports: **HTTP(S)**, **mTLS**, **DNS**, **SMB**, **TCP**.
  Transport choice is a profile/deployment concern; the protocol semantics are
  transport-independent.
- An implant is always the **connection initiator** (reverse connection). The
  teamserver and redirectors never dial targets.
- **Listener and public endpoint are decoupled, and the endpoint is repointable
  at runtime.** A redirector fronts the listener; a burned redirector is replaced
  without touching the backend by repointing the listener's public endpoint
  (`POST /listeners/{id}:repoint`). The Kestrel bind is untouched; the old
  endpoint simply no longer resolves to any listener, which severs it. This
  decoupling is what makes disposable infrastructure practical.
- **Message sizing and flow control.** A single frame stays well under 1 MiB and
  never exceeds the negotiated maximum. Bulk data (files, output) is chunked.
- **Malleable transport profile (per implant).** Each implant carries a transport
  profile baked in at generation (Sec 5.1, Sec 7): an enroll URI path, a
  User-Agent, custom HTTP headers, a per-request timeout, and a body envelope. It
  is applied client-side at enroll -- the reference Go and .NET implants enroll
  against the profile's path and present its headers, timeout, and body shape --
  so a profile changes the wire shape. The teamserver's enroll route stays fixed
  at `/implants/enroll`; URI and header routing at the public endpoint is a
  redirector concern (Sec 7). Verified by a build-pipeline round-trip test and an
  httptest-backed wire-shape test that captures the enroll request.
- Redirectors terminate transport only as needed and forward opaque payloads;
  they can route frames whose inner payload they cannot deserialize.

## 9. Security model

Rod is remote-code-execution infrastructure: a compromised teamserver is
fleet-wide code execution. Security is a first-class concern.

- **Identity.** Operator identities (credentials, MFA, API tokens); implant
  identities bound to their engagement via client certificates.
- **mTLS.** The mTLS transport is mutually authenticated; an implant's certificate
  binds `(implant_id, engagement_id)`.
- **Command signing.** Dispatched tasks are signed so an implant only acts on
  teamserver-authorized tasking.
- **Sealing** _(future)_. End-to-end protection of task payloads so untrusted
  redirectors cannot read or alter them. Designed for, not implemented initially.
- **Per-implant keys and rotation.** Unique keys per implant, generated
  server-side at enrollment and build time (Sec. 7). The key is baked into the
  artifact, so rotation is the operational flow *retire the compromised implant,
  repoint its endpoint, and build a fresh artifact with a fresh server-generated
  key* (Sec 7, Sec 8); there is no live in-place key swap.
- **Retirement.** An implant can be retired from the operator API
  (`POST /engagements/{engagementId}/implants/{implantId}:retire`); a retired
  implant is refused at handshake (`HANDSHAKE_STATUS_IMPLANT_RETIRED`, no session
  opens), is untaskable (`422`), and its active session is closed. Retirement is
  idempotent and recorded as an `ImplantRetired` audit event in the engagement
  trail; any queued tasks for it are left inert (no dispatch, no cancellation).
  Cert revocation stays a future concern (application-layer, like the kill date).
- **Kill-date enforcement.** The teamserver refuses to open a session for an
  implant past its baked-in kill date (the handshake returns
  `HANDSHAKE_STATUS_KILL_DATE_EXPIRED`), and the implant self-terminates past it
  on startup and each beacon cycle -- a lost implant cannot stay live past its
  date even if it ignores its own check.
- **Audit trail.** Every privileged action produces an immutable, hash-chained
  `AuditEvent`. Tampering breaks the chain (Sec. 11).
- **Engagement isolation.** Enforced at the teamserver and by engagement binding
  in certificates; redirectors never enforce tenancy.
- **ROE guardrails.** The audit store feeds guardrails that warn or block
  high-risk actions against out-of-scope targets.
- **No self-protection.** Rod ships no protection against its own detection by
  defenders. Stealth is a deployment and capability concern (Sec. 7), not a
  security boundary of the platform.

## 10. Capability model and tasking

A **capability** is a verb an implant advertises and the teamserver may dispatch,
namespaced `namespace.action`, each carrying a `version` and `attributes`. The
teamserver gates dispatch on the advertised verb.

### 10.1 Capability categories

| Category | Example verbs | Summary |
|----------|---------------|---------|
| **core** | `shell.exec`, `file.push`, `file.pull`, `tunnel.open`, `probe.read` | The mandatory-to-useful baseline. |
| **recon** | `recon.portscan`, `recon.hostenum`, `recon.service` | Target and network reconnaissance. |
| **lateral** | `lateral.move`, `lateral.token`, `lateral.exec_remote` | Lateral movement within authorized scope. |
| **persist** | `persist.install`, `persist.remove`, `persist.list` | Persistence mechanisms. |
| **collect** | `collect.file`, `collect.cred`, `collect.keylog` | Data and credential collection. |
| **exfil** | `exfil.push`, `exfil.stage` | Exfiltration over the C2 channel. |
| **evasion** | `evasion.avoid`, `evasion.unload *(contract only)* | Detection-evasion hooks. Contract and dispatch only. |
| **exploit** | `exploit.invoke`, `exploit.module *(contract only)* | PoC/exploit integration point. Contract and dispatch only. |

The recon verbs are registered through the tradecraft layer as first-class
descriptors (`Rod.Tradecraft.Recon.ReconCapabilities`, category `Recon`); their
concrete behavior runs on the reference implants and is captured as task output
over the beacon stream (Sec 10.3). Recon is a long-haul activity, so the three
verbs are gated to Stage-2 at task issuance -- a non-Stage-2 class is refused
before the task is queued (Sec 5.2).

The lateral verbs are registered the same way
(`Rod.Tradecraft.Lateral.LateralCapabilities`, category `Lateral`):
`lateral.move` carries a `derives-child` attribute and is the deployment verb
that means "derive a child implant"; `lateral.token` and `lateral.exec_remote`
carry `touches-credential` and `touches-network` attributes respectively. Like
recon they are gated to Stage-2 at task issuance (Sec 5.2). Their concrete
behavior is out-of-tree tradecraft (Sec 13); what the core provides in M5.2 is
the parentage data model and the child-enrollment path -- the server records a
child's `ParentImplantId` and validates it against the redeemed token's
engagement, so a child derives only from a live parent in the same engagement.
The implant-side handlers and the end-to-end child-enroll round-trip land in a
follow-up milestone.

The persistence verbs are registered the same way
(`Rod.Tradecraft.Persist.PersistCapabilities`, category `Persist`):
`persist.install` and `persist.remove` carry `writes-to-disk` attributes
(install additionally carries `persists`), and `persist.list` is a read that
carries no such flag, like the host-local `recon.hostenum`. Like recon and
lateral they are gated to Stage-2 at task issuance (Sec 5.2). Their concrete
behavior is out-of-tree tradecraft (Sec 13, AGENTS.md Sec 7): persistence is a
long-haul activity, but the reference implants ship none -- they perform no
persistence (Sec 5) -- so install, list, and remove run only when an operator
supplies an out-of-tree module for the verb.

The collection and exfiltration verbs are registered the same way
(`Rod.Tradecraft.Collect.CollectCapabilities`, category `Collect`, and
`Rod.Tradecraft.Exfil.ExfilCapabilities`, category `Exfil`): `collect.file`
carries a `reads-filesystem` attribute, `collect.cred` carries a
`reads-credential` attribute, and `collect.keylog` carries `reads-input` and
`persists` (it installs a resident input-capture hook); `exfil.push` carries a
`touches-network` attribute (it transfers over the C2 channel), and `exfil.stage`
is a read that carries no such flag, like `persist.list` and the host-local
`recon.hostenum` (it stages already-collected data on the teamserver). Like
recon, lateral, and persist they are gated to Stage-2 at task issuance
(Sec 5.2). Their concrete behavior is out-of-tree tradecraft (Sec 13, AGENTS.md
Sec 7): collection and exfiltration are long-haul activities, but the reference
implants ship none -- they perform no collection or exfiltration (Sec 5) -- so
each verb runs only when an operator supplies an out-of-tree module for it.

### 10.2 Sensitive-capability boundary

`evasion` and `exploit` are first-class in the capability model -- they have
defined interfaces, registration, dispatch, and data shapes. Their **concrete
behavior is intentionally not part of the core**: the core provides the contract
and the plumbing; the tradecraft is supplied as separate, opt-in, out-of-tree
`CapabilityModule`s. See Sec. 13.

### 10.3 Tasking lifecycle

`Task` -> dispatched to a `Session` -> `TaskExecution` (streams, result, status)
-> recorded in the engagement audit trail. Sensitive verbs additionally require
engagement authorization and are always audited.

## 11. Evidence and reporting -- a first-class output

A red-team operation ends in a deliverable: timeline, findings, and evidence. Rod
treats the audit trail as the **source for report generation**, not a post-hoc
scrape.

- **Every action is an immutable, attributed event**: `operator_id`,
  `engagement_id`, `implant_id`, `task_id`, `command`, `timestamp`, input
  parameters, output/result, and linked artifacts. This is the engagement
  timeline by construction.
- **The event log is append-only and per-engagement**; it is never deletable
  mid-operation (chain-of-custody).
- **Artifacts** (files, screenshots, command output) are first-class objects
  linked to tasks, not loose files.
- **Timeline and report export** are built-in consumers of the event + task +
  artifact store -- the audit trail renders directly into the deliverable.
- **The audit trail outlives the operation.** It is retained after infrastructure
  teardown; ROE guardrails read from the same store.

## 12. Technology stack and language boundaries

| Concern | Choice | Why |
|---------|--------|-----|
| Teamserver (monolithic kernel) | .NET 10 (LTS), ASP.NET Core, gRPC | Strong async networking, first-class gRPC, strong typing, mature web UI. LTS to ~2028. |
| Data store | PostgreSQL | Authoritative teamserver state; per-engagement audit. |
| Build units | C#/.NET, Go, C/C++, Nim toolchains, one per language | Polyglot implants with no teamserver-language coupling. |
| Redirectors | Go (latest stable), static single binary | Tiny VPS footprint; stdlib mTLS/HTTP/DNS. |
| Implants | C#/.NET, Go, C/C++, Nim -- per target | .NET for Windows in-memory tradecraft; Go for cross-platform; C/C++/Nim for footprint. |
| Operator UI | Web (React), served by the teamserver | Lives in the teamserver project; see ADR 0002. |

The wire protocol and capability registry are the long-lived, language-neutral
contract implants build against; the build contract is the language-neutrality
boundary for generation.

## 13. Sensitive-capability statement

Evasion, exploit, and related offensive behavior are part of Rod's capability
model as **pluggable contracts**. The core repository defines their interfaces,
registration, dispatch, and data models, and provides no concrete bypass
techniques, weaponized code, or in-the-wild proof-of-concepts. Operators supply
tradecraft as separate, opt-in modules. All use assumes an authorized context;
see [RESPONSIBLE-USE.md](../RESPONSIBLE-USE.md).

## 14. Capability bar (design aspiration)

Rod is designed to meet or exceed the state of the art across the capability
dimensions a modern offensive platform is expected to cover. This is a standing
design constraint, not marketing: every capability area below must be planned and
built so that its reach, flexibility, and OPSEC qualities are at least on par
with -- and aim to surpass -- what established platforms offer in that area.

- **Core operations** -- shell execution (interactive and one-shot), file
  transfer, tunneling, host enumeration: as capable and as OPSEC-tunable as the
  best available, with per-implant profiles baked in at generation.
- **Reconnaissance** -- port and service discovery, host and network
  enumeration: comprehensive, fast, and audited.
- **Lateral movement** -- token and credential reuse, remote execution, child
  implant derivation and pivoting: full coverage with parentage tracking.
- **Persistence** -- a broad, cross-platform set of mechanisms, installable and
  removable, all recorded.
- **Collection and exfiltration** -- file, credential, and input collection,
  staged and transferred over the C2 channel, every byte attributed.
- **Evasion** -- the platform must provide the contracts, hooks, and per-implant
  tuning (profiles, jitter, kill dates, malleable transports, per-command OPSEC
  metadata) that let operators keep a low profile; concrete tradecraft is
  out-of-tree, but the substrate must be best-in-class.
- **Exploitation integration** -- a clean, extensible integration point for
  external exploit and payload modules, so new tradecraft plugs in without core
  changes.
- **OPSEC and infrastructure** -- disposable, reprovisionable infrastructure,
  redirector decoupling, burn handling, and per-implant keying as first-class.
- **Evidence and reporting** -- an immutable, attributed, hash-chained audit
  trail that is the source for timeline and report generation, surviving
  infrastructure teardown.

When a capability area falls short of this bar, the right response is to raise
the design, not to lower the bar. Concrete evasion techniques and exploit code
remain out-of-tree modules (Sec. 13); the bar above concerns the platform's
capability substrate, not bundled tradecraft.
