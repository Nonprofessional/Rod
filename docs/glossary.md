# Rod -- Glossary

A quick reference for the terms used across docs/architecture.md and the
codebase. Definitions here are summaries; authoritative detail is in the linked
sections.

## Engagement and identity

| Term | Meaning |
|------|---------|
| **Engagement** | The unit of tenancy, isolation, authorization, and evidence -- one authorized operation. All domain state is engagement-scoped and disposable with the operation. |
| **Operator** | A global human identity; an authenticated user of the platform. Any authenticated operator can operate on any engagement; accountability is through the attributed audit trail. |
| **Deploy token** | An engagement-scoped, short-lived, bounded-use secret used only during initial enrollment/deployment. |

## Implants and sessions

| Term | Meaning |
|------|---------|
| **Implant** | A short-lived, disposable payload on a target host. Untrusted by default; generates its own keypair (identity bound by the CA-signed leaf at enroll -- no key material ships in the artifact). Speaks the wire protocol. |
| **Session** | The implant's live channel in an engagement -- not one TCP connection. Reconnects (poll contacts, flapped streams) reuse it; the staleness sweeper or retirement closes it. Online means "seen within the staleness threshold". |
| **Beacon** | The implant's contact over the reverse stream. Two modes are baked per implant: `stream` (persistent connection, interactive) and `poll` (drain tasking, close, sleep the interval with jitter -- the periodic low-and-slow shape). |
| **Beacon profile** | The per-implant contact mode, sleep, jitter, and kill-date parameters, baked into the artifact at generation. |
| **Kill date** | A hard self-termination timestamp baked in per implant; limits exposure if lost. |
| **Malleable profile** | A configurable transport shape (URIs, headers, timing, payload) that mimics legitimate traffic, per implant. |
| **Pinned trust** | The default TLS posture: the engagement CA baked at build is the only root the artifact's dials trust. No public-PKI or target-store dependence; no public CA can mint an accepted identity. |
| **Public trust** | The real-domain TLS posture (`Trust: public`): the front's certificate a public CA issued, terminated at an operator-run edge; the artifact validates it like an ordinary client against the compiled-in Mozilla root set. |

## Implant classes

| Term | Meaning |
|------|---------|
| **Implant class** | The primary long-haul implant; full capability set and module support. The wire class value is `Implant`. |
| **Stager** | Retired: a stage-1 loader that fetched the implant as a separate build output. Delivery rides the launcher one-liners now. |
| **Web-shell class** | A script in a web root, bound to the web transport; code execution over HTTP, no interactive PTY. |
| **Ephemeral** | A short-lived, TTL'd implant from a one-liner bootstrap; one-off execution and temporary access. |
| **Pivot** | An implant representing hosts that cannot run their own implant, enrolling each as its own session and forwarding tasking. |

## Infrastructure

| Term | Meaning |
|------|---------|
| **Teamserver** | The monolithic .NET control-plane kernel: core state, transport, build pipeline, operator layer, storage/audit, tradecraft. |
| **Listener** | The ingress endpoint that terminates a C2 transport (HTTP(S), DNS, TCP, DoH; plus `shellcatch`, the kind that catches a raw reverse-shell one-liner and hands back upgrade lines). Decoupled from the public endpoint. |
| **Redirector** | A near-stateless .NET Native AOT forwarder (a single static binary) that fronts a listener for OPSEC and infra flexibility, splicing the byte stream without inspecting it. Burned redirectors are swappable at runtime by repointing the listener. No engagement state, no business logic. |
| **Repoint** | Repointing a listener swaps its public endpoint at runtime (`POST /engagements/{engagementId}/listeners/{id}:repoint`) without touching the Kestrel bind; the old endpoint stops resolving, which severs it. |
| **DoH** | DNS-over-HTTPS: the DNS grammar carried over RFC 8484 HTTPS bodies -- one of the five in-tree transports, and the resolution carrier the recon workbench's `recon:resolve` rides. |
| **Build unit** | A per-language compilation service driven by the teamserver through the build contract (Rust in-tree; Go, C/C++, and Nim as out-of-tree community units). |
| **Build contract** | The uniform message schema coupling the teamserver to build units; the language-neutrality boundary for generation. |

## Tasking and capabilities

