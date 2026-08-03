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
and audit; the build pipeline and transport depend on core state; core state and
audit depend on nothing in-house. The dependency rule is enforced by
architecture tests.

### 4.2 External components

- **Build units.** One per implant language (C#/.NET, Go, C/C++, Nim). Each
  owns its toolchain and compiles artifacts on demand. Coupled to the teamserver
  only by the build contract. (Sec. 6.)
- **Implants.** Target-resident, polyglot, disposable. Speak the wire protocol.
  Independent of the teamserver language. (Sec. 5.)
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
| `Rod.CoreState` | The teamserver's authoritative domain core: typed ids, the `Engagement` aggregate, operators, implants, tasks, stager tokens, the implant session registry, the task queue and history, and the per-engagement implant certificate authority. The use cases (`EngagementService`, `EnrollmentService`, `HandshakeService`, `TaskService`) orchestrate these ports and define the operational behavior everything else consumes. | Inner ring -- depends on nothing in-house. | Implemented (M2.1 core-state layer; sessions lift the M1.x presence record, ports carry an in-memory adapter). |
| `Rod.Audit` | The append-only, per-engagement audit trail: hash-chained `AuditEvent` records and the `IAuditStore` port, plus the `IArtifactStore` for first-class evidence objects attached to tasks. The evidence backbone (Sec. 11); the source for timeline and report export. | Inner ring -- depends on nothing in-house (crosses the layer boundary with primitive `Guid` ids, never core-state types). | Implemented (in-memory; M2.3: per-engagement hash chain -- tampering breaks the chain -- and the artifact store). |
| `Rod.Protocol` | **Not a layer.** The gRPC/protobuf wire protocol: frames, the enrollment/handshake/tasking messages, and the `Beacon` check-in stream (Sec. 8). The long-lived, language-neutral contract implants of every language build against. | Not a layer -- depends on nothing in-house; never leaks into `Rod.CoreState`. | Implemented (frame + M1.x messages). |
| `Rod.Transport` | Listeners that terminate C2 transports and map core-state use cases onto the operator HTTP API and the implant beacon stream. Owns endpoint routing, mTLS termination, and the mapping of use-case failures to wire status codes. | Layer 2 -- may depend on `Rod.CoreState`, `Rod.Protocol`, `Rod.Audit`. | Implemented (M1.x endpoints + M2.2 listener abstraction: HTTP(S) and mTLS listeners, bind address decoupled from the public endpoint). |
| `Rod.BuildPipeline` | Drives the external, per-language build units to compile polyglot implants on demand through the uniform build contract, fingerprinting and recording each artifact (Sec. 6). | Layer 3 -- may depend on `Rod.CoreState`. | Placeholder (`Layers/` marker only); M4.x. |
| `Rod.Operators` | Multiplayer operator sessions over the operator API: shared live engagement state, task ownership and attribution, and real-time push to the operator UI. | Layer 4 -- may depend on `Rod.CoreState`, `Rod.Audit`. | Implemented (M2.4: Server-Sent Events stream per engagement, a channel-backed live-event bus fanning task-issued / task-completed / presence events to every connected session, an operator-presence roster, and query-param session identity; real operator auth arrives later). |
| `Rod.Tradecraft` | Pluggable post-exploitation capability modules, including the evasion/exploit category contracts (Sec. 10, Sec. 13). Concrete tradecraft is out-of-tree; this layer holds the contract and dispatch only. | Layer 6 -- may depend on `Rod.CoreState`, `Rod.Audit`. | Implemented skeleton (M2.5: `ICapabilityModule` contract, capability registry + dispatcher, the five core verbs loaded through it; the dispatchable `shell.exec` stub proves the round-trip. Not yet wired onto the live task path -- that arrives with the offensive-capability milestones). |
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

A capable implant can deploy another class on the same host (e.g. a web-shell
deriving a stage-2 implant) via a deployment verb; the child enrols into the same
engagement and records its parent.

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
  exposure if lost.
- **Per-implant cryptographic key.** Unique per implant, so compromising one does
  not compromise all. Keys are generated and rotated server-side.
- **Malleable transport profiles.** URIs, headers, timing, and payload shape that
  match legitimate traffic, configurable per implant.
- **Disposable infrastructure.** Keys, identities, and endpoints are ephemeral
  per engagement; burned redirectors are swappable.
- **Redirector decoupling.** Filter by User-Agent / URI / IP / OS; forward only
  real beacon traffic, send the rest to a decoy.
- **Per-command OPSEC metadata.** Commands carry OPSEC flags (e.g. "writes to
  disk") so operators and tradecraft filters can avoid risky actions.
- **Burn handling.** Rotate keys/endpoints, retire an implant, sever a redirector
  quickly.

> This section defines what the platform must **provide** for OPSEC. It does not
> describe concrete evasion techniques. Those are out-of-tree capability modules
> (Sec. 10, Sec. 13).

## 8. Transports, listeners, and redirectors

- Supported listener transports: **HTTP(S)**, **mTLS**, **DNS**, **SMB**, **TCP**.
  Transport choice is a profile/deployment concern; the protocol semantics are
  transport-independent.
- An implant is always the **connection initiator** (reverse connection). The
  teamserver and redirectors never dial targets.
- **Listener and public endpoint are decoupled.** A redirector fronts the
  listener; a burned redirector is replaced without touching the backend. This
  decoupling is what makes disposable infrastructure practical.
- **Message sizing and flow control.** A single frame stays well under 1 MiB and
  never exceeds the negotiated maximum. Bulk data (files, output) is chunked.
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
- **Per-implant keys and rotation.** Unique keys per implant, generated and
  rotated server-side (Sec. 7).
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
