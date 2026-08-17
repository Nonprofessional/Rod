# Rod -- Architecture & Design

> **Status:** Living document. This is the agreed architecture for Rod as an
> authorized-use red-team command-and-control (C2) platform. The repository
> holds the teamserver and a .NET reference implant, with a Postgres persistence
> layer; [todo.md](todo.md) tracks open work. Sections marked _(future)_ are
> designed for but not yet implemented.

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
   operators. The engagement is created as an isolation boundary.
2. **Infrastructure stand-up.** Provision teamserver, listeners, redirectors,
   domains, certificates. Infrastructure is **disposable and reprovisionable**;
   burn rate is expected, so it is config-driven and tear-down friendly.
3. **Payload generation and staging.** Build per-implant artifacts with baked-in
   C2 endpoint, beacon parameters, and kill date. Emit a stage-1 stager where
   useful.
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

- An authenticated **Operator** creates an **Engagement**, recorded as its owner.
- **Implants** enrol into exactly one engagement; an implant's identity is bound
  to its engagement and is disposable with it.
- Any authenticated **Operator** can view and task implants in the engagement.
  There are no role tiers: like mainstream C2s, Rod trusts its named operators
  and holds them accountable through the attributed audit trail.
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

A monolithic kernel gives a security-critical core one blast radius and one
state model, with low inter-component latency and the simplest deployment for a
small team. The parts that change most (implant builds, transports, tradecraft)
are already decoupled as build units, redirectors, and capability modules, so
hot-swapping is not lost; the alternative -- a container-per-concern split -- is
heavier to operate and secure for no current gain, so the strong *logical*
layering (enforced by architecture tests, below) is kept and a future move
toward services stays open. Polyglot implants are met by a uniform build
contract with one build unit per language (Sec. 6), so C#/.NET, Go, C/C++, and
Nim payloads compile from one language-agnostic control plane; a single implant
language was rejected because it forces one language onto every target class
(Windows in-memory tradecraft, cross-platform reach, and small footprint each
demand a different one). The stack itself is in Sec. 12.

### 4.1 The six internal layers

1. **Core state.** The implant/session registry, the task queue and history, and
   engagement/operator state. Authoritative and in-memory-or-DB-backed.
2. **Transport layer.** Listeners terminate C2 transports; redirectors front
   them. The listener and the public endpoint are decoupled so a burned
   redirector is replaceable without backend change. (Sec. 8.)
3. **Payload build pipeline.** Drives **external build units** to compile
   polyglot implants on demand through a uniform build contract. (Sec. 6.)
4. **Operator layer.** Authenticated, multiplayer operator sessions over the
   operator API; shared live engagement state; task ownership and attribution.
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

- **Build units.** The in-tree build unit is .NET (`Rod.BuildPipeline`'s
  `DotNetBuildUnit`). It compiles the reference implant on demand and owns its
  toolchain, coupled to the teamserver only by the build contract. Other
  languages (Go, C/C++, Nim) stay available through that same contract and the
  `Language` enum, supplied as out-of-tree community units -- the project
  maintains one in-tree reference, not one per language (Sec 12.2).
- **Implants.** Target-resident, disposable, speaking the wire protocol and
  independent of the teamserver language. (Sec. 5.) The **reference .NET
  implant** lives in the `src/implant/dotnet/` tree: a benign, readable
  stage-2 implant that enrolls over HTTP (submitting its own public key),
  beacons over mTLS, and runs the standard-category verb set (Sec 10.1). It
  compiles its wire bindings
  from the canonical `src/teamserver/Rod.Protocol/protos/rod.proto` at build time (no
  committed generated code), and `DotNetBuildUnit` bakes the per-implant
  profile in at compile time. It performs no evasion and no obfuscation
  (RESPONSIBLE-USE.md, Sec. 7); the in-repo tradecraft it carries is bounded by
  Sec 13. The wire protocol is the language-neutral product, so a community
  implant in Go, C, or Nim builds against the same contract without coupling
  the teamserver to its language (Sec 12.2).
- **Redirectors.** Near-stateless forwarders (.NET, Native AOT, single static
  binary) for OPSEC
  and infra flexibility. No engagement state, no business logic. (Sec. 8.)
  The in-tree reference forwarder ships (`src/redirector/dotnet/`): an opaque
  L4 TCP splice published as a single static binary. Together with the
  server-side rotation path -- listener repoint (`POST /listeners/{id}:repoint`)
  and retire with their audit writes -- a burned redirector is swapped
  end to end; see [operations/redirectors.md](operations/redirectors.md).
- **Operator UI.** The web front end; lives in the teamserver project.

### 4.3 Source-tree map (`src/teamserver/`)

The teamserver is a single .NET solution (`Rod.slnx`) split into the projects
below. Six of them are the **internal layers** of §4.1; three are not layers and
sit alongside them -- `Rod.Protocol` (the language-neutral wire contract every
transport speaks), `Rod.Persistence` (the durable PostgreSQL adapters behind
the core-state and audit ports), and `Rod.TeamServer` (the single runnable
process and composition root). Each project's role, the layer rule it lives
under, and a note on its current state are listed.

