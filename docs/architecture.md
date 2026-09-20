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
5. **Beaconing / contact.** The implant calls in; the teamserver authenticates
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
  profile in at compile time. The reference set it carries is the standard,
  documented tradecraft surface (Sec 13); anything beyond it arrives through
  the extension seams. The wire protocol is the language-neutral product, so a
  community
  implant in Go, C, or Nim builds against the same contract without coupling
  the teamserver to its language (Sec 12.2).
- **Redirectors.** Near-stateless forwarders (.NET, Native AOT, single static
  binary) for OPSEC
  and infra flexibility. No engagement state, no business logic. (Sec. 8.)
  The in-tree reference forwarder ships (`src/redirector/dotnet/`): an opaque
  L4 TCP splice published as a single static binary. Together with the
  server-side rotation path -- listener repoint
  (`POST /engagements/{engagementId}/listeners/{id}:repoint`)
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
| `Rod.Protocol` | **Not a layer.** The gRPC/protobuf wire protocol: frames, the enrollment/handshake/tasking messages, and the `Beacon` contact stream (Sec. 8). The long-lived, language-neutral contract implants of every language build against. | Not a layer -- depends on nothing in-house; never leaks into `Rod.CoreState`. | Implemented. Versioned handshake (major.minor), a status code for every enrollment/handshake refusal, and the chunked exfil frame kind (Sec 8, Sec 10.1). |
| `Rod.Transport` | Listeners that terminate C2 transports and map core-state use cases onto the operator HTTP API and the implant beacon stream. Owns endpoint routing, mTLS termination, and the mapping of use-case failures to wire status codes. | Layer 2 -- may depend on `Rod.CoreState`, `Rod.Protocol`, `Rod.Audit`, `Rod.BuildPipeline`. | Implemented. HTTP(S) and mTLS listeners with the bind decoupled from the public endpoint (a repoint swaps a burned redirector without touching the socket); the full operator API (engagements, stager tokens, implants with notes and retirement, tasks with queued-task cancellation, artifacts, audit, timeline/report, payloads) and the beacon stream with bounded frames, capped exfil reassembly, and atomic task dispatch (Sec 8, Sec 10.3, Sec 11). The task, audit, and artifact listings are paged (limit + opaque cursor, newest window first) so a long engagement never grows a listing response without bound; the operator UI walks pages. |
| `Rod.BuildPipeline` | Drives the external, per-language build units to compile polyglot implants on demand through the uniform build contract, fingerprinting and recording each artifact (Sec. 6). | Layer 3 -- may depend on `Rod.CoreState`. | Implemented. `DotNetBuildUnit` -- the sole in-tree unit -- publishes the reference implant in a per-build staging copy in any of the four artifact formats (self-contained single-file default, trimmed, native AOT, or the in-memory-loadable dll bundle; runtime identifier mapped from the build target for the executable shapes), baking the profile (transport shape, beacon parameters, class verb set) without any key material; the built bytes land in the payload store for operator download (Sec 6). |
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
[extending/implants.md](extending/implants.md); that ladder is the contract's
complexity budget, and its evolution rules bind every future protocol change.

### 5.1 Profiles are baked in at generation

A **profile** -- the contact mode, beacon parameters (sleep, jitter, kill
date), the transport profile, and the C2 endpoint list -- is embedded into the
artifact at build time, and the bake is the artifact's whole configuration:
the release build (what the pipeline publishes, and what lands on targets)
reads no flags and no environment, so a fielded executable cannot be
re-pointed or re-credentialed and runs bare -- zero arguments, console silent
beyond fatal one-liners. The debug build is the dev shape: flags and the
ROD_* environment drive the checked-in empty profile stub, and the run
narrates to stderr; a bake, when a debug build carries one, still overrides
both. This
is what makes per-implant OPSEC possible: no two implants look the same, and a
lost implant self-terminates at its kill date. No key material is baked: the
implant's cryptographic identity is the keypair it generates itself at first
run, bound to its engagement by the CA-signed leaf issued at enroll (Sec 9).
What a captured artifact does carry is the enrollment credential the build
minted for it (Sec 6): a deployment secret, not key material -- single-use by
default (a zero max-uses budget is unlimited), bounded by the artifact's own
kill window when one is pinned and 30 days when there is none, bound to its
engagement's
listener scope, revocable by id the moment it is known to have leaked. A build
that mints nothing bakes nothing, and the credential never passes through an
operator's hands.

The endpoint list is ordered: a primary callback endpoint plus optional
fallbacks, walked client-side when an entry burns (Sec 8).

The bake-in is verified end-to-end: the configured sleep, jitter, and kill date
land in the decoded artifact across the .NET and stub build units, so a
profile that is silently dropped or defaulted fails the build-pipeline tests.

The kill date is optional: unset (the default) bakes an open-ended artifact --
the long-haul posture with no time fuse at all -- and the implant reports its
baked date at enroll so the teamserver's record mirrors the artifact's own.
It is enforced on both sides of the wire (Sec 7). The teamserver
refuses to open a session for an implant whose kill date has passed, returning
`HANDSHAKE_STATUS_KILL_DATE_EXPIRED` at handshake before any session or tasking
is recorded; the implant itself refuses to start past its kill date and
re-checks it at the top of each beacon cycle, so a long-running implant
self-terminates the moment the date passes rather than waiting for a reconnect
or restart.

The contact cadence reports the same way the kill date does: the enrollment
carries the artifact's baked sleep/jitter pair, and every contact's handshake
re-advertises the implant's current pair, so the teamserver's record tracks a
`beacon.sleep` retune at the next contact -- what the fleet's detail view
reads. An implant that reports no cadence (a pre-field client) records none;
the record shows the honest absence rather than an invented pair.

### 5.2 Implant classes (by operational purpose)

Implants differ by purpose, not by a "managed device flavor":

- **Stage-2 implant** -- the primary long-haul implant; full capability set and
  module support. (e.g. the .NET reference implant, cross-platform.)
- **Stager** -- a stage-1 loader that fetches a stage-2 implant and runs it by
  its format: hosting a dll bundle in its own process, or executing a native
  stage-2 (from memory on Linux). Separate generation output class.
- **Web-shell class** -- a script placed in a web root, bound to the web
  transport; code execution over HTTP, no interactive PTY. The endpoint is
  operator-initiated in every phase: registration creates the class's
  anchor implant row directly (no enrollment, no handshake, no session --
  the register action itself is the engagement binding), and execution is
  the operator's request driving one adapter round trip synchronously
  (Sec 10.3's exception). The connection profile (URL, protocol adapter,
  credential, encoder pair) is the side table of the anchor row; the
  protocol adapter registry carries the two in-tree families -- the
  Rod-native one (a one-line placed script whose baked 256-bit key seals
  the channel as AES-256-GCM in both directions; the credential is that
  key, not a password) and the universal one-liner eval family (the
  classic `@eval($_POST[...])` shape every manager drives; scripts placed
  by hand or by another tool answer it because the one-liner itself is
  universal) -- and leaves any other family to out-of-tree tradecraft
  (Sec 13).
- **Ephemeral** -- a short-lived, TTL'd implant from a one-liner bootstrap; for
  one-off execution and temporary access.
- **Pivot** -- an implant that represents hosts which cannot run their own
  implant (network/OT gear), enrolling each as its own session and forwarding
  tasking. Its class set is the tunnel set (Sec 10.1): a Pivot-class build is
  the minimal tunneling artifact, and the parent-fronting shape for
  unplantable hosts rides the same set. Fronting ships: a Pivot child
  enrolled by a parent (`lateral.move`, this section) has no process of its
  own and never handshakes -- no session, no stream -- so its tasking is
  claimed by the parent's beacon stream (the fronting claim, Sec 10.3),
  arrives marked with the child's id, and executes in the parent with every
  record attributed to the child (Sec 9's signature already binds the
  target's id into the tuple; the frame's `target_implant_id` marking carries
  the routing). The reference implant fronts what it enrolled: its
  `lateral.move` handler records each Pivot child in a fronted ledger, and
  the beacon loop refuses fronted tasking for an implant not in it -- the
  enrollment this implant performed is the voucher.

