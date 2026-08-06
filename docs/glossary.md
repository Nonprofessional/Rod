# Rod -- Glossary

A quick reference for the terms used across docs/architecture.md and the
codebase. Definitions here are summaries; authoritative detail is in the linked
sections.

## Engagement and identity

| Term | Meaning |
|------|---------|
| **Engagement** | The unit of tenancy, isolation, authorization, and evidence -- one authorized operation. All domain state is engagement-scoped and disposable with the operation. |
| **Operator** | A global human identity; an authorized user of the platform. Access derives entirely from engagement memberships. |
| **Role** | `Owner` / `Lead` / `Operator` / `Observer` within an engagement. |
| **Stager token** | An engagement-scoped, short-lived, bounded-use secret used only during initial enrollment/deployment. |

## Implants and sessions

| Term | Meaning |
|------|---------|
| **Implant** | A short-lived, disposable payload on a target host. Untrusted by default; carries a unique per-implant key. Speaks the wire protocol. |
| **Session** | A live, authenticated implant connection in an engagement; the handle operators task against. |
| **Beacon** | The implant's periodic check-in to fetch tasking and push results, with configurable interval and jitter. |
| **Beacon profile** | The per-implant sleep, jitter, and kill-date parameters, baked into the artifact at generation. |
| **Kill date** | A hard self-termination timestamp baked in per implant; limits exposure if lost. |
| **Malleable profile** | A configurable transport shape (URIs, headers, timing, payload) that mimics legitimate traffic, per implant. |

## Implant classes

| Term | Meaning |
|------|---------|
| **Stage-2 implant** | The primary long-haul implant; full capability set and module support. |
| **Stager** | A tiny stage-1 loader that fetches a stage-2 implant. Separate generation output. |
| **Web-shell class** | A script in a web root, bound to the web transport; code execution over HTTP, no interactive PTY. |
| **Ephemeral** | A short-lived, TTL'd implant from a one-liner bootstrap; one-off execution and temporary access. |
| **Pivot** | An implant representing hosts that cannot run their own implant, enrolling each as its own session and forwarding tasking. |

## Infrastructure

| Term | Meaning |
|------|---------|
| **Teamserver** | The monolithic .NET control-plane kernel: core state, transport, build pipeline, operator layer, storage/audit, tradecraft. |
| **Listener** | The ingress endpoint that terminates a C2 transport (HTTP(S), mTLS, DNS, SMB, TCP). Decoupled from the public endpoint. |
| **Redirector** | A near-stateless Go forwarder that fronts a listener for OPSEC and infra flexibility. Burned redirectors are swappable at runtime by repointing the listener. No engagement state, no business logic. |
| **Repoint** | Repointing a listener swaps its public endpoint at runtime (`POST /listeners/{id}:repoint`) without touching the Kestrel bind; the old endpoint stops resolving, which severs it. |
| **Build unit** | A per-language compilation service (C#/.NET, Go, C/C++, Nim) driven by the teamserver through the build contract. |
| **Build contract** | The uniform message schema coupling the teamserver to build units; the language-neutrality boundary for generation. |

## Tasking and capabilities

| Term | Meaning |
|------|---------|
| **Capability** | A verb an implant advertises and the teamserver may dispatch; namespaced (`namespace.action`). |
| **Capability module** | A distributable, signed bundle adding capability verbs (core or offensive). Evasion/exploit behavior is delivered as out-of-tree modules. |
| **Recon** | The `recon.portscan`, `recon.hostenum`, and `recon.service` verbs (category `Recon`); target and network reconnaissance, gated to Stage-2 at task issuance (Sec 5.2). Like the non-shell core verbs their concrete behavior runs on the implants and is captured as task output -- the descriptors and dispatch live in the tradecraft layer. |
| **Lateral** | The `lateral.move`, `lateral.token`, and `lateral.exec_remote` verbs (category `Lateral`); lateral movement within an authorized engagement, gated to Stage-2 at task issuance (Sec 5.2). `lateral.move` is the deployment verb that derives a child implant: the child enrols through the standard enrollment route naming its parent, and the recorded `ParentImplantId` is the parentage linkage. The descriptors and dispatch live in the tradecraft layer; the concrete behavior is out-of-tree (Sec 13). |
| **Persist** | The `persist.install`, `persist.remove`, and `persist.list` verbs (category `Persist`); installing, enumerating, and tearing down footholds within an authorized engagement, gated to Stage-2 at task issuance (Sec 5.2). The descriptors and dispatch live in the tradecraft layer; the concrete behavior is out-of-tree (Sec 13) and the reference implants ship none (Sec 5). |
| **Collect** | The `collect.file`, `collect.cred`, and `collect.keylog` verbs (category `Collect`); file, credential, and input collection within an authorized engagement, gated to Stage-2 at task issuance (Sec 5.2). The descriptors and dispatch live in the tradecraft layer; the concrete behavior is out-of-tree (Sec 13) and the reference implants ship none (Sec 5). |
| **Exfil** | The `exfil.push` and `exfil.stage` verbs (category `Exfil`); staging collected data on the teamserver and transferring it over the C2 channel within an authorized engagement, gated to Stage-2 at task issuance (Sec 5.2). The descriptors and dispatch live in the tradecraft layer; the concrete behavior is out-of-tree (Sec 13) and the reference implants ship none (Sec 5). |
| **Task / Tasking** | An operator-issued request targeting a session; has a state machine, result, and attribution. |

## Evidence and OPSEC

| Term | Meaning |
|------|---------|
| **Audit event** | An immutable, hash-chained, attributed record of a privileged action; the engagement timeline and report source by construction. |
| **Artifact** | A first-class object (file, screenshot, command output) linked to a task; part of the evidence store. |
| **Retire** | Marking an implant retired from the operator API; a retired implant is refused at handshake (`HANDSHAKE_STATUS_IMPLANT_RETIRED`), untaskable, and its active session is closed. Idempotent; recorded as an `ImplantRetired` audit event.
| **Burn handling** | The recovery flow when an implant or endpoint is compromised: retire the implant, repoint (swap) the burned endpoint, and rebuild a fresh artifact with a fresh key. |
| **ROE guardrails** | Rules-of-engagement controls that warn or block high-risk actions against out-of-scope targets, reading from the audit store. |