| Project | Role | Layer rule (what it may depend on) | State |
|---------|------|------------------------------------|-------|
| `Rod.CoreState` | The teamserver's authoritative domain core: typed ids, the `Engagement` aggregate, operators, implants, tasks, stager tokens, the implant session registry, the task queue and history, and the per-engagement implant certificate authority. The use cases (`EngagementService`, `EnrollmentService`, `HandshakeService`, `TaskService`, `ImplantService`) orchestrate these ports and define the operational behavior everything else consumes. The per-class reduced verb sets (`ImplantClassCapabilities`, Sec 5.2) live here as the inner-ring authority both the build pipeline and tradecraft read. | Inner ring -- depends on nothing in-house. | Implemented. In-memory adapters behind every port; the durable pair lives in `Rod.Persistence`. Task issuance gates each verb on the implant's class reduced set, enforces the kill date and retirement at handshake, and claims tasks atomically from the queue (Sec 5.2, Sec 10.3). |
| `Rod.Audit` | The append-only, per-engagement audit trail: hash-chained `AuditEvent` records and the `IAuditStore` port, plus the `IArtifactStore` for first-class evidence objects attached to tasks. The evidence backbone (Sec. 11); the source for timeline and report export. | Inner ring -- depends on nothing in-house (crosses the layer boundary with primitive `Guid` ids, never core-state types). | Implemented. In-memory and file-backed (`Audit:DataDirectory`) adapters for the trail and the artifact store; the file store verifies each engagement's chain on recovery and refuses a tampered trail. Also hosts the payload store for built artifacts (Sec 6). |
| `Rod.Protocol` | **Not a layer.** The gRPC/protobuf wire protocol: frames, the enrollment/handshake/tasking messages, and the `Beacon` check-in stream (Sec. 8). The long-lived, language-neutral contract implants of every language build against. | Not a layer -- depends on nothing in-house; never leaks into `Rod.CoreState`. | Implemented. Versioned handshake (major.minor), a status code for every enrollment/handshake refusal, and the chunked exfil frame kind (Sec 8, Sec 10.1). |
| `Rod.Transport` | Listeners that terminate C2 transports and map core-state use cases onto the operator HTTP API and the implant beacon stream. Owns endpoint routing, mTLS termination, and the mapping of use-case failures to wire status codes. | Layer 2 -- may depend on `Rod.CoreState`, `Rod.Protocol`, `Rod.Audit`, `Rod.BuildPipeline`. | Implemented. HTTP(S) and mTLS listeners with the bind decoupled from the public endpoint (a repoint swaps a burned redirector without touching the socket); the full operator API (engagements, stager tokens, implants and retirement, tasks, artifacts, audit, timeline/report, payloads) and the beacon stream with bounded frames, capped exfil reassembly, and atomic task dispatch (Sec 8, Sec 10.3, Sec 11). The task, audit, and artifact listings are paged (limit + opaque cursor, newest window first) so a long engagement never grows a listing response without bound; the operator UI walks pages. |
| `Rod.BuildPipeline` | Drives the external, per-language build units to compile polyglot implants on demand through the uniform build contract, fingerprinting and recording each artifact (Sec. 6). | Layer 3 -- may depend on `Rod.CoreState`. | Implemented. `DotNetBuildUnit` -- the sole in-tree unit -- publishes the reference implant in a per-build staging copy as a self-contained single-file executable for the requested OS/arch (runtime identifier mapped from the build target; no target-side .NET install), baking the profile (transport shape, beacon parameters, class verb set) without any key material; the built bytes land in the payload store for operator download (Sec 6). |
| `Rod.Operators` | Multiplayer operator sessions over the operator API: shared live engagement state, task ownership and attribution, and real-time push to the operator UI. | Layer 4 -- may depend on `Rod.CoreState`, `Rod.Audit`. | Implemented. Cookie-authenticated operator sessions (login/logout/me; config-seeded first operator; hash-only credential port) and the per-engagement SSE live-event bus. Cookies were chosen over JWT (no client-side token store for a same-origin SPA); ASP.NET Core Identity was rejected (its own user/role tables conflict with the layered stores). Per-engagement RBAC is deliberately absent -- the trusted-operators model (Sec 4.1, Sec 9): every authenticated operator reaches every endpoint, and a per-handle login throttle slows brute force. |
| `Rod.Tradecraft` | Pluggable post-exploitation capability modules, including the evasion/exploit category contracts (Sec. 10, Sec. 13). Concrete tradecraft is out-of-tree; this layer holds the contract, the registration path, and the gate only. | Layer 6 -- may depend on `Rod.CoreState`, `Rod.Audit`. | Implemented. The capability contract (`ICapabilityModule`, a registration-only contract: a descriptor, no execution surface -- Sec 10.2), the registry, and the registry-backed task-issuance resolver; every framework verb ships as a placeholder descriptor carrying its OPSEC attributes, and `GET /capabilities` exposes the catalog to the UI. Sensitive behavior stays out-of-tree (Sec 10.2, Sec 13). |
| `Rod.Persistence` | **Not a layer.** The durable PostgreSQL adapters behind the core-state and audit ports (operators, operator credentials, engagements, implants, sessions, tasks, stager tokens, audit, artifacts), swapped in at the composition root when `ConnectionStrings:Postgres` is set (Sec 12.1). | Not a layer -- may depend on `Rod.CoreState` and `Rod.Audit`; wired only at the composition root, never by transport. | Implemented. EF Core 10 over Npgsql behind a context factory (singleton-safe), migrations, and the full adapter pair; absent the connection string the in-memory adapters stay registered. |
| `Rod.TeamServer` | **Not a layer.** The single runnable .NET process and composition root: it wires `Rod.Transport`'s services and endpoints, terminates mTLS, and serves the built React operator UI same-origin with an SPA fallback. It is where the layers are assembled for `dotnet run`; the layer dependency tests do not constrain it. | Not a layer -- the composition root; depends inward on `Rod.Transport`, `Rod.Operators`, `Rod.Tradecraft`, and `Rod.Persistence` (transport itself cannot reference the outer layers). | Implemented. Wires the layers, binds the configured listeners, and serves the built operator UI same-origin with hardening headers; the build runs the npm bundle first when it is missing (Sec 4.2). |

The dependency column is not aspirational: it is the rule the architecture tests
enforce. `LayerDependencyTests.cs` checks namespace usage, and
`ProjectReferenceTests.cs` checks the csproj reference edges themselves, so adding
a forbidden project reference fails the build even when no code uses it yet.

#### Former ADR index

Until 2026-08 the design decisions lived as numbered ADR files under
`docs/decisions/`; they were folded into this document and the files retired.
Comments in the source tree still cite the ids, so they resolve here:

| ADR | Folded into |
|-----|-------------|
| 0001 monolithic kernel | Sec 4 |
| 0002 .NET reference implant + wire protocol as the product | Sec 4.2, Sec 12.2 |
| 0003 PostgreSQL as the durable store | Sec 12.1 |
| 0004 offensive-tradecraft boundary | Sec 10.2, Sec 13 |
| 0005 (task argument shape) | Sec 10.3 |
| 0006 (capability-catalog endpoint placement) | Sec 4.3 |
| 0007 (placeholder-only verbs) | Sec 10.1 |
| 0008 operator authentication | Sec 4.1 layer 4 |
| 0009 single in-tree .NET toolchain | Sec 12.2 |
| 0010 production implant CA | Sec 9 |
| 0011 production redirector | Sec 4.2, Sec 8 |
| 0012 implant-side capability pluggability | Sec 5.3 |

One consequence of the layer rule: an endpoint backed by data in an outer layer
is exposed by that layer itself and mapped at the composition root, not hosted
in transport. `Rod.Operators` does this for its SSE
stream and `Rod.Tradecraft` for `GET /capabilities` (the capability catalog):
transport may depend on neither, so each owns its endpoint the way it owns its
data. The catalog is a process-global read of the loaded module set -- registry
metadata, not engagement-scoped domain state -- so it earns no CoreState port
and no parallel DTO; an operator-scoped capability concern would be a *separate*
engagement-scoped endpoint, not a retrofit onto the global catalog.

## 5. Implants and profiles

An implant is a short-lived, disposable payload on a target. It is **untrusted by
default** and generates its own keypair at first run -- there is no global
shared secret, and no key material in the artifact at all.
What a from-scratch implant must implement to interoperate -- and what is
optional hardening or optional features -- is specified as a tier ladder in
[implant-contract.md](implant-contract.md); that ladder is the contract's
complexity budget, and its evolution rules bind every future protocol change.