Each class carries a **reduced verb set** -- the subset of the verbs its
purpose justifies, defined in `Rod.CoreState.ImplantClassCapabilities` (the
inner ring both the build pipeline and the tradecraft layer read). Stage-2
carries the full core set (one-shot and interactive shell execution,
both-direction file transfer, directory listing, process termination, and
the beacon's own sleep control) plus the
tunnel set, the recon set, the lateral set, the persist set, the collect set,
and the exfil set (tunneling and process control join stage-2's core
operations, and recon,
lateral movement, persistence, collection, and exfiltration are long-haul
activities that justify a stage-2 footprint); a stager only `file.pull`s the
stage-2 it loads; a web-shell and an ephemeral run `shell.exec` over their
short-lived channels; a pivot carries exactly the tunnel set --
`tunnel.forward`, the port-forward verb, and `tunnel.socks`, the
multiplexed proxy (Sec 10.3) -- enough to forward traffic for hosts that
cannot run their own implant and nothing a long-haul footprint justifies.
No class but Stage-2 carries a recon, lateral, persist, collect, or exfil
verb. The set is the server's authority for what a class
may do: task issuance gates on it in core state (a verb outside the set is
refused before it is queued, Sec 10.3), and the build pipeline bakes it into
each artifact so a generated payload is self-describing.

Admission is not execution: a verb may be class-admissible (the class gate
does not refuse it) yet ship no built-in handler, running only when an
operator supplies an out-of-tree module. The contract-only verbs
(`collect.keylog`, and the `evasion` and `exploit` categories in their
entirety) follow this shape (Sec 10.2); they are listed in the capability
catalog but carry no in-repo handler -- `collect.keylog` inside the stage-2
class set, the evasion and exploit verbs outside every class set as ungated
contract verbs the bake carries alongside the class set (Sec 5.3), so an
artifact compiled with an out-of-tree handler for one advertises it at
handshake.

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
baked verbs (the class set plus the ungated contract-only verbs) with the
compiled handler set, and dispatch routes through an
implant-side handler registry (the implant analog of the server's
`ICapabilityModule`) rather than a hard-coded `switch`, so adding a verb is a
handler plus a registration, not an edit to the runner. Registration is
compile-time -- no runtime assembly loading for *handler plugins* (that would
break Native AOT, enlarge
the artifact, and introduce on-disk plugin files; the in-memory loading path
that does exist in the tree is the loader's stage-2 host, a baked artifact
carriage rather than a plugin mechanism), and the capability set is
decided per class at build time, so runtime discovery buys nothing. Out-of-tree
handlers compile in through the build unit's extension overlay (Sec 6) -- a
configured extension directory whose sources overlay onto the per-build staging
tree, with generated registrations feeding the registry's `additional` seam.

Rejected alternatives: runtime dynamic assembly loading for plugins (breaks
Native AOT and the lean artifact, and is unnecessary since the set is fixed at
build time); advertising the full baked class set regardless of implemented
handlers (recreates the unknown-verb-for-an-advertised-verb failure the
intersection exists to prevent); keeping the hard-coded switch and adding
`collect.keylog` in-repo behind a flag (bypasses the module contract and
leaves no growth seam); and making the implant class-aware but
keeping the switch (solves advertising but not extensibility -- the registry
is what makes the design durable).

The reference .NET implant implements this end to end. `HandlerRegistry`
holds one compiled handler per verb and is the implant's only dispatch path:
the beacon loop calls it directly and advertises `AdvertisedVerbs` -- the
registry verbs filtered by the baked verb set -- at handshake. The build
unit's baked `verbs` key reaches the implant through the profile (mapped onto
`ROD_VERBS`, parsed into `Config.ClassVerbs`); an un-baked dev binary (empty
class set) advertises its full compiled handler set, so the checked-in stub
keeps running from flags/env. The implant tests pin both halves of the
contract: the advertised set is the baked-verbs/handlers intersection for
every class, an added registration widens it, and the .NET reference registry
contains no contract-only verb (the Rust reference carries its own compiled
set -- core verbs everywhere, the sensitive three on Windows builds).

