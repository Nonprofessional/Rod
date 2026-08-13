# Architecture-vs-implementation audit

- **Date:** 2026-08-11
- **Baseline:** [architecture.md](../architecture.md) at HEAD (609 lines, 14 sections)

> **Note:** this is a point-in-time snapshot. [ADR 0009](../decisions/0009-single-in-tree-toolchain-dotnet.md)
> later removed the in-tree Go implant and Go build unit; findings below that
> reference `implant/` or `GoBuildUnit` describe the baseline as of this date,
> not the current tree.
- **Scope:** every load-bearing claim in architecture.md Sec 1--14, checked against
  the source under `src/`, `implant/`, and `implant-dotnet/`.
- **Method:** per-section claim extraction, then code verification
  (file:line evidence below). Each finding is one of:
  - **TRUE** -- the claim matches the implementation.
  - **PARTIAL** -- the claim is accurate at its core but overstates or omits a detail.
  - **DIVERGENT** -- the claim does not match the implementation.
  - **STALE** -- the claim was accurate at an earlier milestone and has not been updated.

The intent is a faithful record of *where the doc and the code disagree* and,
for each gap, *whether it is intentional*. The doc is the blueprint; where the
code has moved further than the doc, the doc loses. Where the doc claims
something the code does not deliver, the finding names the gap and a proposed
resolution.

---

## Summary

The architecture doc is broadly accurate. The M1--M8 milestone annotations
inside the Sec 4.3 layer table are kept current on every delivery, which is
where most implementation reality lives; those rows verified clean across the
board. The divergences cluster in two places:

1. **The doc header and a handful of Sec 7--9 prose claims** were written when
   the repo was pre-implementation and have not been revisited as code landed.
   The header's "no code is implemented yet" is the most visible example.
2. **Two security-model claims** (command signing, ROE guardrails) are stated as
   present-tense properties but are not implemented, and -- unlike *Sealing*,
   which is correctly hedged as `_(future)_` -- neither is so marked.

Everything load-bearing in Sec 10 (capability model, tasking gate, the
widens-never-narrows resolver rule), Sec 11 (audit trail, artifacts, durable
adapter), and the M9.1 lateral-move child-derivation path is TRUE and matches
the code verbatim.

| # | Section | Claim | Verdict |
|---|---------|-------|---------|
| 1 | Header | "pre-implementation ... no code is implemented yet" | **STALE** |
| 2 | Sec 5.2 | Stage-2 carries "the collect set" (which includes `collect.keylog`) | **PARTIAL** (admit-gate vs. execution; see finding) |
| 3 | Sec 7 | All OPSEC mechanisms (jitter, kill date, per-implant key, malleable profile) | **TRUE** |
| 4 | Sec 8 | "Supported listener transports: HTTP(S), mTLS, DNS, SMB, TCP" | **DIVERGENT** (only HTTP(S) + mTLS) |
| 5 | Sec 8 | "A single frame stays well under 1 MiB" | **PARTIAL** (comment only; no enforcement) |
| 6 | Sec 9 | "Dispatched tasks are signed" | **DIVERGENT** (not implemented; not marked `_(future)_`) |
| 7 | Sec 9 | "Sealing _(future)_" | **TRUE** (correctly absent) |
| 8 | Sec 9 | mTLS cert binds `(implant_id, engagement_id)` | **TRUE** |
| 9 | Sec 9 | "ROE guardrails. The audit store feeds guardrails..." | **DIVERGENT** (aspirational; no implementation) |
| 10 | Sec 10.1 | Capability categories and per-verb OPSEC attributes | **TRUE** |
| 11 | Sec 10.3 | Tasking gate; resolver widens, never narrows | **TRUE** |
| 12 | Sec 10.1 | M9.1 lateral.move child-derivation (both implants + wire + server) | **TRUE** |
| 13 | Sec 11 | AuditEvent carries "linked artifacts" as a field | **PARTIAL** (relational, not a field) |
| 14 | Sec 11 | Audit kinds, artifact endpoints, timeline/report, durable adapter | **TRUE** |
| 15 | Sec 12 | "Data store -- PostgreSQL -- Authoritative teamserver state" | **PARTIAL** (opt-in; in-memory still default) |
| 16 | Sec 12 | "Redirectors -- Go, static single binary" | **DIVERGENT** (no redirector ships; only the server-side repoint path) |
| 17 | Sec 12 | Build units / implants for C#/.NET, Go, C/C++, Nim | **PARTIAL** (only .NET + Go exist) |