### 5.1 Profiles are baked in at generation

A **profile** -- the check-in mode, beacon parameters (sleep, jitter, kill
date), the transport profile, and the C2 endpoint -- is embedded into the
artifact at build time, so each implant is self-contained and standalone. This
is what makes per-implant OPSEC possible: no two implants look the same, and a
lost implant self-terminates at its kill date. No key material is baked: the
implant's cryptographic identity is the keypair it generates itself at first
run, bound to its engagement by the CA-signed leaf issued at enroll (Sec 9) --
a captured artifact carries nothing reusable.

The bake-in is verified end-to-end: the configured sleep, jitter, and kill date
land in the decoded artifact across the .NET and stub build units, so a
profile that is silently dropped or defaulted fails the build-pipeline tests.

The kill date is enforced on both sides of the wire (Sec 7). The teamserver
refuses to open a session for an implant whose kill date has passed, returning
`HANDSHAKE_STATUS_KILL_DATE_EXPIRED` at handshake before any session or tasking
is recorded; the implant itself refuses to start past its kill date and
re-checks it at the top of each beacon cycle, so a long-running implant
self-terminates the moment the date passes rather than waiting for a reconnect
or restart.

### 5.2 Implant classes (by operational purpose)

Implants differ by purpose, not by a "managed device flavor":

- **Stage-2 implant** -- the primary long-haul implant; full capability set and
  module support. (e.g. the .NET reference implant, cross-platform.)
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
carries the full core set (shell plus both-direction file transfer) plus the
recon set, the lateral set, the persist set, the collect set, and the exfil set
(recon, lateral movement, persistence, collection, and exfiltration are
long-haul activities that justify a stage-2 footprint); a stager only
`file.pull`s the stage-2 it loads; a web-shell and an ephemeral run `shell.exec`
over their short-lived channels; a pivot is reserved for tunneling artifacts --
no tunnel verb has shipped, so it carries an empty set and admits nothing until
the artifact that owns it defines what it runs. No class but Stage-2 carries a
recon, lateral, persist, collect, or exfil verb. The set is the server's
authority for what a class may do: task issuance gates on it in core state (a
verb outside the set is refused before it is queued, Sec 10.3), and the build
pipeline bakes it into each artifact so a generated payload is
self-describing.

Admission is not execution: a verb may be class-admissible (the class gate
does not refuse it) yet ship no built-in handler, running only when an
operator supplies an out-of-tree module. The contract-only verbs
(`collect.keylog`, and the `evasion` and `exploit` categories in their
entirety) follow this shape (Sec 10.2); they are listed in the
class set and in the capability catalog but carry no in-repo handler.

A capable implant can deploy another class on the same host (e.g. a web-shell
deriving a stage-2 implant) via a deployment verb; the child enrols into the same
engagement and records its parent. This is the lateral-movement path:
the `lateral.move` verb is the deployment verb that semantically means
"derive a child," and the child's enrollment records its `ParentImplantId` on
the implant entity. The child enrols through the same enrollment route a
top-level implant takes, naming its parent; the enrollment service resolves and
scope-checks the parent (it must exist, belong to the same engagement the
redeemed token resolved, and not be retired) before binding the child. The
parentage is surfaced on the operator implant listing so the UI can render
lineage; a top-level (stager-derived) implant reports no parent.

### 5.3 Implant-side capability pluggability

The class verb set (Sec 5.2) is the server's authority; the implant's advertised
set is its own, and the two must agree. A reference implant advertises exactly
the verbs its build permits and its compiled handlers implement -- never a verb
it cannot run. The advertised beacon capability set is the intersection of the
baked class verbs with the compiled handler set, and dispatch routes through an
implant-side handler registry (the implant analog of the server's
`ICapabilityModule`) rather than a hard-coded `switch`, so adding a verb is a
handler plus a registration, not an edit to the runner. Registration is
compile-time -- no runtime assembly loading (that would break Native AOT, enlarge
the artifact, and introduce on-disk plugin files), and the capability set is
decided per class at build time, so runtime discovery buys nothing. Out-of-tree
handlers for contract-only verbs (e.g. `collect.keylog`) compile into a separate
per-engagement artifact; the reference implant ships no Sec 13 boundary verb.

Rejected alternatives: runtime dynamic assembly loading for plugins (breaks
Native AOT and the lean artifact, and is unnecessary since the set is fixed at
build time); advertising the full baked class set regardless of implemented
handlers (recreates the unknown-verb-for-an-advertised-verb failure the
intersection exists to prevent); keeping the hard-coded switch and adding
`collect.keylog` in-repo behind a flag (crosses the technique-kind boundary of
Sec 13 and leaves no growth seam); and making the implant class-aware but
keeping the switch (solves advertising but not extensibility -- the registry
is what makes the design durable).

The reference .NET implant implements this end to end. `HandlerRegistry`
holds one compiled handler per verb and is the implant's only dispatch path:
the beacon loop calls it directly and advertises `AdvertisedVerbs` -- the
registry verbs filtered by the baked class set -- at handshake. The build
unit's baked `verbs` key reaches the implant through the profile (mapped onto
`ROD_VERBS`, parsed into `Config.ClassVerbs`); an un-baked dev binary (empty
class set) advertises its full compiled handler set, so the checked-in stub
keeps running from flags/env. The implant tests pin both halves of the
contract: the advertised set is the baked-verbs/handlers intersection for
every class, an added registration widens it, and the reference registry
contains no Sec 13 boundary verb.

## 6. Payload build pipeline (polyglot via decoupled build units)

The flow: **operator build request -> teamserver emits build params -> the
language's build unit compiles -> artifact + stager returned -> fingerprinted and
recorded.**

- **One in-tree build unit (.NET); polyglot by contract.** `DotNetBuildUnit`
  owns the .NET toolchain and compiles the reference implant on demand. The
  teamserver drives it through a **uniform build contract** and is coupled to
  it only by that contract, so a community build unit in Go, C/C++, or Nim can
  register and compile against the same contract with no in-language coupling
  (the `Language` enum keeps those slots, Sec 12.2).
- **Artifacts are self-contained single-file executables for the requested
  target.** The unit maps the build target's OS/arch onto a runtime identifier
  and publishes self-contained (runtime bundled, compressed), so a generated
  implant runs on a stock target with no .NET installed -- the deployment shape
  an operation actually has. An unmappable target fails the build with the
  supported set named rather than silently building for the build host.
- **Build params** include the implant class, target OS/arch, transport
  profile, and beacon parameters (mode, sleep, jitter, kill date). They are
  produced at request time so each artifact is unique -- this is essential for
  OPSEC. No key material crosses the build contract (Sec 5.1).