The class verb set is also a compile-time boundary, not only an
advertise-time one: the bake trims each implant-class build to the verbs its
class runs. The reference registrations sit behind a selection seam
(`HandlerSelection`) the build unit rewrites per bake -- a reduced-class
build generates the registrations naming only the class's verbs and deletes
the unused handler sources from the compilation whole, the same whole-file
trim the transport selection applies -- so a reduced class is a genuinely
reduced binary: the code for capabilities the artifact will never run
neither links nor ships. The trim is per source file (a file keeping one
verb keeps all its handlers, and its support files with it; the handlers
themselves register only for the class's own verbs), and the shared dispatch
infrastructure -- the registry machinery, the channel contract, the enroll
bundle, the chunker -- is always compiled. Out-of-tree handlers follow their
verb through the extension overlay: a verb the class table gates compiles
only into builds whose class carries it, while the ungated contract verbs
and any verb the class table does not know ride every build, keeping the
kit's drop-in promise; a handler whose verb the source scan cannot read is
conservatively kept.

## 6. Payload build pipeline (polyglot via decoupled build units)

The flow: **operator build request -> teamserver emits build params -> the
language's build unit compiles -> artifact + stager returned -> fingerprinted and
recorded.**

- **Two in-tree build units (.NET and Rust); polyglot by contract.**
  `DotNetBuildUnit` owns the .NET toolchain for the full-capability reference
  implant and stager; `RustBuildUnit` drives cargo for the Rust reach
  implant (Sec 12.2). The teamserver drives either through the **uniform
  build contract** and is coupled to each only by that contract, so a
  community build unit in Go, C/C++, or Nim can register and compile against
  the same contract with no in-language coupling (the `Language` enum keeps
  those slots, Sec 12.2). The Rust unit maps the target onto a cargo triple,
  bakes the same base64url profile into the staging copy's `src/baked.rs`,
  and refuses the stager class (not ported) and the dll format (the
  in-memory bundle is the .NET shape) with the fix named at parse time.
- **Artifacts ship in four form factors; the format is a build request
  knob.** `ArtifactFormat` rides the build contract beside the class and
  target, and the unit maps it onto one publish invocation:
  **`exe`** (the default) -- a self-contained single-file native executable,
  runtime bundled and compressed, the drop-and-run shape that needs nothing
  installed; **`exe-trimmed`** -- the same shape with IL trimming applied
  (the tree is source-generation clean, so the trim runs warning-free), a
  materially smaller transfer; **`aot`** -- native AOT compilation, a
  runtime-free native binary with the same deployment property a C or Go
  artifact has, the smallest and fastest-starting executable (measured on
  linux-x64: 39.8 MB single-file and 14.8 MB trimmed for the implant,
  10.3 MB AOT; 37.4/14.0/6.2 MB for the stager); and **`dll`** -- a
  framework-dependent net8.0 publish packed into one zip (the entry
  assembly, its dependencies, deps/runtimeconfig), the in-memory-loadable
  shape: a host with a .NET 8+ runtime loads it via `Assembly.Load` with no
  bytes on disk, and net8.0 is the oldest TFM every supported host runtime
  loads (pwsh 7.4 LTS through the teamserver's own .NET 10). The
  compatibility tiers the formats cover: a target with nothing installed
  runs the exe/trimmed/aot shapes and the script one-liners; a stock
  Windows with only .NET Framework answers through the PowerShell families;
  a target with a .NET 8+ host additionally takes the dll bundle in
  memory. An unmappable target fails the build with the supported set
  named rather than silently building for the build host.
- **Build params** include the implant class, artifact format, target OS/arch,
  transport profile, and beacon parameters (mode, sleep, jitter, kill date).
  They are produced at request time so each artifact is unique -- this is
  essential for OPSEC. No key material crosses the build contract (Sec 5.1).
- **The extension overlay is the implant-side out-of-tree seam.**
  `Build:ImplantExtensionDirectory` names a directory of handler sources; the
  unit copies its `.cs` files onto the per-build staging tree and generates the
  registrations that feed the implant registry's `additional` seam (Sec 5.3),
  so an operator drops a handler in as a source file and every implant-class
  build whose class permits the verb carries it -- no fork of the implant tree
  ([extending/tradecraft.md](extending/tradecraft.md)). A configured directory
  that is missing or yields no handler fails loudly, the same rule the
  server-side module loader applies; the stager tree is never overlaid.
- **The bake trims each build to the transport it dials.** The egress walk
  the profile bakes names URL shapes -- a schemed http(s) front carries the
  envelope POST cycle, a bare host:port the mTLS gRPC stream (Sec 8) -- and
  the unit compiles exactly the contact modules those shapes can dial: the
  other module's source files leave the staging copy whole, a generated
  selection replaces the checked-in both-modules stub, and a walk with no
  stream entry generates the rod.v1 message types without the gRPC client,
  dropping the Grpc.Net.Client reference with them. A web-front artifact
  therefore ships no gRPC client at all -- less surface, less size, one less
  fingerprint -- while a shape-crossing walk (a stream primary with web
  fallbacks) keeps both clients so no bake strands the artifact on a front
  it cannot dial. The stager tree is never trimmed: it fetches over plain
  HTTP and carries no contact clients.
- **The bake trims each build to the verbs it runs.** The class's verb set
  (Sec 5.2) is the server's authority for what an artifact may run, and the
  unit compiles exactly that set's handlers -- the whole-file trim Sec 5.3
  describes (the selection-seam rewrite, the reduced binary, the overlay's
  verbs riding every build). The stager tree is never trimmed: a stage-1
  loader carries no handlers.
- **Staging** is a separate output class with its own generation path: a
  stager-class build compiles the stage-1 loader, not the implant, and
  bakes in a fetch reference -- the stage-2 payload's id, sha256
  fingerprint, and format -- alongside the listener, kill date, and its
  own minted credential (a deployment secret, not key material). The
  loader runs with zero arguments and zero environment -- the bake is
  authoritative, and no flag or variable re-points or re-credentials a
  fielded artifact: the loader presents its token for the fetch
  (`GET /implants/stage2/{id}`, each served fetch spending one use -- the
  download gate is that credential's whole job), refuses bytes that do not
  hash to the baked fingerprint, runs the fetched artifact by its baked
  format, and hands nothing operational across the process boundary --
  the stage-2 spends its own baked token at its enroll and appears on the
  roster as a top-level implant. The run path follows the format: a
  **dll** stage-2 is hosted in the loader's own process (the bundle
  unpacks into memory, dependencies pre-load from bytes, the entry point
  invokes) -- no byte of the stage-2 on any filesystem, any OS; a
  **native AOT** stage-2 runs from an anonymous memfd on Linux
  (`memfd_create` + `execveat`, the documented kernel facilities -- the
  loader's process image becomes the stage-2), equally disk-free, because
  an AOT binary is a plain ELF with no self-reference; the single-file
  shapes keep the temp-file child, because a single-file bundle reads its
  own file to mount the runtime (observed: the bundle host cannot resolve
  the current executable from an anonymous fd). An AOT stager pairs only
  with executable stage-2 shapes -- a native host cannot load IL -- and
  the build refuses the pairing with the fix named. The .NET reference
  loader lives in `src/stager/dotnet/`; the fetch route is
  engagement-scoped by the token and by the listener it arrived on
  (Sec 8).
- **Every build mints the enrollment credential it bakes.** The token is
  minted at build time (single use by default, inside the artifact's kill
  window), baked into the profile's `token` key, and reported by id only --
  the operator never handles the plaintext, and the leak answer is revocation
  by id (`POST /engagements/{id}/stager-tokens/{tokenId}:revoke`, audited as
  `StagerTokenRevoked`). The manual mint stays for the rotation and re-entry
  flows (Sec 9), scoped per request to uses and window.
- **The transform seam is post-build.** Build-time artifact transformation
  -- where MSF put its encoders and payload encryption -- is a
  config-listed `IPayloadTransform` chain (each transform owns its key
  material and decode contract end to end; against modern EDR the classic
  encoders are legacy anyway). Each transform names itself, receives the
  built bytes plus the build context, and returns transformed bytes plus
  metadata; the chain runs after the build unit and before anything is
  recorded, so the stored fingerprint covers exactly the transformed bytes
  and the `PayloadBuilt` audit event names every applied transform. No
  in-tree transform ships: the empty chain is the seam, exactly like the
  capability placeholders, and transforms arrive through the explicit
  `Build:Transforms` list (the same loading shape as `Tradecraft:Modules`,
  [extending/tradecraft.md](extending/tradecraft.md)). A shellcode-form
  transform (turning a built artifact into position-independent bytes for
  the loaders that want them) is the seam's canonical occupant.
- **Artifact tracking.** Every generated artifact is fingerprinted and recorded
  (who, when, config) into the audit trail.

The build contract is the language-neutrality boundary; it is what lets the wire
protocol be "the product" while implants stay polyglot.

## 7. OPSEC -- a first-class design axis

OPSEC is a design axis, not a feature flag. The architecture bakes in:

- **Per-implant beacon profile, including the contact mode.** Two shapes ride
  the same stream contract: **stream** holds one long-lived connection (the
  interactive shape -- server-push tasking, no reconnect cost) and **poll**
  drains queued tasking, closes, and sleeps the interval with **jitter**
  (randomized delta) before the next contact -- the low-and-slow shape, since a
  persistent connection to a C2 endpoint is itself a loud signal. The mode is
  baked per implant at generation (`mode: stream|poll` on the build request),
  so one engagement can mix an interactive foothold with sleeping beacons.
- **Kill date.** A hard self-termination timestamp baked in per implant to limit
  exposure if lost. Enforced on both sides: the teamserver refuses a handshake
  past it (`HANDSHAKE_STATUS_KILL_DATE_EXPIRED`, no session opens), and the
  implant refuses to start past it and re-checks it each beacon cycle so a
  long-running implant self-terminates the moment the date passes.
- **Per-implant cryptographic identity.** Each implant generates its own ECDSA
  P-256 keypair at first run (effectively instantaneous, where RSA-2048 keygen
  costs ~100ms on-target and produces the larger leaf on the wire) and submits
  only the public half at enroll; the teamserver
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

> This section defines what the platform must **provide** for OPSEC. Concrete
> tradecraft beyond it is an operator-supplied capability module (Sec. 10).

## 8. Transports, listeners, and redirectors

- **Listeners come in two tiers, and only one of them is implant ingress.**
  The startup configuration names the shared tier -- the operator front the
  UI and API ride -- which carries **no implant ingress at all**: enrollment
  and the stage-2 fetch are refused on it outright. Implant-facing listeners
  are **engagement-scoped**: created through the operator API against exactly
  one engagement, persisted (in-memory with the process, Postgres when
  configured; a restart rebinds them with the same ids), and enforced at
  enrollment -- enroll and the stage-2 fetch resolve the listener a request
  arrived on, and anything but that engagement's own token is refused whole
  and unspent on that socket. Ports are unique across both tiers: the
  create-time bind check refuses a collision with a clear error before any
  socket opens. A payload build names its engagement's listener and the
  baked endpoint comes from the listener's record.
- Supported listener transports: **HTTP(S)**, **HTTPS** (the single-port
  shape: one TLS socket that requests no client certificate anywhere, so the
  handshake is indistinguishable from an ordinary website's -- enrollment
  rides the stager token and contacts ride the sealed envelope under the
  per-artifact key, both authenticated at the application layer), **mTLS**
  (one bind posture on every mTLS endpoint, startup-bound or created at
  runtime: ask for the client certificate, refuse one that does not chain to
  the CA in the handshake, never demand one there -- enrollment precedes the
  leaf, and the requirement lands where identity is consumed; Sec 9), **DNS**,
  **SMB** (named pipe), **raw TCP**, **QUIC** (the duplex socket
  transport for egress that passes UDP/443 but blocks TCP), and **DoH** (the
  DNS grammar over RFC 8484 HTTPS bodies -- the egress-restricted carrier
  behind a shape a restricted network already allows) are implemented.
  Transport choice is a profile/deployment concern; the protocol semantics
  are transport-independent. The web family additionally serves the
  WebSocket beacon (`GET /implants/beacon/stream`,
  extending/implants.md): the same live session the gRPC stream runs, over
  the envelope's own auth and frame grammar, so a web-fronted implant holds
  the interactive tier without a gRPC stack -- the reference implant's
  stream-mode web build dials it, and a poll-mode build keeps the envelope
  POST cycle. The http/https transports declare the beacon-stream carrier
  for issuance gating alongside mTLS and quic, so any of the four may be
  named as a build's beacon.
- **Plain HTTP is the loopback dev posture.** An `Http` listener entry binds a
  socket with no TLS and no client certificates, and every mapped route rides
  it: the operator API and UI in the clear, and contacts identified by the
  implant id in their handshake alone -- the DNS/SMB/TCP tradeoff, but on a
  socket anything with reach can present. The dev fallback binds loopback
  for exactly that reason, and a non-loopback plain-HTTP bind logs a startup
  warning naming this posture. The cleartext contact carrier is the
  envelope route (an ordinary HTTP/1.x POST): Kestrel serves cleartext
  HTTP/2 only on an HTTP/2-only endpoint, which cannot also serve the
  HTTP/1.x enrollment riding the same socket, so the gRPC stream is
  TLS-carried. Over TLS the gRPC stream's identity is the client
  certificate, and only the `mtls` transport requests one; the
  certificate-less `https` socket carries contacts on the envelope route
  instead, where the per-artifact key sealing the body is the identity.
  A deployment that fronts the teamserver with its own TLS-terminating edge
  accepts the split knowingly; without such an edge, real binds are `Https`
  or `Mtls`.
- **DNS is the egress-restricted contact transport, and it carries its own
  enrollment.** A DNS listener entry
  answers TXT queries under its public endpoint (the zone) over UDP: a poll
  (`p.<b32(implant-id)>.<zone>`) refreshes an implant's presence and returns
  the next queued tasking as a signed `TaskRequest` in TXT; result chunks
  (`r.<b32(task-id)>.<s|f>.<seq>.<t|m>.<b32(chunk)>.<b32(implant-id)>.<zone>`)
  report outcomes, reassembled server-side. The wire grammar is the DNS
  contact contract ([extending/implants.md](extending/implants.md)); the
  responses ride EDNS0 so a signed TaskRequest fits the datagram, and a task
  too large for the budget is not claimed over DNS: it stays queued for a
  stream transport. **Delivery confirmation:** a lost chunk drops a
  report's reassembly whole, so the sender probes
  `n.<b32(task)>.<b32(sha128 of the plaintext)>.<b32(implant)>` after
  chunking and keeps the frame pending until the `y` answer -- the
  re-send rides later cycles idempotently (first-wins recording), the
  reliability shape a datagram carrier needs without a per-chunk ack
  protocol.
  **The record-type cover:** an A query anywhere under
  the zone answers one A record (the bind's own host, else a deterministic
  per-name address in 198.18.0.0/15) -- a zone answering TXT for random
  labels but NXDOMAIN for every A query would itself be the fingerprint,
  and an ordinary v4 zone answers its A records. AAAA and other types keep
  the NXDOMAIN a v4-only zone gives; out-of-zone queries stay REFUSED.
  **The store-and-forward channels ride the polls (Sec 10.3):** the poll
  answer carries a kind byte naming its frame -- a queued task, or the
  parked operator input the degraded hub drained (every frame the drain
  collected, length-prefixed, so a typing burst and its eof cross
  together) -- and the channel's output chunks up as `c.` queries,
  reassembled and ingested through the shared composition the beacon
  stream uses. The DNS carrier serves the degraded discipline: the
  slowest wire that carries the interactive verbs, carried anyway -- the
  operator's pick, at the query-rate cadence.
  **Enrollment over DNS (the full-independence step for a DNS-only
  target):** the enroll body uploads as chunked TXT queries
  (`e.<b32(stream)>.<seq>.<t|m>.<b32(chunk)>.<zone>`) keyed by a
  client-chosen stream id -- sealed under the baked per-artifact key when
  the artifact carries one, so the token secret never crosses the resolver
  chain in the clear -- and the assembled `EnrollResponse` chunks back down
  as token-keyed answers (`a.<b32(token)>.<seq>.<zone>`). The terminal
  chunk drives the shared `ScopedEnrollment` flow scoped by the answering
  listener's engagement, and an accepted DNS enrollment opens the session
  itself (no handshake exists to open it): the polls that follow refresh
  what it wrote. A DNS-only target runs its whole lifecycle on this one
  carrier -- the lightweight implant. **The contact carriage seals for a
  keyed artifact:** a build that baked an envelope key polls `k.`-named
  (the key id in the name, so the server resolves the seal statelessly and
  a restart re-derives it on the next poll), the poll answer rides as a
  raw R1 AES-GCM body under a DNS-purpose tag, and results and channel
  outputs seal whole before chunking under their own tags -- the resolver
  chain reads no frame bytes in the clear. The seal is confidentiality,
  not authority: the signature still gates execution, and the server
  refuses the plaintext downgrade for a key-bound implant (an empty
  `p.` answer, a dropped plaintext reassembly) so tasking is never handed
  down in the clear to an artifact known to carry a key. The transport's tradeoff is deliberate and documented: no
  handshake and no mTLS ride DNS, an implant is identified by its id alone
  on the contact path (the sealed enroll exchange authenticates by key
  possession). Downstream tasking keeps the full Sec 9 posture -- the
  TaskRequest carries the same command signature, and a DNS-delivered task
  verifies exactly like a stream-delivered one. The degraded-mode contract
  rides the session record: every contact stamps the carrier it rode (web,
  grpc, quic, dns, pipe), the roster badges a dns-carried session as
  degraded, and a task the carrier cannot serve stays queued with its
  visible why -- capability is a property of the carrier at runtime, not of
  the artifact.
- An implant is always the **connection initiator** (reverse connection). The
  teamserver and redirectors never dial targets.
- **Listener and public endpoint are decoupled, and the endpoint is repointable
  at runtime.** A redirector fronts the listener; a burned redirector is replaced
  without touching the backend by repointing the listener's public endpoint
  (`POST /engagements/{engagementId}/listeners/{id}:repoint`). The Kestrel
  bind is untouched; the old
  endpoint simply no longer resolves to any listener, which severs it. This
  decoupling is what makes disposable infrastructure practical. The in-tree
  reference redirector -- an opaque L4 TCP forwarder published as a Native AOT
  binary -- ships this rotation end to end; see
  the deploy/rotate runbook ([operations/redirectors.md](operations/redirectors.md)).
- **Fallback egress endpoints are baked in and walked client-side.** A build
  may carry an ordered endpoint list -- the primary plus fallbacks (Sec 5.1) --
  and the implant walks it on failed contacts: enroll retries and beacon
  cycles that never reach a handshake advance to the next entry, wrapping to
  the primary so a front that returns is picked up again. The walk is entirely
  client-side -- the Tier 0 frame grammar is untouched -- and it never touches
  identity: the implant presents the same enrolled leaf whichever entry it
  lands on, so its listener-side identity is unchanged and a listener cannot
  tell which front an implant arrived through. This is the implant-side answer
  to a burned front, complementing the server-side repoint above: repoint
  swaps the front for future builds, the baked walk keeps the already-deployed
  implant talking. Verified end to end by the implant subprocess tests (a dead
  primary, a live fallback, one identity).
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
- **The plain-HTTP envelope contact is the implant-reach transport and the
  reference implant's web contact.** The same rod.v1 frames the gRPC stream
  carries, marshaled as varint-length-delimited sequences in ordinary
  HTTP(S) request/response bodies -- one POST (`/implants/beacon`) is one
  poll contact: the request body carries the handshake first plus any
  results, exfil chunks, and staged pulls; the response carries the handshake
  response, the staged chunk runs answering the request's demands, and
  queued tasking while a 4 MiB dispatch budget lasts (what does not fit is
  requeued for the next contact). It changes the framing, not the protocol
  semantics: the route is mapped on every listener and the frame paths are
  the beacon compositions every transport shares -- an mTLS front serves it
  alongside the gRPC stream on the same socket, so a deployment never needs a
  dedicated envelope-only entry (the retired `HttpsEnvelope` listener name
  said nothing the transport list did not; its stored definitions migrate to
  `mtls` on restore). Authentication is at the application
  layer, under the per-artifact key the build mints (Sec 9) -- the mainstream
  HTTP(S) C2 shape, and the reason the `http`/`https` listeners are
  single-port and request no TLS client certificate anywhere: a
  CertificateRequest is itself a fingerprint an IDS reads. The default body
  is that key sealing `counter || frames` as AES-256-GCM, and the response
  seals the same way, so the cleartext `http` posture carries confidential
  content, not just authenticated content; the counter is strictly increasing
  per attempt and the route refuses one at or below its floor (a replayed
  body turns away without a session or a touch), and an enrollment that
  redeemed a build-minted token is bound to that build's key, so its contacts
  cannot downgrade to the plaintext frame. The plaintext framed body is the
  lab-debug toggle's shape (contact protection off at build), served only
  for implants no key was ever bound to; a client certificate still resolves
  first where an mTLS front presented one. The reference .NET implant picks
  its contact client by the baked beacon URL's shape: an `http(s)://` URL
  runs the envelope POST cycle on that port -- the mainstream single-port web
  posture, the build's derived default for `Http`/`Https` fronts -- while a
  bare host:port dials the mTLS gRPC stream (what a named mTLS beacon
  listener bakes). The pick is also compile-time: the bake trims each build
  to the contact modules its walk can dial (Sec 6, the transport trim).
  A build against a web front therefore
  needs no beacon split; naming the mTLS listener as the beacon stays the
  hardened option for an engagement that wants the interactive stream.
  Dropping the gRPC/HTTP-2 requirement is the point -- Tier 0 is reachable
  from any language with an HTTP client and a protobuf codec
  ([extending/implants.md](extending/implants.md)). A channel task claims
  over the envelope under the store-and-forward discipline every poll
  artifact advertises (Sec 10.3; only the DNS datagram poll refuses one),
  and an artifact's exfil chunk run must
  complete within one request body -- the poll-transport bounds, documented
  with the wire grammar.
- **The stream listeners: raw TCP answers weak-inspection egress, SMB the
  internal segment.** Raw TCP is the front for environments that permit
  arbitrary outbound sockets but put nothing between them and the internet
  -- no HTTP inspection to blend with, no TLS requirement to satisfy -- so
  a plain framed socket is the cheapest adequate shape. SMB serves Windows
  segments with no egress at all (the pipe is the shape such a segment
  still allows): the reach is internal, and the deployment that serves it
  bridges the segment to a teamserver -- not with a redirector (a
  redirector is OUR infrastructure, deployed on our own servers to rotate
  IPs and machines while the teamserver stays fixed, never placed inside a
  target network), but with the pivot posture: a parent implant holding
  the outward session derives children inside the segment
  (`lateral.move`, Sec 5.2), and the children's tasking rides fronted on
  the parent's stream -- implant-to-implant reach that goes deeper into
  the environment as far as the parent chain goes, in both dial
  directions. Both listeners carry
  the same rod.v1 frames the envelope carries -- one self-delimited message
  per direction (a varint byte length, then the envelope's delimited frame
  sequence), because a raw stream lacks the request boundary an HTTP body
  provides for free -- through the shared frame paths (`BeaconIngest`,
  `BeaconTasking`): a result captured over a pipe or socket is
  indistinguishable in core state, the audit trail, and the live bus from one
  captured over the gRPC stream. One connection is one poll contact, the
  envelope's cadence on the envelope's budget, and channel tasks claim under
  the store-and-forward discipline every poll artifact advertises (Sec 10.3)
  -- operator input parks server-side and rides the next connection's
  response.
  **Enrollment over the stream contact (the full-independence step QUIC
  first took):** the opening message may carry a kind-bearing
  `EnrollRequest` frame ahead of its handshake -- the enroll body the web
  route carries, promoted into the rod.v1 frame grammar -- answered by an
  `EnrollResponse` frame as its own message, the ordinary handshake
  following on the same connection. The shared `ScopedEnrollment` flow does
  the work, scoped by the listener's own engagement with the web route's
  refusal rules and audit arc, so a no-egress segment can enroll its first
  implant over the pipe or socket it already reaches. The reference
  implant's socket module carries it end to end: the parser bakes the
  transport's own dial (`tcp://host:port`, the pipe path in URL form
  `smb://host/pipe/name`), the enroll exchange runs the frame grammar on
  the dial, and the poll cycles ride the envelope's own request/response
  shape over the message framing -- one connection is one contact, the
  interactive verbs on the shared store-and-forward carriage every poll
  client runs. **The stream mode holds the connection instead:** the
  handshake's live advertisement switches the server to the shared session
  runner (the gRPC stream's, the WebSocket beacon's, and the QUIC
  session's own), so a stream-mode bake over a pipe or socket gets
  server-push tasking and live channels on the held connection -- the same
  dial, the mode picking the client that dials it, and an older teamserver
  serving the connection as an ordinary poll contact when it does not
  know the advertisement. The sealed body rides by default: the enroll
  exchange and every contact message are AES-256-GCM under the baked
  per-artifact key (counter-floored on the contact side, purpose-tagged
  on both), the same application-layer seal the cleartext http posture
  carries -- a bare socket or pipe leaks no frame bytes either, and the
  token secret never crosses in the clear.
  The identity posture is the certificate-less one: no client certificate rides a
  pipe or a raw socket, so the implant is identified by the id in its
  handshake -- the DNS tradeoff extended to a handshake-capable transport,
  with the enrolled, kill-date, and retired gates applying in full (on a
  Windows host the SMB session layer authenticates the peer before the pipe
  is reachable; a raw socket rides whatever segmentation protects it).
  Dispatched tasking keeps the full Sec 9 posture: the TaskRequest carries
  the same command signature, and a stream-delivered task verifies exactly
  like any other. Each entry is a hosted service owning its pipe or socket,
  registered into the listener registry the same bind-then-register way every
  transport follows (`StreamBeaconBridge` is the transport-blind contact
  flow both share); the wire grammar is the stream contact contract
  ([extending/implants.md](extending/implants.md)), pinned end to end by the
  stream-enroll acceptance tests (a from-scratch TCP client drives
  enroll-then-contact on one connection, and a foreign engagement's token is
  refused whole and unspent).
- **QUIC is the duplex socket transport: the interactive tier over a UDP
  egress.** An engagement whose egress passes UDP/443 (where HTTP/3-era
  traffic lives) but blocks TCP has no shape among the stream listeners, so
  the socket-owning family gained its duplex variant: a `quic` listener owns
  a UDP socket, terminates TLS 1.3 with the CA-issued server leaf every TLS
  front shares, and requests no client certificate anywhere -- the web
  posture's fingerprint rule, which QUIC needs anyway (it cannot ride
  cleartext). One connection is one live session (not the family's
  one-connection-one-poll): the implant opens a single bidirectional stream,
  speaks the pipe/TCP self-delimited message framing over it, and the shared
  `BeaconSessionRunner` holds the session -- server-push tasking the moment
  it is queued, live channels for the streaming verbs. That duplex truth is
  declared where it is read: the transport serves the native `beacon-stream`
  carrier, so a quic listener is beacon-nameable, the build bakes its dial
  as the transport's own scheme (`quic://host:port` -- the URL shape picks
  the artifact's contact client, and the bake-time trim compiles the QUIC
  module for exactly that shape). Either mode bakes: stream holds the
  session, poll ends each cycle on the client's idle window at the baked
  cadence -- the operator's pick -- and a poll run carries the interactive
  verbs store-and-forward on its cycles, the same shared discipline
  (PollChannels) every poll client runs, whatever its wire. The identity is the certificate-less
  family posture -- the implant id in the handshake inside the encrypted
  transport, with the enrolled, kill-date, and retired gates in full; the
  TLS layer authenticates the server to the implant (chain-to-CA pinned),
  not the implant to the server. The wire grammar is the QUIC stream
  contract ([extending/implants.md](extending/implants.md)); the transport
  needs a host QUIC stack (libmsquic on Linux), and the bind refuses with
  the named cause when the host carries none.
  **Enrollment over QUIC (the designed full-independence step):** the
  certificate-less posture above is what makes the carriage clean -- no
  TLS change, no second connection. The opening stream's first exchange
  may be an enroll instead of a handshake: a length-prefixed
  `EnrollRequest` frame (the enroll body the web route carries, promoted
  from JSON into the rod.v1 frame grammar -- token secret, class, host
  facts, the implant's public key, parent, kill date) answered by an
  `EnrollResponse` frame (status, identity, leaf and chain, the
  per-artifact contact key) and followed immediately by the ordinary
  handshake on the same stream -- one connection carries
  enroll-then-session; every reconnect carries the handshake alone. The
  server reuses the enrollment flow the web route drives (one shared,
  engagement-scoped implementation), scoped by the listener's own
  engagement (the ingress the HTTP route resolves from the local port,
  the QUIC listener knows directly), with the web route's refusal rules
  and audit arc. The build story is the same coin: a quic listener is
  enroll-nameable, the parser bakes its dial, and the implant enrolls
  over QUIC when the baked enroll endpoint is quic-schemed -- the
  web-enroll + QUIC-session pairing inverts into QUIC-only independence.
  Both halves are pinned by the QUIC acceptance tests: the from-scratch
  client drives the whole exchange on one connection, and the reference
  implant's subprocess test enrolls, handshakes, and tasks over the one
  UDP socket with no HTTP shape dialed at all.
- **The shellcatch transport holds caught reverse shells.** Where the TCP
  listener serves contacts -- one connection, one rod.v1 exchange,
  closed -- the shellcatch listener (`"shellcatch"`) accepts connections
  that speak no Rod protocol at all: the peer is whatever reverse-shell
  one-liner the operator ran on the target (nc, a bash `/dev/tcp` pipe, a
  perl or python snippet), it never enrolls and carries no identity, so
  the catch is scoped the only way an anonymous arrival can be -- by the
  engagement-bound listener it landed on. The connection is held (not
  one-exchange-per-connection): output flows onto a bounded,
  sequence-coursed log the operator console long-polls by cursor, the
  first output chunks are fingerprinted server-side into an OS/shell
  guess that never downgrades, operator input rides the audited input
  route down the socket, and the ending is attributed to who ended it
  (Lost when the peer goes away, Closed when an operator asks). The
  shell session registry is the anonymous-arrival sibling of the implant
  session registry, engagement-scoped the same way by construction. The
  shell leaves the hub the moment its socket dies, before the durable
  marking, so no route resolves a dead socket. An upgrade render is
  advisory: it mints the engagement a single-use stager token and returns
  paste-ready one-liners against the engagement's web listener, rendered
  for the payload's format -- the disk families (curl/wget/PowerShell
  fetch-and-run) for every shape, the Linux in-memory family (python
  stages the bytes in a memfd and execs through /proc/self/fd) for the
  native AOT payload, and the pwsh cradle (in-memory zip unpack,
  dependency pre-load, `Assembly.Load`, entry invoke) for the dll bundle
  -- the paste is the operator's
  action through the input route, not a server-side write into the
  session. The standalone launchers endpoint renders the same one-liners
  without a caught shell -- the cut-ahead delivery surface, where the
  credential's use budget and lifetime are the operator's choice (the
  shell upgrade always mints single-use for thirty minutes: one paste,
  one download). Every render the endpoint cuts is kept as a launcher
  row -- url, credential, policy, provenance -- so the operator can
  re-copy a command at any time, watch the credential's budget, revoke
  it the moment it leaks, and delete the row when it is spent; the rows
  are engagement-scoped operator state, durable with the store.
  Shellcatch serves no contact carrier -- nothing here is
  implant ingress, and a build may never name it as a beacon. The
  exposure is inherent and named: a shellcatch port accepts whoever
  reaches it (the one-liner carries no secret); the mitigations are a
  fronting redirector's source allow-list, a non-default port, and a
  short-lived listener -- the same infrastructure discipline every
  ingress follows.
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

- **Identity.** Operator identities (credentials and API tokens) verified at
  login and per request; implant identities bound to their engagement by the
  transport they contact over -- a client certificate on the mTLS listener,
  the per-artifact contact key on the web transports (below). API tokens are
  bearer credentials minted per operator
  through the operator API (shown once, stored as a digest), honored alongside
  cookie sessions through a front scheme that authenticates by what the
  request presents, and revocable by their own route with the same
  immediate-effect, no-restart shape. A token is independent of the password:
  each credential revokes through its own route, so rotating one never
  silently invalidates the other.
- **mTLS.** The mTLS transport is mutually authenticated; an implant's certificate
  binds `(implant_id, engagement_id)` through labeled URI SAN entries
  (`spiffe://rod/implant/<id>`, `spiffe://rod/engagement/<id>`) under a
  conventional service-certificate profile -- a fixed non-identifying subject,
  standard end-entity extensions, a random serial. Neither id rides the
  subject DN and no custom OID exists: a GUID common name with an unknown
  extension is itself a toolchain fingerprint, on the wire and in host
  forensics, while URI-SAN identity is the shape legitimate service
  certificates use. Every mTLS endpoint carries the one ask-and-validate
  bind posture Sec 8 defines, however it came to exist. Possession is
  enforced where identity is consumed: over
  TLS the beacon resolves the implant from the certificate alone, so a
  certificate-less connection completes TLS, reaches only what every front
  serves (enrollment answers on its token), and opens no session.
- **Contact keys.** The web transports authenticate implants at the
  application layer, not the TLS layer -- a TLS `CertificateRequest` is
  itself a fingerprint (an ordinary website never asks the visitor for one),
  which is why the `http`/`https` listeners never send one. Each build mints
  a per-artifact AES-256 key (the same envelope-key shape the opt-in AesGcm
  enroll body uses), bakes it into the artifact, and records it beside the
  stored payload: deleting the payload deletes the key, and that artifact's
  sealed bodies stop being decodable -- enroll included. Contact protection
  defaults on at build (its own Advanced knob beside the enroll-body
  envelope; off is the lab-debug plaintext frame), and every envelope
  contact body is then AES-256-GCM under that key covering a strictly
  increasing counter -- possession of the key is the authentication, the
  GCM tag binds the counter to the frames, the server keeps a per-implant
  floor and refuses a counter at or below it (a replayed body never opens a
  session or touches presence), and the response seals the same way, so the
  cleartext `http` posture carries confidential content, not just
  authenticated content -- the Cobalt Strike metadata model. Each direction
  binds to its own purpose tag, so one direction's ciphertext cannot be
  reflected as the other's. When the redeemed enroll token was the one the
  build minted, the enrollment binds the implant to that build's key: a
  plaintext body from a bound implant is refused whole (no downgrade to the
  lab shape) and another artifact's key does not impersonate it. The counter
  burns per POST attempt, not per delivery, so the batch retransmission
  after a lost response never trips the floor. The floor and the binding are
  process-local like the sessions they protect; the key itself lives exactly
  as long as its payload record.
- **Production implant CA.** The teamserver consumes an externally provisioned
  engagement CA; it does not generate the production CA. When
  `Pki:CaCertificatePath` and `Pki:CaPrivateKeyPath` are configured,
  `FileBackedCertificateAuthority` loads the CA certificate and its RSA private
  key (optionally passphrase-encrypted) from disk and signs the implant's
  ECDSA leaf with the same leaf construction the dev authority uses -- only the
  issuer changes (an RSA CA signing EC leaves is the standard cross-algorithm
  PKI shape; the CA's signing key and the leaf's key are independent).
  Absent the config the dev self-signed authority stays. The authority is built
  eagerly at DI registration, so a missing file, an unparseable PEM, a non-RSA
  key, or a key/cert mismatch fails the host at startup, not the first
  enrollment; RSA is the only supported CA key type, the server-held signing
  key. Rotation is operational (replace the files and restart). Rejected:
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
  Deployment
  order matters: this implant rejects unsigned tasking, so the teamserver
  signs -- upgrade it before deploying implants built from this contract.
  Rejected: a dedicated task-signing key pair (a second teamserver-held
  secret to provision, rotate, and bake into artifacts, for no isolation
  gain while the CA key is already the server's signing identity); signing
  the serialized `TaskRequest` bytes (couples verification to one protobuf
  runtime's serialization behavior).
- **Tasking replay nonces.** Command signing binds tasking to its implant,
  but a captured signed frame used to verify on replay to the same implant.
  The arm is negotiated at handshake: an implant that sets
  `replay_nonces` on its `HandshakeRequest` gets every dispatched
  `TaskRequest` stamped with `task_nonce` -- a per-implant monotonic counter,
  increasing across dispatches, sessions, and transports -- and the tasking
  signature covers the five-element tuple (the original four plus the nonce's
  decimal string in the same length-prefixed form), so the nonce cannot be
  altered any more than the arguments can. The negotiation is sticky on the
  implant: once advertised, later handshakes cannot downgrade tasking back to
  the nonce-less shape. The implant tracks the highest nonce it accepted --
  for its whole run, not per connection -- and refuses any at or below it,
  reporting the refusal as the task's `Failed` result so a replayed frame
  surfaces on the task instead of silently re-executing; once negotiated, a
  nonce-less task is refused too. An implant that never advertises keeps the
  original four-element tuple byte-for-byte: the addition is negotiated, never
  imposed (the evolution rules, extending/implants.md). The nonce floor lives
  behind the task repository: the in-memory adapter's counter is per-process
  (a restarted teamserver restarts the count, which the signing posture
  already tolerates -- the threat is an untrusted transport hop, not a
  compromised teamserver, and the task queue is equally per-process in the
  in-memory adapters), while the durable adapter reserves each nonce with an
  atomic upsert-and-return on a persisted floor row, so a restarted
  teamserver's next dispatch for a negotiating implant continues past the
  pre-restart count. The reference implant advertises the arm, and the
  conformance harness's hostile probe replays a genuinely signed control frame
  to pin the refusal.
- **Sealing** _(future, deferred)_. End-to-end protection of task payloads so
  untrusted redirectors cannot read or alter them. Deferred because the
  concrete adversary is absent today: the reference redirector is an opaque L4
  splice, the beacon channel is mTLS terminated at the teamserver, so an
  untrusted hop sees only ciphertext -- and mainstream platforms ship nothing
  equivalent. Building it would put mandatory cryptography on every implant's
  task path (against the implant contract's evolution rules). If it is ever
  built -- for TLS-terminating edges such as domain fronting -- it must be
  handshake-negotiated with a plaintext fallback, so Tier 0 implants keep
  interoperating (see [extending/implants.md](extending/implants.md)).
- **Per-implant identity and rotation.** Each implant owns a keypair it
  generated itself; the server binds it with a CA-signed leaf at enroll and
  never sees the private half (Sec 7, Sec 9). Identity key material stays out
  of artifacts; the one symmetric key a build bakes is the per-artifact
  contact/envelope key above -- it seals wire bodies, not identity, and it
  is per-artifact and revocable with the payload it is recorded beside.
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
  fails from then on. It ends the credential's live cookie sessions too: a
  cookie is self-contained, so every authenticated request revalidates the
  session stamp its login baked into the principal -- a digest of the stored
  verifier -- against the verifier the store holds now. A revoked credential
  (no verifier) or a re-provisioned one (a new password is a new generation)
  fails the comparison at the very request that presented the cookie; the
  stamp is a digest, so the cookie carries nothing usable. Re-provisioning
  the operator with a new password restores login without resurrecting the
  revoked generation's sessions. Revocation is not recorded in the audit
  trail: the trail is engagement-scoped and an operator credential is global
  state, so it has no engagement to live in.
- **Kill-date enforcement.** Both sides refuse past the baked date, the
  discipline Sec 5.1 defines: the teamserver at handshake, the implant at
  startup and each beacon cycle.
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
  carries nothing for it (extending/implants.md, evolution rule 4). Warn-only
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
-- the proto field, core state, the transport DTO, the dispatch contract, and
the implant's dispatch entrypoint. The verb is the typed discriminator; the string is
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

`file.push` is the first shipped arm. A push too large for the arguments string
(the inline shape caps at 1 MiB) is issued as staged content: the sha256 of the
bytes is appended to the arguments -- so the payload's integrity lands inside
the signed tasking tuple (Sec 9) exactly as the inline shape's does -- the bytes
themselves are staged as a task-bound artifact (Sec 11), and the TaskRequest
carries the staged size as a typed field. The implant demands the payload with
a `StagedPull` and the stream answers with a chunked run, the mirror of exfil
chunking in the other direction. Nothing bulk flows downstream unasked: an
implant that never implemented the arm ignores the unknown field and fails the
verb on its own grammar, so the addition costs a Tier 0 implant nothing
(extending/implants.md).

### 10.1 Capability categories

| Category | Example verbs | Summary |
|----------|---------------|---------|
| **core** | `shell.exec`, `shell.interact`, `file.push`, `file.pull`, `fs.list`, `proc.kill`, `beacon.sleep` | The mandatory-to-useful baseline: command execution (one-shot and interactive), file transfer in both directions, directory listing, process termination, and retiming the beacon's own cadence. `file.pull` returns small files inline and streams large ones into the artifact store; `file.push` lands an operator-supplied payload on disk -- inline base64 up to 1 MiB per task, larger uploads staged and streamed down in chunks on the implant's demand (Sec 10's typed arm); `fs.list` lists a directory for the file browser; `proc.kill` ends one process by pid, carrying a `kills-process` OPSEC flag for the picker to badge; `beacon.sleep` retunes the live contact cadence (sleep and jitter) from the next cycle. |
| **recon** | `recon.portscan`, `recon.hostenum`, `recon.service`, `recon.ps` | Target and network reconnaissance. `recon.ps` lists the local host's live processes -- pid, ppid, user, image. |
| **lateral** | `lateral.move`, `lateral.token`, `lateral.exec_remote` | Lateral movement within authorized scope. |
| **persist** | `persist.install`, `persist.remove`, `persist.list` | Persistence mechanisms. |
| **collect** | `collect.cred`, `collect.keylog`, `collect.screenshot` | Credential, screen, and input collection. Operator file transfer is a core verb (`file.push`/`file.pull`), not collection. `collect.screenshot` captures the display as a PNG artifact joined to its task (Sec 11). |
| **exfil** | `exfil.push`, `exfil.stage` | Exfiltration over the C2 channel. |
| **tunnel** | `tunnel.forward`, `tunnel.socks` | Network tunneling through an implant (Sec 14, core operations): `tunnel.forward` bridges a live channel to a TCP connection the implant opens from its own vantage, so operator traffic reaches hosts beyond it; `tunnel.socks` is the multiplexed arm -- the channel's byte stream is a connection-multiplexed grammar, so every proxied connection rides the one task and each destination arrives per connection. Both run as live channels (Sec 10.3); the pivot class carries exactly this set (Sec 5.2). A relay bind exposes either channel as a teamserver-side listener -- the raw one-connection bridge for `tunnel.forward`, a SOCKS5 listener for `tunnel.socks` -- so unmodified operator tooling rides the tunnel without per-byte API posts (Sec 10.3). |
| **evasion** | `evasion.avoid`, `evasion.unload` *(contract only)* | Detection-evasion hooks. Contract and dispatch only. |
| **exploit** | `exploit.invoke`, `exploit.module` *(contract only)* | PoC/exploit integration point. Contract and dispatch only. |

The recon verbs are registered through the tradecraft layer as first-class
descriptors (`Rod.Tradecraft.Recon.ReconCapabilities`, category `Recon`); their
concrete behavior runs on the reference implants and is captured as task output
over the beacon stream (Sec 10.3). Recon is a long-haul activity, so the four
verbs are gated to Stage-2 at task issuance -- a non-Stage-2 class is refused
before the task is queued (Sec 5.2).

The process verbs close the same operational gap from both ends
(`Rod.Tradecraft.Core.CoreCapabilities` carries `proc.kill`; `recon.ps` rides
the recon set above): the reference implant lists live processes over the
documented OS process APIs -- the `/proc` filesystem on Linux, the Win32
toolhelp snapshot plus process-token owner query on Windows -- and terminates
one by pid through the standard kill path, so an operator can see what runs on
a target and end one of it, the pair every mainstream client carries. Both are
Stage-2 gated like their recon kin.

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
as in-repo reference handlers: `lateral.token` enumerates the current process's Windows access-token
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
mechanisms: the Windows
`Run` registry key, scheduled tasks, and services, plus Linux cron and
systemd user units -- the documented persistence surfaces every system
administrator and offensive-security curriculum covers. Install, list, and
remove round-trip against these surfaces. Novel or stealth persistence
techniques arrive as operator-supplied modules.

The collection and exfiltration verbs are registered the same way
(`Rod.Tradecraft.Collect.CollectCapabilities`, category `Collect`, and
`Rod.Tradecraft.Exfil.ExfilCapabilities`, category `Exfil`): `collect.cred`
carries a `reads-credential` attribute, `collect.keylog` carries
`reads-input` and `persists` (it installs a resident input-capture hook), and
`collect.screenshot` carries `reads-screen` (it captures what the target's
display shows);
`exfil.push` carries a `touches-network` attribute (it transfers over the C2
channel), and `exfil.stage` is a read that carries no such flag, like
`persist.list` and the host-local `recon.hostenum` (it stages already-collected
data on the teamserver). Like recon, lateral, and persist they are gated to
Stage-2 at task issuance (Sec 5.2). Collection and exfiltration are long-haul
activities. The reference implant
ships in-repo handlers for the core file verbs (`file.pull` reads the target's
filesystem -- small files return inline, large ones chunk into the exfil
channel -- and `file.push` lands an operator-supplied payload on disk),
`collect.cred` (standard credential-store *listings* -- SSH key presence with
fingerprints, AWS profile names, Windows saved-credential names via
`cmdkey /list` -- without dumping secret material), `collect.screenshot`
(the display read over the standard desktop-capture APIs -- GDI `BitBlt` on
Windows, `XGetImage` on X11 -- PNG-encoded in-process and chunked into the
exfil channel, so the capture lands as an artifact joined to its task with no
new server-side path; a headless target refuses cleanly naming the missing
display), and `exfil.push` /
`exfil.stage` (data transferred over the C2 channel into engagement-scoped
artifact storage, Sec 11). Two collection surfaces ride the Rust reference
implant's Windows builds (the .NET reference keeps them contract-only):
LSASS memory dumping (`collect.minidump`, the dbghelp minidump path) and
`collect.keylog` input capture. On the .NET implant -- and on every platform
the Rust build does not gate in -- each runs only when an operator supplies a
module for the verb.

The tunnel verbs are registered the same way
(`Rod.Tradecraft.Tunnel.TunnelCapabilities`, category `Tunnel`):
`tunnel.forward` and `tunnel.socks` each carry a `touches-network` attribute,
since each opens network connections from the target. Unlike the categories
above they are gated to two classes: Stage-2 (tunneling is a core operation,
Sec 14) and Pivot (Sec 5.2 -- the tunneling class). Their tasks run as live
channels (Sec 10.3) -- the channel carries the tunnel's bytes both ways -- so
the poll transports never claim them, and the reference implant ships both
channel handlers: `tunnel.forward` connects to the `<host> <port>` named in
the arguments from the implant's own vantage and bridges the channel to the
socket until the peer closes, with the relay summary as the task's final
output; `tunnel.socks` takes no arguments at all -- its channel's byte stream
is the proxy's own connection-multiplexed grammar (one `open` packet per
connection naming that connection's destination, `data` packets under each
connection id, `close` to end one, `opened` to answer a dial), the
verb-local escape hatch Sec 10 licenses, so every proxied connection rides
the one task and the implant dials each destination from its own vantage
until the operator's eof ends the proxy, with the destinations dialed and
the bytes moved as the task's final output. The traffic's attribution is end
to end: every byte crossed the channel the signed TaskRequest opened, the
input posts land as `ChannelInput` audit events, and the transcript plus
summary is the operator's record. Input posts are the manual shape: a
**relay bind** (`POST /engagements/{id}/tasks/{taskId}/relay`, tunnel-only,
loopback by default and an ephemeral port unless the operator names one)
starts a teamserver-side listener bridged onto the dispatched channel -- the
raw relay for `tunnel.forward` (one connection: the channel is one TCP
connection on the implant's side) or a SOCKS5 listener for `tunnel.socks`
(no auth, CONNECT only -- the surface a browser or proxychains speaks),
where each accepted SOCKS connection joins the channel under its id and
each CONNECT's destination travels as an open packet. Either listener's
reads enter the same channel-input enqueue the route uses, and the
channel's output chunks are handed back raw, before the transcript's UTF-8
decode, so the tool's bytes are the channel's bytes. The bind dies with the
task: the final result, the stream ending, or the operator unbinding each
close it and write the `RelayClosed` event with the relayed tallies, next
to the `RelayBound` event the bind wrote. The relayed traffic itself keeps
the channel's no-per-chunk discipline -- it rides the task's transcript,
so an unmodified tool reaches third hosts with zero operator API calls per
byte and the whole flow stays attributed to the task.

The evasion verbs are registered the same way
(`Rod.Tradecraft.Evasion.EvasionCapabilities`, category `Evasion`): both
`evasion.avoid` and `evasion.unload` carry a `modifies-defenses` attribute,
since each alters the target's defensive or monitoring posture (Sec 7). Unlike
the recon, lateral, persist, collect, and exfil verbs they are **not** gated to a
class in `ImplantClassCapabilities` (Sec 5.2): evasion is contract and dispatch
only -- which class an evasion module runs on is decided when an operator deploys
the out-of-tree module, not by a baked-in class rule. Their concrete behavior is
operator-supplied tradecraft (Sec 10.2, Sec 13):
the core ships no bypass techniques or weaponized code, so each verb runs only
when an operator supplies a module for it.

The exploit verbs are registered the same way
(`Rod.Tradecraft.Exploit.ExploitCapabilities`, category `Exploit`): both
`exploit.invoke` and `exploit.module` carry an `exploits-target` attribute,
since each actively attacks a target to gain or widen access (Sec 7). Like the
evasion verbs they are **not** gated to a class in `ImplantClassCapabilities`
(Sec 5.2): exploit is contract and dispatch only -- which class an exploit
module runs on is decided when an operator deploys the out-of-tree module, not by
a baked-in class rule. Their concrete behavior is operator-supplied tradecraft
(Sec 10.2, Sec 13): the core ships no
weaponized exploit code or proof-of-concepts, so each verb runs only when an
operator supplies a module for it.

### 10.2 Capability modules (the operator-supplied seam)

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
gate as any built-in verb. [extending/tradecraft.md](extending/tradecraft.md)
is the worked guide for module authors: both halves of a capability, the
registration paths, and the seams' current limits. The one runtime loader is config-listed and narrowly
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
**reuses** an implant's active session on a reconnect (a poll-mode contact or a
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
The session's whole life is live on the operator event stream: opening a
genuinely new session fans out a `SessionOpened` event (the same flood guard
-- a poll contact reuses the active session and publishes nothing), so
connected operators watch an implant come online the moment it contacts
rather than on the next roster poll, and closing the session is what drops
it off the online roster; each swept
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

Dispatch onto the stream is push-based. Every accepted enqueue -- an issuance,
or a dispatch returned to the queue by a failed write -- releases a per-implant
wake that the stream's dispatch writer parks on, so a queued task is pushed
downstream the moment it is queued and an idle stream costs nothing: no poll
loop in the writer path. The wake is a hint, not a ledger: the writer claims
before it parks, so tasks queued while no stream was open are picked up on
connect without relying on the wake, and a stale permit costs one empty claim,
never a lost task.

**The synchronous exception.** A WebShell-class implant (Sec 5.2) never opens
a session, so no dispatch writer exists to claim its tasks: the operator's
execution request itself plays the beacon -- issue, claim, one protocol-adapter
round trip, record result, each with the audit beats the stream path makes.
The task therefore never parks queued (a failed round trip completes it
failed), and the claim the route makes is the only claimer there is; the
lifecycle, the audit arc, and the timeline read exactly like a beacon's
capture. This is the third claim exception beside the channel rules -- a
channel task is not claimed over DNS (a datagram poll carries no input
half) -- and it needs no gate of its
own: with no session there is no stream to claim from, by construction.

**The dispatch strand.** A written frame used to count as delivered, and below
the result no delivery evidence existed: a claimed task whose frame rode a
stream that died stayed Dispatched forever, because the failed-write requeue
covers only the write. The receive-ack arm closes that strand. An implant
whose handshake advertised `task_acks` gets the arm echoed, acks every parsed
`TaskRequest` with a `TaskAck` frame before executing it, and the live stream
transports (the gRPC stream, the WebSocket beacon, QUIC) hold each dispatched
task in a per-stream ledger until its ack crosses -- a stream that ends
holding an ack-less dispatch returns it to the queue, so the task rides the
next contact instead of stranding. Delivery is then at-least-once, and the
implant makes that safe: it recognizes a task it already held (a bounded
per-run ledger of parsed ids), re-acks it without running it twice, and
re-sends its cached result when the original delivery died with a stream.
Duplicate results are idempotent on the server -- the completion transition
is atomic in the task store, so the first result wins and a retransmit
changes nothing -- which is also what makes an eager implant safe: either the
original or the resend may land, never both. The negotiation is deliberately
per handshake, never sticky on the implant the way the replay-nonce flag is
(Sec 9): a handshake that stops advertising must drop the arm, or the server
would requeue dispatches an unupgraded implant already ran. Unupgraded
implants never advertise and keep today's semantics exactly -- a written
frame counts as delivered, a lost result leaves the task Dispatched -- and
the poll carriers (DNS, the plain-HTTP envelope, the pipe/TCP contacts)
keep their own posture: their response is answered whole or not at all, they
hold no ack ledger, and an ack they receive is accepted and inert.

A queued task can be retracted before the implant wakes:
`POST /engagements/{engagementId}/tasks/{taskId}:cancel` takes the operator's
own tasking back (the shell command issued in error, the target that went
quiet), terminal and claim-proof -- the retransition is atomic against the
dispatch claim, so a cancel racing a claim resolves one way, never both, and a
cancelled task is never handed to a stream. A task already dispatched is the
implant's to run: the cancel is a `409`, not a rewrite of history. The
retraction lands in the engagement trail as a `TaskCancelled` event attributed
to the cancelling operator, ending the task's audit arc with no dispatch
behind it, and a `TaskCancelled` live event drops it from every connected
operator's queue view the moment it is retracted.

**The fronting claim.** A beacon stream's writer claims for its own implant
first and, in widening order, for the Pivot children that implant fronts
(Sec 5.2): the claim spans the fronting set -- parent plus fronted children --
and hands out the oldest queued task across the set by issue order, with the
same claim-once guarantee a single-implant claim carries. Issuing to a pivot
child releases the fronting parent's wake too (the child has no writer of its
own to wake), and a fronted task returned by a failed write requeues the same
way. The claim is the stream transports' alone: DNS and the plain-HTTP
envelope keep the narrow claim, because a fronted channel's input half needs
the stream the fronting executor holds. A claimed fronted task is marshaled
with `target_implant_id` naming the child -- the signature still signs the
child's own id in the tuple (Sec 9), so verification on the parent binds
exactly what the server authorized -- and nonce-less, because the child never
handshakes and so never negotiated the replay-nonce arm. Upstream, a fronted
task's frames (channel output, staged pulls, results) are accepted on the
parent's stream when the task belongs to a fronted child, and the operator
input route reaches a fronted channel through the parent's sink; every record
-- issued, dispatched, each input post, completed -- attributes to the child.
The evolution rules hold: an implant that never implements fronting ignores
the unknown field, and a server never fronts tasking to one that does not
(enough: the field is absent on all its frames, the Tier 0 shape unchanged).

**The streaming task shape.** Not every verb is a one-shot round trip:
`shell.interact` -- `shell.exec`'s interactive shape -- and `tunnel.forward`
-- the port-forward bridge (Sec 5.2, Sec 14) -- run as live channels. A
TaskRequest dispatches like any other (same signature, same queue), but on
the implant it opens a channel instead of completing inline: output streams
back as ChannelOutput chunks that land on the task's transcript while it is
still Dispatched (an operator reads the shell live, over the same task read),
and the operator's typing flows down as ChannelInput frames -- routed from
the operator input route through a per-implant live-channel hub into the
stream's dispatch writer, so the stream's single-writer discipline holds. A
final ordinary TaskResult closes the task with the whole session as its
record; the operator's eof closes the shell's stdin, which is the natural end
of the session -- and for the tunnel it half-closes the TCP send side, the
channel ends when the tunneled peer closes. The channel is session-scoped by
construction: it lives on the beacon stream that carried its TaskRequest, so
a dropped stream kills the shell or the tunnel (the implant's write gate and
channel lifetime see to that) and the task stays dispatched -- and DNS never
claims a channel task at all, because a datagram poll has no stream to carry
the input half. The poll carriers have a third answer, the degraded
discipline, carried by every poll artifact: the artifact advertises the
store-and-forward capability in its handshake, and the interactive verbs
claim over its envelope contacts -- operator input parks server-side
(DegradedChannelHub, bounded per task) and rides the next cycle's response,
the handler's output batches upstream, and a channel the implant stops
collecting closes itself with a timeout result. The tradeoff is the
operator's to make, not the bake's: while a channel is open, the
interactive traffic runs at the contact cadence, every keystroke costing
up to one interval each way. The reference implant's shell channel runs the platform shell
under a pseudo-terminal on Unix -- the documented `script` wrapper -- so the
channel behaves like a real terminal: prompt, line editing, and the
interrupt byte becoming SIGINT for the foreground program. Where no PTY
wrapper exists (a stripped container, Windows pending ConPTY) it falls back
to the plain pipes shape -- byte-transparent, no prompt or line editing --
and a richer PTY handler is a drop-in over the same byte-transparent channel
contract. Its tunnel channel bridges the same contract to a TCP connection
of the implant's own -- the byte transparency is what lets one channel shape
carry stdio and sockets alike. The same transparency carries the tunnel's
machine half: a relay bind (Sec 10.1) bridges a teamserver-side TCP listener
onto the channel, so the byte-transparent contract serves an unmodified tool
exactly as it serves an operator's keystrokes -- the input route and the
relay are two producers of the same channel-input queue, and the transcript
plus the relay's own audit pair are the attributed record either way.

## 11. Evidence and reporting -- a first-class output

A red-team operation ends in a deliverable: timeline, findings, and evidence. Rod
treats the audit trail as the **source for report generation**, not a post-hoc
scrape.

- **Every action is an immutable, attributed event**: `operator_id`,
  `engagement_id`, `implant_id`, `task_id`, `command`, `timestamp`, input
  parameters, and output/result. Linked artifacts are recorded as separate
  `ArtifactAttached` (operator attach) or `ExfilCaptured` (implant-side exfil)
  events whose outcome carries the artifact id -- artifacts live in the artifact
  store, joined by `task_id`, not as a field on every event. Operator notes on
  implants -- the free-text "whose beacon is this" memory -- are events too
  (`ImplantNoteAdded`, written through the implant's notes routes and read back
  as a query over the trail), so the note's only storage is the chain: it rides
  the same durability and chain-of-custody as every engagement fact, with no
  separate note store to keep consistent. This is the engagement timeline by
  construction.
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
- **The close-out exports the evidence as one package.** A finished engagement
  leaves behind a deliverable that must survive teardown and re-verify with no
  Rod infrastructure running: `POST /engagements/{id}:evidence-package` writes a
  ZIP carrying the full hash-chained trail (`audit.jsonl`, the store encoding),
  the artifacts (`artifacts.jsonl`), and the report (`report.json`/`report.md`),
  pinned by a `manifest.json` that records every other file's size and SHA-256
  plus the event/artifact counts. The close-out path is ordered:
  **freeze** (`:freeze`) stops new tasking, enrollments, and token mints so the
  trail is final -- in-flight results still land, and re-export before retire
  carries them; a mistaken freeze is reversible before retirement
  (`:unfreeze` reopens the engagement); **export** builds and verifies the
  package server-side before it
  leaves; **retire** (`:retire`) completes the close-out, terminal, and is
  refused on an open engagement so the export cannot be skipped. Each step is an
  audited operator event (`EngagementFrozen`, `EvidenceExported`, whose outcome
  is the exported chain-head hash, `EngagementRetired`). Offline, the
  teamserver binary itself re-verifies a package --
  `Rod.TeamServer --verify-evidence <package.zip>` -- recomputing every digest,
  re-running the chain check, and validating the artifact records, with no
  listeners and no stores: the acceptance bar is that a closed engagement's
  package re-verifies byte-exact on a host with nothing Rod running on it.

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

### 12.2 Toolchain: .NET plus Rust in-tree, polyglot by contract

Rod ships two in-tree toolchains, each with a job the other cannot do. The
**control plane and the full-capability reference implant are .NET 10** (the
teamserver, the stager, the richest verb set, the extension overlay, the
in-memory dll form); the **reach implant is Rust**
(`src/implant/rust/`, built by `RustBuildUnit`): a ~2 MB native binary for
the targets a managed runtime cannot serve -- static musl on routers,
32-bit ARM/MIPS IoT Linux, native shells for mobile platforms -- carrying the
core verb set plus the Windows sensitive verbs (inject.shellcode,
collect.minidump, collect.keylog) that self-gate on `cfg(windows)` so a
Linux build compiles none of them. Both speak the same wire protocol
(rod.proto, the baked profile's base64url JSON, the sealed envelope) and are
proven against it by the same end-to-end acceptance; the .NET implant retires
when the Rust one reaches core-verb parity and the conformance suite runs
green against it. The wire protocol remains the language-neutral product and
the `Language` enum (Go/DotNet/Rust/C/Nim) and build contract stay, so an
out-of-tree community implant in Go, C, or Nim registers a build unit and
compiles against the same contract -- polyglot by contract, not by in-tree
parity. .NET is cross-platform via self-contained publishes
(Linux/Windows/macOS from one source), and Native AOT produces the
single-file, no-runtime binary that was the original reason to reach for Go on
the redirector edge -- the same AOT publish the build pipeline now offers for
implants and stagers, alongside the trimmed and in-memory-loadable dll forms.

Rejected alternatives: **a single language end to end** (neither .NET alone
reaches 32-bit ARM/MIPS IoT or a ~2 MB footprint, nor Rust alone carries the
teamserver's velocity and the extension overlay -- two references each doing
their own job beats one doing both badly); **collapse to Go instead of .NET**
(the control plane is .NET 10, so standardizing on .NET keeps the control
plane in one toolchain); and **asymmetric polyglot -- .NET full, a second
language specialist only** (still leaves a second toolchain to build and test
in CI for a small team, with no benefit over the opt-in contract path).

## 13. Capability surface statement

Rod's capability surface is governed by **contracts, not by a technique
allowlist**. The core defines the interfaces, registration, dispatch, and data
models for every capability family; concrete implementations arrive either
in-tree (the reference set the framework ships with) or as opt-in modules
through the extension seams (server-side modules, the implant handler overlay,
the transform chain). Nothing in this repository maintains a list of which
technique kinds may exist -- an operator building for an authorized engagement
composes the surface they need from the reference set plus their own modules,
and the discipline that governs that work is the authorized-use premise below,
not a taxonomy curated here.

- All use assumes an authorized context; see [SECURITY.md](../SECURITY.md).

## 14. Capability bar (design aspiration)

Rod is designed to meet or exceed the state of the art across the capability
dimensions a modern offensive platform is expected to cover. This is a standing
design constraint, not marketing: every capability area below must be planned and
built so that its reach, flexibility, and OPSEC qualities are at least on par
with -- and aim to surpass -- what established platforms offer in that area.

- **Core operations** -- shell execution (interactive and one-shot), file
  transfer, tunneling, host enumeration, process enumeration and termination:
  as capable and as OPSEC-tunable as the
  best available, with per-implant profiles baked in at generation.
- **Reconnaissance** -- port and service discovery, host and network
  enumeration: comprehensive, fast, and audited.
- **Lateral movement** -- token and credential reuse, remote execution, child
  implant derivation and pivoting: full coverage with parentage tracking.
- **Persistence** -- a broad, cross-platform set of mechanisms, installable and
  removable, all recorded.
- **Collection and exfiltration** -- file, credential, screen, and input
  collection, staged and transferred over the C2 channel, every byte
  attributed.
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
outright, whatever it adds (see [extending/implants.md](extending/implants.md)).
