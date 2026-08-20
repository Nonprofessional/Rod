# Rod -- Glossary

A quick reference for the terms used across docs/architecture.md and the
codebase. Definitions here are summaries; authoritative detail is in the linked
sections.

## Engagement and identity

| Term | Meaning |
|------|---------|
| **Engagement** | The unit of tenancy, isolation, authorization, and evidence -- one authorized operation. All domain state is engagement-scoped and disposable with the operation. |
| **Operator** | A global human identity; an authenticated user of the platform. Any authenticated operator can operate on any engagement; accountability is through the attributed audit trail. |
| **Stager token** | An engagement-scoped, short-lived, bounded-use secret used only during initial enrollment/deployment. |

## Implants and sessions

| Term | Meaning |
|------|---------|
| **Implant** | A short-lived, disposable payload on a target host. Untrusted by default; generates its own keypair (identity bound by the CA-signed leaf at enroll -- no key material ships in the artifact). Speaks the wire protocol. |
| **Session** | The implant's live channel in an engagement -- not one TCP connection. Reconnects (poll check-ins, flapped streams) reuse it; the staleness sweeper or retirement closes it. Online means "seen within the staleness threshold". |
| **Beacon** | The implant's check-in over the reverse stream. Two modes are baked per implant: `stream` (persistent connection, interactive) and `poll` (drain tasking, close, sleep the interval with jitter -- the periodic low-and-slow shape). |
| **Beacon profile** | The per-implant check-in mode, sleep, jitter, and kill-date parameters, baked into the artifact at generation. |
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
| **Redirector** | A near-stateless .NET Native AOT forwarder (a single static binary) that fronts a listener for OPSEC and infra flexibility, splicing the byte stream without inspecting it. Burned redirectors are swappable at runtime by repointing the listener. No engagement state, no business logic. |
| **Repoint** | Repointing a listener swaps its public endpoint at runtime (`POST /listeners/{id}:repoint`) without touching the Kestrel bind; the old endpoint stops resolving, which severs it. |
| **Build unit** | A per-language compilation service driven by the teamserver through the build contract (.NET in-tree; Go, C/C++, and Nim as out-of-tree community units). |
| **Build contract** | The uniform message schema coupling the teamserver to build units; the language-neutrality boundary for generation. |

## Tasking and capabilities

| Term | Meaning |
|------|---------|
| **Capability** | A verb an implant advertises and the teamserver may dispatch; namespaced (`namespace.action`). |
| **Capability module** | An out-of-tree assembly that registers capability verbs through `ICapabilityModule` (config-listed, last registration wins). Evasion/exploit behavior is delivered only this way. |
| **Recon** | The `recon.portscan`, `recon.hostenum`, `recon.service`, and `recon.ps` verbs (category `Recon`); target and network reconnaissance, plus the local process listing, gated to Stage-2 at task issuance (Sec 5.2). Like the non-shell core verbs their concrete behavior runs on the implants and is captured as task output -- the descriptors and dispatch live in the tradecraft layer. |
| **Lateral** | The `lateral.move`, `lateral.token`, and `lateral.exec_remote` verbs (category `Lateral`); lateral movement within an authorized engagement, gated to Stage-2 at task issuance (Sec 5.2). `lateral.move` is the deployment verb that derives a child implant: the child enrols through the standard enrollment route naming its parent, and the recorded `ParentImplantId` is the parentage linkage. The reference implant implements the standard handlers (child derivation, token context inspection, remote exec over admin channels); the descriptors and dispatch live in the tradecraft layer. |
| **Persist** | The `persist.install`, `persist.remove`, and `persist.list` verbs (category `Persist`); installing, enumerating, and tearing down footholds within an authorized engagement, gated to Stage-2 at task issuance (Sec 5.2). The reference implant implements the standard documented mechanisms (Run key / scheduled tasks / services / cron / systemd); the descriptors and dispatch live in the tradecraft layer. |
| **Collect** | The `collect.cred`, `collect.screenshot`, and `collect.keylog` verbs (category `Collect`); credential, screen, and input collection within an authorized engagement, gated to Stage-2 at task issuance (Sec 5.2). The reference implant implements credential-store enumeration (`collect.cred`) without dumping secret material and screen capture over the standard desktop-capture APIs (`collect.screenshot`, a PNG artifact joined to its task); `collect.keylog` stays contract-only (Sec 13). File transfer is a core verb (`file.push`/`file.pull`). |
| **Exfil** | The `exfil.push` and `exfil.stage` verbs (category `Exfil`); staging collected data on the teamserver and transferring it over the C2 channel within an authorized engagement, gated to Stage-2 at task issuance (Sec 5.2). The reference implant streams files as chunked ExfilChunk frames into the engagement artifact store; the descriptors and dispatch live in the tradecraft layer. |
| **Evasion** | The `evasion.avoid` and `evasion.unload` verbs (category `Evasion`); detection-evasion hooks within an authorized engagement. Unlike the recon, lateral, persist, collect, and exfil verbs these are **not** gated to a class (Sec 5.2, Sec 10.2): evasion is contract and dispatch only, so which class an evasion module runs on is decided when the operator deploys the out-of-tree module. The descriptors and dispatch live in the tradecraft layer; the concrete behavior is out-of-tree (Sec 13) and the core ships no bypass techniques (RESPONSIBLE-USE.md). |
| **Task / Tasking** | An operator-issued request targeting a session; has a state machine, result, and attribution. |

## Evidence and OPSEC

| Term | Meaning |
|------|---------|
| **Audit event** | An immutable, hash-chained, attributed record of a privileged action; the engagement timeline and report source by construction. |
| **Artifact** | A first-class object (file, screenshot, command output) linked to a task; part of the evidence store. |
| **Retire** | Marking an implant retired from the operator API; a retired implant is refused at handshake (`HANDSHAKE_STATUS_IMPLANT_RETIRED`), untaskable, and its active session is closed. Idempotent; recorded as an `ImplantRetired` audit event.
| **Burn handling** | The recovery flow when an implant or endpoint is compromised: retire the implant, repoint (swap) the burned endpoint, and rebuild a fresh artifact with a fresh key. |
| **ROE guardrails** | The engagement's rules-of-engagement profile (`PermittedVerbs`, `PermittedImplants`); the server blocks task issuance outside it at queue time and records the refusal. |