- **Staging** is a separate output: a stage-1 stager that fetches stage-2 has its
  own generation path.
- **Artifact tracking.** Every generated artifact is fingerprinted and recorded
  (who, when, config) into the audit trail.

The build contract is the language-neutrality boundary; it is what lets the wire
protocol be "the product" while implants stay polyglot.

## 7. OPSEC -- a first-class design axis

OPSEC is a design axis, not a feature flag. The architecture bakes in:

- **Per-implant beacon profile, including the check-in mode.** Two shapes ride
  the same stream contract: **stream** holds one long-lived connection (the
  interactive shape -- server-push tasking, no reconnect cost) and **poll**
  drains queued tasking, closes, and sleeps the interval with **jitter**
  (randomized delta) before the next check-in -- the low-and-slow shape, since a
  persistent connection to a C2 endpoint is itself a loud signal. The mode is
  baked per implant at generation (`mode: stream|poll` on the build request),
  so one engagement can mix an interactive foothold with sleeping beacons.
- **Kill date.** A hard self-termination timestamp baked in per implant to limit
  exposure if lost. Enforced on both sides: the teamserver refuses a handshake
  past it (`HANDSHAKE_STATUS_KILL_DATE_EXPIRED`, no session opens), and the
  implant refuses to start past it and re-checks it each beacon cycle so a
  long-running implant self-terminates the moment the date passes.
- **Per-implant cryptographic identity.** Each implant generates its own RSA
  keypair at first run and submits only the public half at enroll; the teamserver
  CA signs a leaf bound to (implant_id, engagement_id) over it. There is no
  shared secret anywhere, and the artifact carries no key material at all, so a
  captured payload compromises nothing. Compromise handling is the operational
  flow *retire the implant (refused at the next handshake), repoint its
  endpoint, and build a fresh artifact* (Sec 8) -- there is no live in-place
  key swap.
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

- Supported listener transports: **HTTP(S)** and **mTLS** are implemented;
  **DNS**, **SMB**, and **TCP** are planned (the listener abstraction is in
  place, so adding them is a milestone concern, not an architectural one).
  Transport choice is a profile/deployment concern; the protocol semantics are
  transport-independent.
- An implant is always the **connection initiator** (reverse connection). The
  teamserver and redirectors never dial targets.
- **Listener and public endpoint are decoupled, and the endpoint is repointable
  at runtime.** A redirector fronts the listener; a burned redirector is replaced
  without touching the backend by repointing the listener's public endpoint
  (`POST /listeners/{id}:repoint`). The Kestrel bind is untouched; the old
  endpoint simply no longer resolves to any listener, which severs it. This
  decoupling is what makes disposable infrastructure practical. The in-tree
  reference redirector -- an opaque L4 TCP forwarder published as a Native AOT
  binary -- ships this rotation end to end; see
  the deploy/rotate runbook ([operations/redirectors.md](operations/redirectors.md)).
- **Message sizing and flow control.** A single frame stays well under 1 MiB and
  never exceeds the negotiated maximum. Bulk data (files, output) is chunked.
- **Malleable transport profile (per implant).** Each implant carries a transport
  profile baked in at generation (Sec 5.1, Sec 7): an enroll URI path, a
  User-Agent, custom HTTP headers, a per-request timeout, and a body envelope. It
  is applied client-side at enroll -- the reference .NET implant enrolls
  against the profile's path and presents its headers, timeout, and body shape --
  so a profile changes the wire shape. The teamserver's enroll route stays fixed
  at `/implants/enroll`; URI and header routing at the public endpoint is a
  redirector concern (Sec 7). Verified by a build-pipeline round-trip test and an
  httptest-backed wire-shape test that captures the enroll request.
- **A plain-HTTP-envelope listener is a recorded design option, not scheduled
  work.** The same rod.v1 payloads carried as opaque HTTP request/response
  bodies over the same client certificates, dropping the gRPC/HTTP-2
  requirement for target languages with a weak gRPC story. It changes the
  framing, not the protocol semantics; transport choice is already a
  listener/profile concern, so nothing else moves. It exists as the escape
  hatch for implant reach ([implant-contract.md](implant-contract.md)) and is
  built only when a community implant actually needs it.
- Redirectors forward opaque payloads. The in-tree reference is an opaque L4 TCP
  forwarder (Native AOT) that never terminates transport, so the mTLS beacon
  channel and the HTTPS enroll request carry through end to end. It is L4, not
  L7, because the beacon is mTLS: an L7 reverse proxy that terminated TLS could
  not preserve the client-certificate authentication and would have to forward
  at L4 anyway, and an L7 peek for plaintext HTTP re-introduces
  transport-specific logic for marginal gain while breaking the AOT-clean,
  reflection-free property. v1 runs one forwarding rule per process so a burned
  port does not drag the others down (rejected: a multi-rule single process as
  a single point of failure across ports). Source-IP allow-listing is the only
  routing an opaque L4 forwarder can do; malleable User-Agent/URI routing lives
  inside TLS and stays a TLS-terminating-edge concern an operator layers on. A
  deployment that needs such L7 routing terminates TLS at its own edge -- that
  is an operator deployment concern, not an in-tree capability.

## 9. Security model

Rod is remote-code-execution infrastructure: a compromised teamserver is
fleet-wide code execution. Security is a first-class concern.

- **Identity.** Operator identities (credentials, MFA, API tokens); implant
  identities bound to their engagement via client certificates.
- **mTLS.** The mTLS transport is mutually authenticated; an implant's certificate
  binds `(implant_id, engagement_id)`.
- **Production implant CA.** The teamserver consumes an externally provisioned
  engagement CA; it does not generate the production CA. When
  `Pki:CaCertificatePath` and `Pki:CaPrivateKeyPath` are configured,
  `FileBackedCertificateAuthority` loads the CA certificate and its RSA private
  key (optionally passphrase-encrypted) from disk and signs implant leaves with
  the same leaf construction the dev authority uses -- only the issuer changes.
  Absent the config the dev self-signed authority stays. The authority is built
  eagerly at DI registration, so a missing file, an unparseable PEM, a non-RSA
  key, or a key/cert mismatch fails the host at startup, not the first
  enrollment; RSA is the only supported CA key type, matching the implant leaf
  path. Rotation is operational (replace the files and restart). Rejected:
  generating and persisting the CA from the teamserver (re-creates the dev
  posture -- key in the C2 -- at production privilege); `IOptions<T>` binding
  for the `Pki` section (diverges from the audit store, the other
  config-selected adapter, which reads its key straight off `IConfiguration`);
  and bundling a proper TLS server leaf + SAN (scope creep -- the
  CA-as-trusted-root satisfies enrollment binding; a real server leaf with SAN
  stays a separable hardening).