| Term | Meaning |
|------|---------|
| **Capability** | A verb an implant advertises and the teamserver may dispatch; namespaced (`namespace.action`). |
| **Capability module** | An out-of-tree assembly that registers capability verbs through `ICapabilityModule` (config-listed, last registration wins). Evasion/exploit behavior is delivered only this way. |
| **Recon** | The `recon.portscan`, `recon.hostenum`, `recon.service`, and `recon.ps` verbs (category `Recon`); target and network reconnaissance, plus the local process listing, gated to the Implant class at task issuance (Sec 5.2). Their execution is long-tail (Sec 13): it runs on an artifact whose tree carries the handlers, captured as task output; the descriptors and dispatch live in the tradecraft layer. |
| **Lateral** | The `lateral.move`, `lateral.token`, and `lateral.exec_remote` verbs (category `Lateral`); lateral movement within an authorized engagement, gated to the Implant class at task issuance (Sec 5.2). `lateral.move` is the deployment verb that derives a child implant: the child enrols through the standard enrollment route naming its parent, and the recorded `ParentImplantId` is the parentage linkage. The handlers are long-tail like the recon set; the descriptors and dispatch live in the tradecraft layer. |
| **Persist** | The `persist.install`, `persist.remove`, and `persist.list` verbs (category `Persist`); installing, enumerating, and tearing down footholds within an authorized engagement, gated to the Implant class at task issuance (Sec 5.2). The verbs speak the documented mechanisms (Run key / scheduled tasks / services / cron / systemd); the handlers are long-tail (Sec 13), and the descriptors and dispatch live in the tradecraft layer. |
| **Collect** | The `collect.cred`, `collect.screenshot`, and `collect.keylog` verbs (category `Collect`); credential, screen, and input collection within an authorized engagement, gated to the Implant class at task issuance (Sec 5.2). `collect.cred` and `collect.screenshot` are long-tail handlers (Sec 13); `collect.keylog` ships Windows-gated in the reference crate, alongside `collect.minidump`. File transfer is a core verb (`file.push`/`file.pull`). |
| **Exfil** | The `exfil.push` and `exfil.stage` verbs (category `Exfil`); staging collected data on the teamserver and transferring it over the C2 channel within an authorized engagement, gated to the Implant class at task issuance (Sec 5.2). The chunked-`ExfilChunk` machinery their bulk rides is core (it is how large `file.pull`s stream into the engagement artifact store); the verbs' handlers are long-tail (Sec 13), and the descriptors and dispatch live in the tradecraft layer. |
| **Module family** | The `module.load`, `module.unload`, and `module.list` verbs (category `Module`); the implant-side plugin seam's own machinery (Sec 5.4), gated to the Implant class. `module.load` delivers an SDK module's bytes as staged task content and the loader registers its verbs -- on Linux a close-on-exec memfd and an `execveat` through the anonymous fd, one process per dispatch (the static musl artifact included); on Windows a manual PE map, which also carries the resident collectors. The module's verbs join the dispatch table and the next contact's advertisement. The crate fork remains the compile-time path for handlers. |
| **Evasion** | The `evasion.avoid` and `evasion.unload` verbs (category `Evasion`); detection-evasion hooks within an authorized engagement. Unlike the recon, lateral, persist, collect, and exfil verbs these are **not** gated to a class (Sec 5.2, Sec 10.2): evasion is contract and dispatch only, so which class an evasion module runs on is decided when the operator deploys the out-of-tree module. The descriptors and dispatch live in the tradecraft layer; the concrete behavior is out-of-tree (Sec 13) and the core ships no bypass techniques. |
| **Task / Tasking** | An operator-issued request targeting a session; has a state machine, result, and attribution. |

## Operator surfaces

| Term | Meaning |
|------|---------|
| **Workbench** | The external recon workbench (Sec 11.4): the operator layer's pre-foothold surface -- passive RDAP, certificate-transparency, DoH, and whois lookups plus an ROE-gated port scan, run on the teamserver, engagement-scoped, findings landing as task-less artifacts. Each half stays closed until its egress endpoint is configured. |
| **MCP server** | The read-only Model Context Protocol endpoint (`/mcp`) riding the operator front (Sec 4): six tools over the engagement's read side -- engagements, implants, sessions, tasks, audit -- authenticated by an operator API token under the console's scopes. |
| **Operator API token** | A bearer credential minted per operator through the operator API (shown once, stored as a digest, revocable by its own route); authenticates non-console clients -- today the MCP endpoint -- as the principal a console session would carry. Distinct from the deploy token, which enrolls implants. |
| **Triage** | The opt-in LLM client's job (Sec 11.3): summarize a completed task's captured output through any OpenAI-compatible endpoint (cloud or local) from the task read. Disabled until configured; every attempt audited as `LlmSummaryGenerated`. |

## Evidence and OPSEC

| Term | Meaning |
|------|---------|
| **Audit event** | An immutable, hash-chained, attributed record of a privileged action; the engagement timeline and report source by construction. |
| **Artifact** | A first-class object (file, screenshot, command output, findings) in the evidence store; usually linked to its producing task, but pre-foothold workbench findings land task-less under the acting operator's attribution. |
| **Label** | The marker vocabulary on implants and hosts ("jump", "owned", "watch-edr"; Sec 11.2): setting and clearing append attributed audit events, and the live set is the last-wins reduction over them -- no store, the trail is the storage. |
| **Host picture** | The device dimension read-side (Sec 11.2): a host is the enrollment-hostname grouping (case-insensitive), never an entity; notes and labels on one ride the trail keyed on the normalized hostname. |
| **Loot** | The typed view over the artifact store (Sec 11.2): artifacts classified by the producing verb and content type into screenshot, credential, and file, each entry carrying its capture attribution; retrieving bytes records an `ArtifactViewed` event. |
| **Topology** | The engagement's network picture assembled at read time (Sec 11.2): enrollment grouping, pivot links from recorded parentage, and recon observations parsed from completed task outputs and the workbench's findings artifacts against the documented JSON-lines grammar. Nothing stored. |
| **Retire** | Marking an implant retired from the operator API; a retired implant is refused at handshake (`HANDSHAKE_STATUS_IMPLANT_RETIRED`), untaskable, and its active session is closed. Idempotent; recorded as an `ImplantRetired` audit event.
| **Burn handling** | The recovery flow when an implant or endpoint is compromised: retire the implant, repoint (swap) the burned endpoint, and rebuild a fresh artifact with a fresh key. |
| **ROE guardrails** | The engagement's rules-of-engagement profile (`PermittedVerbs`, `PermittedImplants`, `PermittedTargets`); the server blocks task issuance -- and the workbench's scan -- outside it and records the refusal (`TaskRoeRefused`, `ReconScanRefused`). |