Findings 1, 4, 6, 9, and 16 are the ones worth fixing. The rest are either
accurate or call for a one-line clarification.

---

## Finding 1 -- Header status is stale

**Location:** architecture.md lines 3--7.

> **Status:** Design (pre-implementation). This document is the agreed blueprint
> for Rod as an authorized-use red-team command-and-control (C2) platform. The
> repository currently holds only documentation and conventions; no code is
> implemented yet. Sections marked _(future)_ are deliberately out of the initial
> scope.

**Verdict: STALE.**

The body of the same document contradicts the header. Sec 4.3's layer table
records implemented state through M8.1, the roadmap marks M1--M10.1 as complete,
and the tree holds a building teamserver (`Rod.slnx`, nine projects), two
reference implants (`implant/`, `implant-dotnet/`), and a Postgres persistence
layer (`src/Rod.Persistence/`). The header was accurate on 2026-07-30 (ADR 0001
date) and has not been revisited.

**Resolution:** update the Status block to reflect that the blueprint is largely
implemented. Suggested replacement:

> **Status:** Living document. This is the agreed architecture for Rod as an
> authorized-use red-team command-and-control (C2) platform. The repository
> holds the teamserver, two reference implants (Go, .NET), and a Postgres
> persistence layer; [roadmap.md](roadmap.md) tracks delivery. Sections marked
> _(future)_ are designed for but not yet implemented.

---

## Finding 2 -- "collect set" in Sec 5.2 vs. `collect.keylog` out-of-tree

**Location:** architecture.md lines 209--216 (Sec 5.2) vs. lines 446--449
(Sec 10.1) and [ADR 0004](../decisions/0004-offensive-tradecraft-boundary.md).

Sec 5.2 says Stage-2 carries "the full core set plus the recon set, the lateral
set, the persist set, **the collect set**, and the exfil set". The collect set,
per the Sec 10.1 category table and `CollectCapabilities`, is
`{collect.file, collect.cred, collect.keylog}`. So a literal reading puts
`collect.keylog` in Stage-2's class-admit set.

Sec 10.1 (lines 446--449) and ADR 0004 say `collect.keylog` is contract-only
and runs *only* when an operator supplies an out-of-tree module.

**Verdict: PARTIAL -- coherent in the code, under-explained in the doc.**

The code is self-consistent. `ImplantClassCapabilities` Stage-2 lists
`collect.keylog`
(`src/Rod.CoreState/Implants/ImplantClassCapabilities.cs:36-50`), and the M8.1
resolver (`CapabilityRegistryTaskResolver.IsDispatchable`,
`src/Rod.Tradecraft/Registry/CapabilityRegistryTaskResolver.cs:49-51`) admits a
verb when the class set allows it **OR** a module is registered. So
`collect.keylog` is *class-admissible* for Stage-2 (the class gate does not
refuse it), but no built-in module handles it, and ADR 0004 keeps the concrete
behavior out-of-tree. Admission and execution are separate gates; the doc never
states this explicitly, so Sec 5.2 reads as if Stage-2 *executes* keylogging.

**Resolution:** add one sentence to Sec 5.2 distinguishing class-admission from
execution, citing ADR 0004 for the verbs that are admissible but carry no
built-in handler:

> Admission is not execution: a verb may be class-admissible (the class gate
> does not refuse it) yet ship no built-in handler, running only when an
> operator supplies an out-of-tree module. The contract-only verbs
> (`collect.keylog`, and the `evasion` and `exploit` categories in their
> entirety) follow this shape; see ADR 0004 and Sec 10.2.