- **Command signing.** Dispatched tasks are signed so an implant only acts on
  teamserver-authorized tasking. The beacon endpoint signs each dispatched
  `TaskRequest` with the tasking CA's RSA key (RSASSA-PSS over SHA-256, on a
  canonical length-prefixed encoding of `implant_id`, `task_id`, `verb`,
  `arguments` documented on the proto message -- not on the serialized
  message, so every implant language verifies identically without depending
  on protobuf field ordering). The implant id in the signed tuple is the
  target implant's own identity, binding tasking to its intended executor: a
  captured signed frame fails verification on any other implant under the
  same CA. The signing key is the same CA that issues implant leaves,
  reached through `SignTasking` on the CA port: the implant already holds
  that CA certificate from enrollment or its pinned bundle, so tasking trust
  rides enrollment trust and no new key distribution exists to protect. The
  implant verifies before any handler runs; an unsigned or wrongly signed
  task is reported `Failed` with the cause on the task itself, so the
  rejection is visible on the operator console and nothing executes.
  Replay of an old task to the same implant by a mid-stream attacker remains
  possible (the teamserver ignores the retransmitted result); per-session
  anti-replay nonces were left out as a separate hardening. Deployment
  order matters: this implant rejects unsigned tasking, so the teamserver
  signs -- upgrade it before deploying implants built from this contract.
  Rejected: a dedicated task-signing key pair (a second teamserver-held
  secret to provision, rotate, and bake into artifacts, for no isolation
  gain while the CA key is already the server's signing identity); signing
  the serialized `TaskRequest` bytes (couples verification to one protobuf
  runtime's serialization behavior).
- **Sealing** _(future, deferred)_. End-to-end protection of task payloads so
  untrusted redirectors cannot read or alter them. Deferred because the
  concrete adversary is absent today: the reference redirector is an opaque L4
  splice, the beacon channel is mTLS terminated at the teamserver, so an
  untrusted hop sees only ciphertext -- and mainstream platforms ship nothing
  equivalent. Building it would put mandatory cryptography on every implant's
  task path (against the implant contract's evolution rules). If it is ever
  built -- for TLS-terminating edges such as domain fronting -- it must be
  handshake-negotiated with a plaintext fallback, so Tier 0 implants keep
  interoperating (see [implant-contract.md](implant-contract.md)).
- **Per-implant identity and rotation.** Each implant owns a keypair it
  generated itself; the server binds it with a CA-signed leaf at enroll and
  never sees the private half (Sec 7, Sec 9). Artifacts carry no key material.
  Rotation is the operational flow *retire the compromised implant, repoint its
  endpoint, and build a fresh artifact*; there is no live in-place key swap.
- **Retirement.** An implant can be retired from the operator API
  (`POST /engagements/{engagementId}/implants/{implantId}:retire`); a retired
  implant is refused at handshake (`HANDSHAKE_STATUS_IMPLANT_RETIRED`, no session
  opens), is untaskable (`422`), and its active session is closed. Retirement is
  idempotent and recorded as an `ImplantRetired` audit event in the engagement
  trail; any queued tasks for it are left inert (no dispatch, no cancellation).
- **Certificate revocation.** Both credential halves revocate at the
  application layer and take effect on the next authentication attempt with no
  restart -- no CRL/OCSP plumbing, which would be heavier than the threat
  (neither mTLS peer consults one, so a real CRL would be unenforced
  ceremony). The implant half is retirement itself: the refusal at the next
  handshake is the revocation, pinned by
  `HandshakeServiceTests.Handshake_RefusesRetiredImplant`. The operator half
  is `POST /operators/{operatorId}/credentials:revoke`: it deletes the stored
  password verifier (any authenticated operator may call it; the action is
  idempotent), and login -- which reads the verifier fresh on every attempt --
  fails from then on. Re-provisioning the operator with a new password
  restores login; active cookie sessions outlive the credential they were
  issued from (cookies are self-contained), and ending live sessions on
  revoke is a separate hardening. Revocation is not recorded in the audit
  trail: the trail is engagement-scoped and an operator credential is global
  state, so it has no engagement to live in.
- **Kill-date enforcement.** The teamserver refuses to open a session for an
  implant past its baked-in kill date (the handshake returns
  `HANDSHAKE_STATUS_KILL_DATE_EXPIRED`), and the implant self-terminates past it
  on startup and each beacon cycle -- a lost implant cannot stay live past its
  date even if it ignores its own check.
- **Audit trail.** Every privileged action produces an immutable, hash-chained
  `AuditEvent`. Tampering breaks the chain (Sec. 11).
- **Engagement isolation.** Enforced at the teamserver and by engagement binding
  in certificates; redirectors never enforce tenancy.
- **ROE guardrails.** Each engagement carries a rules-of-engagement profile
  the server enforces at task issuance, after the class gate and before the
  task is queued: `PermittedVerbs` (exact verbs or `namespace.*` wildcards)
  and `PermittedImplants` (exact implant ids), each dimension empty meaning
  unrestricted. A task outside the profile is refused with `422` and a
  `TaskRoeRefused` audit event naming the violated rule -- the refusal is
  part of the engagement's story, so it lands in the same trail as the
  tasking it blocked; the scope change itself is recorded as `RoeUpdated`.
  Operators apply a profile over the API (`PUT /engagements/{id}/roe`);
  applying an empty profile reopens the engagement. The scope is pure
  server-side state on the engagement (JSON column in the durable store, the
  unrestricted default for records that predate it) -- the implant contract
  carries nothing for it (implant-contract.md, evolution rule 4). Warn-only
  modes and audit-history-driven rule suggestions stay future concerns; the
  shipped gate blocks, because a warning an operator can click through is
  not a rule of engagement.
- **No self-protection.** Rod ships no protection against its own detection by
  defenders. Stealth is a deployment and capability concern (Sec. 7), not a
  security boundary of the platform.

## 10. Capability model and tasking

A **capability** is a verb an implant advertises and the teamserver may dispatch,
namespaced `namespace.action`, each carrying a `version` and `attributes`. The
teamserver gates dispatch on the advertised verb.

A task's **arguments stay a single opaque `string` at every contract boundary**
-- the proto field, core state, the transport DTO, the dispatch contract, and the
implant's dispatch entrypoint. The verb is the typed discriminator; the string is
the verb's own grammar, parsed by the handler that owns it (whitespace tokens,
hyphen ranges, comma lists, trailing-command shapes -- deliberately diverse, no
shared parser). A `string` is the lowest-common-denominator shape every implant
language parses with its own stdlib, it keeps the server out of argument
validation (the server gates on the verb and passes the string through
untouched), and it keeps each language's parser free. The escape hatch is
per-verb, not global: when one verb's grammar outgrows a string (streaming
input, binary blobs, nested config) it gets its own typed proto arm, leaving the
opaque field and every other verb untouched. A shared typed-argument schema was
rejected because the grammar is per-verb, not per-system -- it would move the
grammar into the proto without removing it and couple every implant language to
one schema.

### 10.1 Capability categories

| Category | Example verbs | Summary |
|----------|---------------|---------|
| **core** | `shell.exec`, `file.push`, `file.pull` | The mandatory-to-useful baseline: command execution and file transfer in both directions. `file.pull` returns small files inline and streams large ones into the artifact store; `file.push` writes a base64 payload (capped at 1 MiB per task) to the target. |
| **recon** | `recon.portscan`, `recon.hostenum`, `recon.service` | Target and network reconnaissance. |
| **lateral** | `lateral.move`, `lateral.token`, `lateral.exec_remote` | Lateral movement within authorized scope. |
| **persist** | `persist.install`, `persist.remove`, `persist.list` | Persistence mechanisms. |
| **collect** | `collect.cred`, `collect.keylog` | Credential and input collection. Operator file transfer is a core verb (`file.push`/`file.pull`), not collection. |
| **exfil** | `exfil.push`, `exfil.stage` | Exfiltration over the C2 channel. |
| **evasion** | `evasion.avoid`, `evasion.unload` *(contract only)* | Detection-evasion hooks. Contract and dispatch only. |
| **exploit** | `exploit.invoke`, `exploit.module` *(contract only)* | PoC/exploit integration point. Contract and dispatch only. |

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
recon they are gated to Stage-2 at task issuance (Sec 5.2). The core provides
the parentage data model and the child-enrollment path -- the server records a
child's `ParentImplantId` and validates it against the redeemed token's
engagement, so a child derives only from a live parent in the same engagement.
The reference implant carries the matching implant-side path: a
`lateral.move` handler on the implant parses the child's stager token from the
task arguments, generates a fresh child keypair, and enrolls a child naming
itself as parent; the enroll clients thread parentage onto the request, and the
binary `EnrollResponse` gains a `parent_implant_id` so the wire surface mirrors
the HTTP path. The `lateral.token` and `lateral.exec_remote` verbs also ship
in-repo reference handlers under the Sec 13 boundary (AGENTS.md Sec 7): `lateral.token` enumerates the current process's Windows access-token
context (user, groups, privileges) via `whoami`, the documented administration
command for inspecting the calling token; `lateral.exec_remote` runs a command
on a remote host over documented administration channels (scheduled tasks on
Windows, SSH on Linux). The same surface every mainstream C2 exposes for
these activities.

The persistence verbs are registered the same way
(`Rod.Tradecraft.Persist.PersistCapabilities`, category `Persist`):
`persist.install` and `persist.remove` carry `writes-to-disk` attributes
(install additionally carries `persists`), and `persist.list` is a read that
carries no such flag, like the host-local `recon.hostenum`. Like recon and
lateral they are gated to Stage-2 at task issuance (Sec 5.2). Persistence is a
long-haul activity, and the reference implants ship standard, documented
mechanisms under the Sec 13 boundary (AGENTS.md Sec 7): the Windows
`Run` registry key, scheduled tasks, and services, plus Linux cron and
systemd user units -- the documented persistence surfaces every system
administrator and offensive-security curriculum covers. Install, list, and
remove round-trip against these surfaces. Novel or stealth persistence
techniques remain out-of-tree.

The collection and exfiltration verbs are registered the same way
(`Rod.Tradecraft.Collect.CollectCapabilities`, category `Collect`, and
`Rod.Tradecraft.Exfil.ExfilCapabilities`, category `Exfil`): `collect.cred`
carries a `reads-credential` attribute, and `collect.keylog` carries
`reads-input` and `persists` (it installs a resident input-capture hook);
`exfil.push` carries a `touches-network` attribute (it transfers over the C2
channel), and `exfil.stage` is a read that carries no such flag, like
`persist.list` and the host-local `recon.hostenum` (it stages already-collected
data on the teamserver). Like recon, lateral, and persist they are gated to
Stage-2 at task issuance (Sec 5.2). Collection and exfiltration are long-haul
activities. Under the Sec 13 boundary (AGENTS.md Sec 7) the reference implant
ships in-repo handlers for the core file verbs (`file.pull` reads the target's
filesystem -- small files return inline, large ones chunk into the exfil
channel -- and `file.push` lands an operator-supplied payload on disk),
`collect.cred` (standard credential-store *listings* -- SSH key presence with
fingerprints, AWS profile names, Windows saved-credential names via
`cmdkey /list` -- without dumping secret material), and `exfil.push` /
`exfil.stage` (data transferred over the C2 channel into engagement-scoped
artifact storage, Sec 11). Two collection surfaces stay out-of-tree as
pluggable contracts: LSASS memory dumping (no benign-system-tool side, tightly
coupled to active credential theft) and `collect.keylog` input capture. Each of
those runs only when an operator supplies an out-of-tree module for the verb.

The evasion verbs are registered the same way
(`Rod.Tradecraft.Evasion.EvasionCapabilities`, category `Evasion`): both
`evasion.avoid` and `evasion.unload` carry a `modifies-defenses` attribute,
since each alters the target's defensive or monitoring posture (Sec 7). Unlike
the recon, lateral, persist, collect, and exfil verbs they are **not** gated to a
class in `ImplantClassCapabilities` (Sec 5.2): evasion is contract and dispatch
only -- which class an evasion module runs on is decided when an operator deploys
the out-of-tree module, not by a baked-in class rule. Their concrete behavior is
out-of-tree tradecraft (Sec 10.2, Sec 13, AGENTS.md Sec 7, RESPONSIBLE-USE.md):
the core ships no bypass techniques or weaponized code, so each verb runs only
when an operator supplies an out-of-tree module for it.

The exploit verbs are registered the same way
(`Rod.Tradecraft.Exploit.ExploitCapabilities`, category `Exploit`): both
`exploit.invoke` and `exploit.module` carry an `exploits-target` attribute,
since each actively attacks a target to gain or widen access (Sec 7). Like the
evasion verbs they are **not** gated to a class in `ImplantClassCapabilities`
(Sec 5.2): exploit is contract and dispatch only -- which class an exploit
module runs on is decided when an operator deploys the out-of-tree module, not by
a baked-in class rule. Their concrete behavior is out-of-tree tradecraft
(Sec 10.2, Sec 13, AGENTS.md Sec 7, RESPONSIBLE-USE.md): the core ships no
weaponized exploit code or proof-of-concepts, so each verb runs only when an
operator supplies an out-of-tree module for it.

### 10.2 Sensitive-capability boundary

`evasion` and `exploit` are first-class in the capability model -- they have
defined interfaces, registration, dispatch, and data shapes. Their **concrete
behavior is intentionally not part of the core**: the core provides the contract
and the plumbing; the tradecraft is supplied as separate, opt-in, out-of-tree
`CapabilityModule`s. See Sec. 13.

Every built-in verb is registered in the default registry, contract-only ones
included: a contract-only verb is a real `PlaceholderCapabilityModule` that
satisfies the registry and the task gate until an operator supplies a module.
That makes the out-of-tree path a *registration*, not a schema change -- a
module registered for `evasion.avoid` or `exploit.invoke` replaces the
placeholder (last-registration-wins) and is taskable through the same UI and
gate as any built-in verb. The one runtime loader is config-listed and narrowly
bounded: the `Tradecraft:Modules` section names each module as a
`Namespace.Type, AssemblyName` string, the assembly is resolved by that name
alone (already loaded, or a same-named dll in the application directory), and
the type is instantiated at startup and registered against the DI-resolved
registry (last-registration-wins). There is no directory scanning and no
arbitrary plugin path -- a module reaches the process exactly when an operator
built it, placed it next to the binary, and listed it, so adding one never
edits the composition root; a misconfigured entry fails startup loudly. The
capability contract is registration-only: `ICapabilityModule` carries its
`Descriptor` and nothing else -- there is no server-side
dispatcher surface to retire. The server only gates and forwards on the
live task path -- it never invokes a capability module server-side -- so
execution and dispatch stay on the implant (Sec 5.3), where the target's
filesystem, network, and credentials actually live.

### 10.3 Tasking lifecycle

`Task` -> dispatched to a `Session` -> `TaskExecution` (streams, result, status)
-> recorded in the engagement audit trail. Sensitive verbs additionally require
engagement authorization and are always audited.

A session is the implant's live channel, not one TCP connection: the handshake
**reuses** an implant's active session on a reconnect (a poll-mode check-in or a
flapped stream refreshes capabilities and last-seen) and opens a new entity only
after the prior one closed, so a poll cadence neither churns session entities
nor floods the trail with `SessionOpened` records -- the audit write happens
only for a genuinely new session. Each beacon frame advances the session's
last-seen stamp, and a stream ending does not close it; liveness is last-seen
based. A stream that dies silently -- the implant vanishes mid-stream, or the
connection drops without a clean close -- leaves its session Active until the
hosted staleness sweeper closes every Active session whose last-seen stamp is
older than the configured `Sessions:Staleness:Threshold` (checked every
`Sessions:Staleness:SweepInterval`); retirement closes a session immediately.
Closing the session is what drops the implant off the online roster; each swept
close also fans out a `SessionClosed` live event so connected operators see it
immediately, and the beacon stream's reader ends the connection on its next
frame so a recovered implant reconnects and re-handshakes instead of refreshing
a session it no longer holds.

Task issuance gates the verb through a capability resolver
(`ITaskCapabilityResolver`). The per-class reduced verb set (Sec 5.2) is the
primary authority; the composition root swaps in a registry-backed resolver
(`CapabilityRegistryTaskResolver` in `Rod.Tradecraft`) so a verb the class set
does not admit is still dispatchable when a capability module is registered for
it. The registry only widens the gate -- it never narrows it -- and it is the
path that opens dispatch for the contract-and-dispatch-only categories (Sec
10.2): the evasion and exploit verbs are not class-gated, so they are admitted
when the registry holds a module for them (the built-in placeholder, or an
operator-supplied out-of-tree override). A verb outside both the class set and
the registry is refused before the task is queued. Verb execution itself stays
on the implant: the teamserver resolves the gate, hands the verb to the beacon
stream, and captures the result.

## 11. Evidence and reporting -- a first-class output

A red-team operation ends in a deliverable: timeline, findings, and evidence. Rod
treats the audit trail as the **source for report generation**, not a post-hoc
scrape.

- **Every action is an immutable, attributed event**: `operator_id`,
  `engagement_id`, `implant_id`, `task_id`, `command`, `timestamp`, input
  parameters, and output/result. Linked artifacts are recorded as separate
  `ArtifactAttached` (operator attach) or `ExfilCaptured` (implant-side exfil)
  events whose outcome carries the artifact id -- artifacts live in the artifact
  store, joined by `task_id`, not as a field on every event. This is the
  engagement timeline by construction.