---

## Finding 3 -- OPSEC mechanisms (Sec 7)

**Verdict: TRUE** across all five claims.

- **Jitter.** Go: `sleepWithJitter`
  (`implant/internal/beacon/beacon.go:228-244`) adds a symmetric delta to
  `b.sleep`, clamped non-negative. .NET mirrors it
  (`implant-dotnet/Internal/Beacon.cs:220-231`).
- **Kill date, both sides.** Server refuses at handshake
  (`src/Rod.CoreState/Application/HandshakeService.cs:105-110`, returns
  `HANDSHAKE_STATUS_KILL_DATE_EXPIRED`, `rod.proto:133`). Implant refuses at
  start (`implant/cmd/rod-implant/main.go:46-48`,
  `implant-dotnet/Program.cs:50-54`) **and** re-checks at the top of every
  beacon cycle (`implant/internal/beacon/beacon.go:99-103`,
  `implant-dotnet/Internal/Beacon.cs:110-114`). The per-cycle re-check is the
  load-bearing half of the claim and is present.
- **Per-implant 32-byte key at enrollment AND build time.**
  `EnrollmentService.cs:96` and `PayloadBuildService.cs:49` both call
  `RandomNumberGenerator.GetBytes(32)`.
- **Malleable transport profile applied at enroll (both implants).** URI path
  via `ResolvedEnrollURL` (`implant/internal/config/config.go:85-109`,
  `implant-dotnet/Internal/Config.cs:81-105`); User-Agent + headers
  (`implant/internal/c2/enroll.go:164-169`, `implant-dotnet/Internal/C2.cs:183-191`);
  timeout (`enroll.go:144-153`, `C2.cs:165-168`); base64 body envelope
  (`enroll.go:139-142`, `C2.cs:174-176`).
- **Burn handling.** Retire endpoint + `ImplantRetired` audit kind + session
  close verified earlier; repoint endpoint verified separately.

---

## Finding 4 -- Listener transports overstate coverage

**Location:** architecture.md line 300 (Sec 8).

> Supported listener transports: **HTTP(S)**, **mTLS**, **DNS**, **SMB**, **TCP**.

**Verdict: DIVERGENT.**

Only HTTP(S) and mTLS are implemented. `src/Rod.Transport/Listeners/ListenerTransport.cs:8-23`
defines an enum with exactly `Http` and `Mtls`; its own doc-comment states
"M2.2 ships `Http` and `Mtls`; DNS, SMB, and TCP are the remaining transports
the architecture calls out, added in later milestones." No DNS/SMB/TCP listener
exists anywhere under `src/Rod.Transport/`.

**Resolution:** mark the unshipped three as planned, matching how Sec 9 marks
*Sealing*. Suggested rewrite:

> Supported listener transports: **HTTP(S)** and **mTLS** are implemented;
> **DNS**, **SMB**, and **TCP** are planned (the protocol semantics are
> transport-independent, and the listener abstraction is in place).

---

## Finding 5 -- Frame-size cap is comment-only

**Location:** architecture.md line 312 (Sec 8).

> A single frame stays well under 1 MiB and never exceeds the negotiated maximum.

**Verdict: PARTIAL.** The constraint is stated as a proto-level comment
(`src/Rod.Protocol/protos/rod.proto:17-19`) and chunking exists for the bulk path
(`ExfilChunk` reassembly in `BeaconEndpoint.cs:509-540`), but no constant,
validation, or `MaxReceiveMessageSize` configuration enforces the ceiling
anywhere in `src/`, `implant/`, or `implant-dotnet/`. The default gRPC receive
limit (~2 MB on the server) exceeds the 1 MiB figure the doc names, so the cap
is advisory.

**Resolution:** low priority -- either implement a frame-size check on the
beacon read path or soften the doc to "design intent, enforced at the transport
boundary as implementations land."

---

## Finding 6 -- Command signing claimed but not implemented

**Location:** architecture.md line 334--335 (Sec 9).

> Command signing. Dispatched tasks are signed so an implant only acts on
> teamserver-authorized tasking.

**Verdict: DIVERGENT.** This is the most material doc defect in the audit.

`TaskRequest` (`src/Rod.Protocol/protos/rod.proto:159-163`) carries only
`task_id`, `verb`, `arguments` -- no signature field. A repo-wide search for
signing primitives (`sign`, `signature`, `hmac`, `ed25519`, `rsa.sign`,
`VerifyData`) across `.cs`, `.go`, `.proto` finds zero signing logic on the
tasking path. The integrity guarantee actually in place is the mTLS channel
binding `(implant_id, engagement_id)`, which authenticates the *implant* but
does not sign individual tasks. The claim is stated as a present security
property and is *not* marked `_(future)_` -- compare the very next bullet,
*Sealing*, which is correctly hedged.

**Resolution:** either implement task signing or, more honest for now, rewrite
the bullet to match *Sealing*'s tense and mark it planned. Suggested rewrite:

> Command signing _(future)_. Dispatched tasks are intended to be signed so an
> implant only acts on teamserver-authorized tasking. Designed for, not
> implemented initially; until it lands, task integrity rests on the mTLS
> channel binding (`implant_id`, `engagement_id`).

If keeping the claim as a present property, track it as an open item in
[todo.md](../todo.md).

---

## Finding 7 -- Sealing correctly hedged

**Location:** architecture.md lines 336--337 (Sec 9). **Verdict: TRUE.**
Marked `_(future)_`; a repo-wide search finds no sealing logic, consistent with
the doc. No action.

---

## Finding 8 -- mTLS cert binding

**Location:** architecture.md lines 330--333 (Sec 9). **Verdict: TRUE.**