- **The event log is append-only and per-engagement**; it is never deletable
  mid-operation (chain-of-custody).
- **Artifacts** (files, screenshots, command output) are first-class objects
  linked to tasks, not loose files.
- **Timeline and report export** are built-in consumers of the event + task +
  artifact store -- the audit trail renders directly into the deliverable.
- **The operator-facing listings are paged.** The task, audit, and artifact
  list endpoints accept a `limit` and an opaque cursor (the newest window
  first; each page's cursor walks one page older), and the operator UI walks
  pages, so a long engagement never grows a listing response without bound.
  Exports still read the full store -- the deliverable is the whole trail by
  design; only the interactive read views are paged.
- **The audit trail outlives the operation.** It is retained after infrastructure
  teardown; ROE guardrails read from the same store. When the composition root
  finds an `Audit:DataDirectory`, it swaps the in-memory
  `IAuditStore`/`IArtifactStore`/`IPayloadStore` for file-backed ones --
  JSON Lines for the trail and the artifact metadata, a
  blob per artifact -- so the per-engagement trail and its evidence survive a
  teamserver restart and infrastructure teardown. Each append writes and flushes
  one hash-chained record, and the store recovers each engagement's chain head on
  startup, so a restarted teamserver continues each engagement's trail off its
  last stored event and the reloaded chain still verifies. This stands in for Postgres
  behind the same ports; a managed store slots in the same way.

## 12. Technology stack and language boundaries

| Concern | Choice | Why |
|---------|--------|-----|
| Teamserver (monolithic kernel) | .NET 10 (LTS), ASP.NET Core, gRPC | Strong async networking, first-class gRPC, strong typing, mature web UI. LTS to ~2028. |
| Data store | PostgreSQL (opt-in; in-memory default) | Authoritative teamserver state; per-engagement audit. PostgreSQL is the authoritative store when configured (`ConnectionStrings:Postgres`); absent it, in-memory adapters remain the default for tests and dev deployments (see Sec 12.1). |
| Build units | .NET (in-tree, implemented); Go/C/C++/Nim via out-of-tree community units (see Sec 12.2) | One in-tree toolchain; polyglot by contract, no teamserver-language coupling. |
| Redirectors | .NET Native AOT (shipped), single static binary | Tiny VPS footprint, no runtime install. The teamserver-side rotation path (listener repoint) and the in-tree opaque L4 forwarder both ship; deploy/rotate runbook in [operations/redirectors.md](operations/redirectors.md). |
| Implants | .NET (reference implant shipped); Go/C/C++/Nim via out-of-tree community units -- per target | One .NET reference implant; community implants slot in by contract for targets .NET does not fit. |
| Operator UI | Web (React + TypeScript, Vite), served same-origin by the teamserver | React sources in `src/teamserver/Rod.TeamServer/Client/`; the production build emits into the host's `wwwroot/`, served as static files with an SPA fallback so the client owns deep links, and Vite's dev server proxies the operator API in development. Chosen over Blazor for the larger React ecosystem and audience reach, trading away Blazor's .NET-native service reuse and adding a Node/Vite step to CI. The UI talks to the operator HTTP API over `fetch` (no direct .NET injection), keeping the API the single integration point. |

The wire protocol and capability registry are the long-lived, language-neutral
contract implants build against; the build contract is the language-neutrality
boundary for generation.

### 12.1 Data access (PostgreSQL via EF Core)

PostgreSQL is reached through **Entity Framework Core 10** over the Npgsql
provider, with all persistence code isolated in a dedicated `Rod.Persistence`
project that depends inward on `Rod.CoreState` and `Rod.Audit` only. The inner
ring (`Rod.CoreState`, `Rod.Audit`) is zero-package, so the EF/Npgsql dependency
cannot live there; `Rod.Persistence` is the structural answer, the same reason
`Rod.Operators` and `Rod.Tradecraft` are separate projects wired at the
composition root. The domain model stays persistence-ignorant -- no EF
attributes, no concurrency fields on entities -- and ids map to Postgres `uuid`
through per-id value converters; enums are stored as `int` to keep the audit
chain's canonical `(int)Kind` hash stable. Concurrency (single-use stager-token
redeem, task FIFO) lives at the adapter, not on the domain. The durable
adapters are selected at the composition root when `ConnectionStrings:Postgres`
is present, replacing the in-memory defaults through the same opt-in swap the
other ports use; absent it, the in-memory adapters stay and every existing test
is unchanged. The audit chain math stays in `Rod.Audit` and is untouched -- a
durable store recovers each engagement's chain head from the highest-sequence
row on startup and stamps new appends through the same `ComputeHash`. The
acceptance test provisions a live Postgres via Testcontainers, gated to skip
(not fail) when Docker is unavailable.