`DevCertificateAuthority.IssueLeaf` puts `implant_id` in the subject DN
(`src/Rod.CoreState/Pki/DevCertificateAuthority.cs:85`) and `engagement_id` in a
custom X.509 extension, OID `1.3.6.1.4.1.65535.1.1`
(`DevCertificateAuthority.cs:100`, defined in
`src/Rod.CoreState/Pki/RodImplantEngagementExtension.cs`). Server-side binding
is checked at handshake (`HandshakeService.cs:92-98` compares the cert's
engagement id to the implant's).

---

## Finding 9 -- ROE guardrails aspirational

**Location:** architecture.md lines 359--360 (Sec 9).

> ROE guardrails. The audit store feeds guardrails that warn or block high-risk
> actions against out-of-scope targets.

**Verdict: DIVERGENT.** No guardrail implementation exists. The word "guardrail"
appears only in documentation (`architecture.md:359`, `architecture.md:520`,
`glossary.md:70`, `roadmap.md:19`), never in source. There is no guardrail port,
no consumer of `IAuditStore` that warns or blocks, and no "out-of-scope" target
model. The roadmap's "Milestone 0 -- Tooling and guardrails" title is
misleading: its M0.x items are CPM, wire bindings, architecture tests, and CI
(`roadmap.md:21-33`), none of which implement guardrails.

**Resolution:** either soften the claim to a planned property (matching
*Sealing*) or move it to a named roadmap milestone. Suggested rewrite:

> ROE guardrails _(planned)_. The audit store is intended to feed guardrails
> that warn or block high-risk actions against out-of-scope targets. Not yet
> implemented; the audit store is in place and is the data source such
> guardrails will read from.

---

## Finding 10 -- Capability categories and OPSEC attributes (Sec 10.1)

**Verdict: TRUE** for all eight categories and every per-verb attribute.

`CapabilityCategory` declares exactly the eight categories
(`src/Rod.Tradecraft/Capabilities/CapabilityCategory.cs:12-46`). Each category's
descriptor class registers every example verb from the Sec 10.1 table
(`CoreCapabilities.cs`, `ReconCapabilities.cs`, `LateralCapabilities.cs`,
`PersistCapabilities.cs`, `CollectCapabilities.cs`, `ExfilCapabilities.cs`,
`EvasionCapabilities.cs`, `ExploitCapabilities.cs`). Every claimed OPSEC
attribute is present verbatim -- `derives-child`, `touches-credential`,
`touches-network`, `writes-to-disk`, `persists`, `reads-filesystem`,
`reads-credential`, `reads-input`, `modifies-defenses`, `exploits-target` --
with `persist.list`, `exfil.stage` correctly unflagged as reads.

---

## Finding 11 -- Tasking gate; resolver widens, never narrows (Sec 10.3)

**Verdict: TRUE.**

- `ITaskCapabilityResolver` port in `Rod.CoreState`
  (`src/Rod.CoreState/Implants/ITaskCapabilityResolver.cs:32`).
- `ClassTableCapabilityResolver` is the in-`Rod.CoreState` default and delegates
  purely to `ImplantClassCapabilities.Allows`
  (`src/Rod.CoreState/Implants/ClassTableCapabilityResolver.cs:26-27`).
- `CapabilityRegistryTaskResolver` in `Rod.Tradecraft` is swapped in by
  `AddRodTradecraft` at the composition root
  (`src/Rod.Tradecraft/RodTradecraftHost.cs:85-86`,
  `src/Rod.TeamServer/Program.cs:30`).
- The "widens, never narrows" claim is verbatim in the resolver body
  (`src/Rod.Tradecraft/Registry/CapabilityRegistryTaskResolver.cs:49-51`):
  ```csharp
  public bool IsDispatchable(ImplantClass @class, string verb)
      => ImplantClassCapabilities.Allows(@class, verb)
          || _registry.FindAsync(verb).GetAwaiter().GetResult() is not null;
  ```
  The logical OR means the registry path can only add dispatchability.

---

## Finding 12 -- M9.1 lateral.move child-derivation (Sec 10.1)

**Verdict: TRUE** on every clause across both implants, the wire, and the server.

- **Go implant** (`implant/internal/exec/lateral.go:46-79`): parses the stager
  token from args (`parseMoveArgs`, `lateral.go:84-93`), generates a fresh
  2048-bit RSA child keypair (`lateral.go:60`), and enrolls a child naming
  itself (`r.enroll.ParentID`, set at `implant/cmd/rod-implant/main.go:98`) as
  parent.
- **.NET implant** (`implant-dotnet/Internal/Lateral.cs:43-75`): same shape;
  `enroll.ParentId` set at `Program.cs:93`.
- **Enroll clients thread parentage.** Go
  `enrollRequest.ParentImplantID` (`implant/internal/c2/enroll.go:38-43,130`);
  .NET `EnrollRequest.ParentImplantId` (`implant-dotnet/Internal/C2.cs:33-49`).
- **Proto.** `EnrollResponse.parent_implant_id` field 6
  (`src/Rod.Protocol/protos/rod.proto:98-105`), echoing the server-recorded
  parent.
- **Server parentage.** `EnrollmentService.ResolveParentAsync`
  (`src/Rod.CoreState/Application/EnrollmentService.cs:128-156`) enforces all
  three rules (exists, same engagement, not retired) and records the parent on
  the child via `Implant.EnrollChild`
  (`src/Rod.CoreState/Implants/Implant.cs:110-130`).

---

## Finding 13 -- AuditEvent "linked artifacts" is relational, not a field

**Location:** architecture.md lines 509--512 (Sec 11).

> Every action is an immutable, attributed event: `operator_id`,
> `engagement_id`, `implant_id`, `task_id`, `command`, `timestamp`, input
> parameters, output/result, and linked artifacts.

**Verdict: PARTIAL.** Eight of the nine listed attributes are fields on
`AuditEvent` (`src/Rod.Audit/AuditEvent.cs:20-33`: `OperatorId`, `EngagementId`,
`ImplantId`, `TaskId`, `Verb`, `At`, `Payload`, `Output`/`Outcome`). "Linked
artifacts" is not a field -- it is a relational concept: artifacts live in
`IArtifactStore` joined by `TaskId`, and the binding is recorded as a *separate*
`AuditEvent` of kind `ArtifactAttached` (operator attach) or `ExfilCaptured`
(implant-side exfil), whose `Outcome` carries the artifact id. The record also
carries three fields the bullet omits (`EventId`, `Kind`, `PreviousHash`/`Hash`),
which Sec 11's later paragraphs do cover.

**Resolution:** minor. Reword "and linked artifacts" to "and linked artifacts
(recorded as separate `ArtifactAttached` / `ExfilCaptured` events; see below)"
so the reader does not expect an artifact field on every event.

---

## Finding 14 -- Audit kinds, artifact endpoints, timeline/report, durable adapter

**Verdict: TRUE** across the board.

- `AuditEventKind` has all 11 members
  (`src/Rod.Audit/AuditEventKind.cs:23-118`): `EngagementCreated`,
  `StagerTokenMinted`, `ImplantEnrolled`, `SessionOpened`, `TaskIssued`,
  `TaskDispatched`, `TaskCompleted`, `PayloadBuilt`, `ImplantRetired`,
  `ArtifactAttached`, `ExfilCaptured`.
- Audit trail endpoint `GET /engagements/{engagementId}/audit`, oldest-first
  (`src/Rod.Transport/Endpoints/AuditEndpoints.cs:20-41`).
- Artifact endpoints (M6.2): attach, list-per-task, retrieve-by-artifact-id
  (`src/Rod.Transport/Endpoints/ArtifactEndpoints.cs:32-45`).
- Timeline + report export (M6.3): `GET .../timeline`, `GET .../report`, each
  JSON or Markdown via `format=` (`src/Rod.Transport/Endpoints/ReportEndpoints.cs:47-53`).
- Retire + repoint (M4.4):
  `POST /engagements/{engagementId}/implants/{implantId}:retire`
  (`src/Rod.Transport/Endpoints/ImplantEndpoints.cs:37`),
  `POST /listeners/{id}:repoint`
  (`src/Rod.Transport/Endpoints/ListenerEndpoints.cs:25`).
- Durable adapter (M6.4): `FileAuditStore` / `FileArtifactStore` (JSON Lines +
  blob per artifact) swapped in when `Audit:DataDirectory` is present
  (`src/Rod.Audit/FileAuditStore.cs`, `FileArtifactStore.cs`,
  `src/Rod.Transport/TransportHost.cs:82-101`). The composition root recovers
  each engagement's chain head on startup.

The exfil receive path added under ADR 0004 is TRUE end-to-end: `FrameKind`
enum and `ExfilChunk` message in the proto (`rod.proto:49-78`), `BeaconEndpoint`
branches on `frame.Kind` with a per-stream `ExfilReassembler`
(`BeaconEndpoint.cs:268-285, 509-540`), builds an engagement-scoped `Artifact`
attributed to the deploying operator, saves it, and appends an `ExfilCaptured`
event (`BeaconEndpoint.cs:355-421`). The two-test integration
(`tests/Rod.Integration.Tests/ExfilRoundTripTests.cs`) covers the single-chunk
and three-chunk reassembly paths.

---

## Finding 15 -- PostgreSQL is opt-in, not the default authoritative store

**Location:** architecture.md line 538 (Sec 12 tech-stack table).

> Data store -- PostgreSQL -- Authoritative teamserver state; per-engagement
> audit.

**Verdict: PARTIAL.** Postgres is now wired (ADR 0003 is *Accepted*,
roadmap M10.1 is `[x]`, all eight ports have `Postgres*` adapters in
`src/Rod.Persistence/Stores/`, EF migrations exist, `Program.cs:39` calls
`AddRodPersistence`), **but** the in-memory adapter is still the default.
`RodPersistenceHost.cs:46-52` returns early when
`ConnectionStrings:Postgres` is absent, so Postgres adapters only `Replace` the
in-memory ones when the connection string is set. ADR 0003 itself states
"the in-memory path stays as the default"
(`docs/decisions/0003-data-access-postgres.md:60-62, :128`).

**Resolution:** clarify in Sec 12 that Postgres is the *target* authoritative
store, wired and opt-in, with in-memory as the default for tests and skeleton
deployments. One-line note: "PostgreSQL is the authoritative store when
configured (`ConnectionStrings:Postgres`); absent it, in-memory adapters remain
the default (ADR 0003)."

---

## Finding 16 -- No redirector binary ships

**Location:** architecture.md line 539 (Sec 12 tech-stack table) and lines
135--136 (Sec 4.2).

Sec 4.2:
> Redirectors. Near-stateless forwarders (Go, single static binary) for OPSEC
> and infra flexibility.

Sec 12 table:
> Redirectors -- Go (latest stable), static single binary -- Tiny VPS footprint.

**Verdict: DIVERGENT.** No `redirector/` directory exists in the tree; the only
Go source is under `implant/`. The roadmap marks M4.4 "Redirectors and burn
handling" as `[x]`, but the M4.4 deliverable that landed is the
**server-side rotation path** -- the listener repoint endpoint, retire, and
audit -- not a redirector binary. `docs/todo.md:71-73` still lists "Redirector
deployment story" as an open `[ ]` item, confirming no redirector ships.

**Resolution:** scope the claim to what shipped. Suggested rewrite of the Sec 12
row:

> Redirectors -- Go (planned), static single binary. The teamserver-side
> rotation path (listener repoint, `POST /listeners/{id}:repoint`) ships; the
> forwarder binary itself is a planned deliverable (todo.md).

And in Sec 4.2, append "(planned; the listener-repoint plumbing the redirector
depends on is implemented in M4.4)" to the redirector bullet.

---

## Finding 17 -- Build units and implants beyond Go + .NET

**Location:** architecture.md line 538 (Sec 12) and line 116 (Sec 4.2).

Both name C#/.NET, Go, C/C++, and Nim as build units / implant languages.

**Verdict: PARTIAL.** The `Language` enum lists all four
(`src/Rod.BuildPipeline/PayloadBuild/Language.cs:10-23`), but only `GoBuildUnit`
and `DotNetBuildUnit` exist and register
(`src/Rod.Transport/TransportHost.cs:127-128`). There are no `CBuildUnit` or
`NimBuildUnit` classes. Likewise, reference implants exist for Go (`implant/`)
and .NET (`implant-dotnet/`) only; no C/C++ or Nim reference implant ships. Sec
4.2 itself is accurate here (it explicitly says C/C++ and Nim "arrive with their
implants (M3.4+)"), so the divergence is only the Sec 12 table presenting the
four languages without that hedge.

**Resolution:** none required for Sec 4.2. For the Sec 12 table, either cite
Sec 4.2 or add "(Go and .NET implemented; C/C++ and Nim planned)".

---

## Recommended doc edits (in priority order)

1. **Finding 6 (command signing)** -- material security claim, not implemented,
   not hedged. Rewrite to `_(future)_` tense or track as an open todo.
2. **Finding 1 (header status)** -- the visible "no code is implemented yet" is
   the first thing a reader sees and is wrong.
3. **Finding 4 (transports)** and **Finding 9 (ROE guardrails)** -- both state
   unimplemented things as present. Hedge to planned.
4. **Finding 16 (redirector)** -- scope the claim to the rotation path that
   shipped; mark the binary as planned.
5. **Findings 2, 13, 15, 17** -- one-line clarifications; low effort, low risk.

Findings 3, 7, 8, 10, 11, 12, and 14 need no action.

The deferred-ADR work (arg shape, catalog endpoint, placeholder verbs) is
separate from this audit and follows it.

> **Later update (2026-08-13):** the `docs/decisions/` ADRs referenced in this
> snapshot (0001, 0003, 0004, 0009, and the deferred-decision set 0005/0006/0007)
> were folded into [architecture.md](../architecture.md) and the
> `docs/decisions/` directory removed; their links here are kept as the
> historical record of this date's tree.