Rejected alternatives: **raw Npgsql** (loses migrations and the
value-converter/construction story across six aggregates, and contradicts the
EF-migration command the toolchain already commits to); **Dapper over Npgsql**
(same drawbacks for a smaller saving); and a **managed-Postgres-as-a-service
abstraction** (premature -- it defers the access question this answers without
resolving how the host reaches the database today).

### 12.2 Toolchain: a single in-tree .NET stack

Rod ships one in-tree toolchain end to end: **.NET 10**. The reference implant
and the in-tree redirector are both .NET; the Go reference implant and its
build unit were removed. The wire protocol remains the language-neutral product
and the `Language` enum (Go/DotNet/C/Nim) and build contract stay, so an
out-of-tree community implant in Go, C, or Nim registers a build unit and
compiles against the same contract -- polyglot by contract, not by in-tree
parity. .NET is cross-platform via self-contained publishes
(Linux/Windows/macOS from one source), and Native AOT produces the
single-file, no-runtime binary that was the original reason to reach for Go on
the redirector edge.

Rejected alternatives: **keep both reference implants in lockstep** (recurring
cost of writing and maintaining every verb twice, no longer forced by
cross-platform reach); **collapse to Go instead of .NET** (the control plane
is .NET 10, so standardizing on .NET keeps the whole stack in one toolchain);
and **asymmetric polyglot -- .NET full, a second language specialist only**
(still leaves a second toolchain to build and test in CI for a small team,
with no benefit over the opt-in contract path). The trade-off the .NET-only
choice accepts: a larger self-contained footprint than a Go static binary, and
a CLR/AMSI/ETW surface more heavily instrumented by Windows AV/EDR --
acceptable for the reference/learning posture, with the class of tradecraft
that needs another language arriving as an out-of-tree community implant
through the contract this keeps open.

## 13. Sensitive-capability statement

The boundary between in-repo and out-of-tree tradecraft is decided by **what
kind of technique it is**, not by capability category. The line is drawn by
technique kind because category is the wrong axis: a category-wide "in" pulls
LSASS dumping in alongside benign credential-store listings, and a
category-wide "out" pushes out documented token manipulation alongside novel
evasion. Two alternatives were rejected on that test -- keeping the original
"all tradecraft out-of-tree" boundary (leaves the reference implants
contract-only and forces every operator to rebuild the same standard handlers
the field already publishes), and deleting the boundary entirely (in-the-wild
zero-days and weaponized proof-of-concepts create real harm the standard,
documented techniques do not). When it is unclear which side a technique falls
on, default to out-of-tree; tightening later is cheap, and loosening under
pressure is how the line erodes.

- **In-repo: standard, mainstream, documented techniques.** Mechanisms
  documented in OS vendor references and covered by offensive-security
  curricula and peer frameworks (Metasploit, Sliver, Havoc) ship in the
  reference implants so Rod is useful for learning, research, and authorized
  red-team work out of the box. This currently covers shell execution, file
  transfer in both directions, host and port reconnaissance, child-implant
  derivation, Windows access tokens, remote execution over documented admin
  channels, standard persistence surfaces (Run key, scheduled tasks, services,
  cron, systemd), standard-store credential collection (listings only, no
  secret material), and C2 exfiltration into engagement-scoped artifact
  storage.
- **Out-of-tree: sensitive tradecraft only.** In-the-wild zero-days,
  weaponized proof-of-concepts, novel or unpublished detection-evasion and
  bypass techniques, LSASS memory dumping for credential theft, and input
  capture (keyloggers) are part of Rod's capability model as **pluggable
  contracts**: the core defines their interfaces, registration, dispatch, and
  data models; the concrete tradecraft is supplied as separate, opt-in,
  out-of-tree modules the operator deploys. The core ships none of it.
- All use assumes an authorized context; see
  [RESPONSIBLE-USE.md](../RESPONSIBLE-USE.md).

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
  redirector decoupling, burn handling, and per-implant identity as first-class.
- **Evidence and reporting** -- an immutable, attributed, hash-chained audit
  trail that is the source for timeline and report generation, surviving
  infrastructure teardown.

When a capability area falls short of this bar, the right response is to raise
the design, not to lower the bar. Concrete evasion techniques and exploit code
remain out-of-tree modules (Sec. 13); the bar above concerns the platform's
capability substrate, not bundled tradecraft. The bar has one hard boundary it
may never cross: it applies to the teamserver substrate and the contract's
quality, never to the implant-side minimum. Capability reach grows in the
server, the tradecraft modules, and the build pipeline -- an addition that
would put mandatory new work on every implant's task path fails this bar
outright, whatever it adds (see [implant-contract.md](implant-contract.md)).
