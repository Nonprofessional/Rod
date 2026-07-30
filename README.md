# Rod

Rod is an **authorized-use red-team command-and-control (C2) platform** for
red-team operations, penetration tests, and security research. A team of
operators drives a fleet of short-lived, disposable implants on authorized
targets from a central teamserver, reaching hosts behind NAT and firewalls over
implant-initiated connections.

> **Status: design phase.** This repository holds the architecture, roadmap, and
> conventions. No code is implemented yet. See
> [docs/architecture.md](docs/architecture.md) for the blueprint and
> [docs/roadmap.md](docs/roadmap.md) for the build plan.

---

## IMPORTANT: authorized use only

Rod is remote-code-execution infrastructure. It must **only** be used against
systems and networks you own or are **expressly authorized** to test. Unauthorized
use is illegal in most jurisdictions. Before you use any of this, read
[RESPONSIBLE-USE.md](RESPONSIBLE-USE.md).

---

## What it is

Rod is a red-team C2 built from operational needs, not from a device-management
model. An operation is isolated per **Engagement**, with disposable
infrastructure, per-implant OPSEC, and an audit trail that is the source for the
final report.

- **Teamserver** -- a monolithic .NET 10 control-plane kernel with six internal
  layers: core state, transport, payload build pipeline, operator layer, storage
  and audit, and pluggable tradecraft.
- **Build units** -- one per implant language (C#/.NET, Go, C/C++, Nim), driven
  by the teamserver through a uniform build contract. This is what makes implants
  polyglot without coupling them to the teamserver language.
- **Implants** -- short-lived, disposable payloads on targets, untrusted by
  default, each with a unique key and a profile (beacon parameters, kill date,
  transport shape) baked in at generation.
- **Redirectors** -- near-stateless Go forwarders that front listeners for OPSEC
  and infrastructure flexibility; burned redirectors are swappable.
- **Operator UI** -- the web front end operators use to run an engagement.

Operations are organized around an **Engagement** -- the unit of tenancy,
isolation, authorization, and evidence. Implants enrol into exactly one
engagement; all data, tasking, artifacts, and audit records are
engagement-scoped. Cross-engagement access is impossible by construction.

## Design goals

- **Designed for the red-team lifecycle**: infrastructure stand-up, payload
  generation, beaconing, post-exploitation, lateral movement, exfiltration, and a
  report/evidence deliverable.
- **OPSEC as a first-class axis**: per-implant beacon profiles with jitter, kill
  dates, per-implant keys, malleable transport profiles, and disposable
  infrastructure.
- **Polyglot implants from one control plane**: a .NET teamserver driving
  implants in C#/.NET, Go, C/C++, or Nim through decoupled per-language build
  units.
- **Make the protocol the product**: a stable, language-neutral, transport-
  agnostic contract so implants can be implemented independently in whatever
  language fits the target.
- **Operation isolation and evidence**: per-engagement isolation, least
  privilege, multiplayer collaboration, and a complete, tamper-evident audit
  trail that is the source for the final report.

## Non-goals (initial)

- Rod commands authorized targets; it is not a general-purpose PaaS or
  orchestration platform.
- Delivery (phishing, host interaction, etc.) is out of scope; Rod ingests the
  first callback and correlates it to the engagement.
- Detection-evasion and exploit behavior are **pluggable capability contracts**:
  the core defines their interfaces and dispatch and provides no concrete bypass
  techniques or in-the-wild PoCs; tradecraft lives in separate, opt-in modules.

## Technology

| Component | Stack | Notes |
|-----------|-------|-------|
| Teamserver | .NET 10 (LTS), ASP.NET Core, gRPC | Monolithic kernel, six internal layers. |
| Data store | PostgreSQL | Authoritative state and per-engagement audit. |
| Build units | C#/.NET, Go, C/C++, Nim toolchains | One per implant language; driven by the build contract. |
| Redirectors | Go (latest stable) | Single static binary; easy VPS deploy. |
| Implants | C#/.NET, Go, C/C++, Nim -- per target | Short-lived, disposable, per-implant keys. |
| Operator UI | Web (Blazor or React) | Lives in the teamserver project. |

See [docs/decisions/0001-stack-and-architecture.md](docs/decisions/0001-stack-and-architecture.md)
for the rationale behind these choices.

## Documentation

Start here:

- **[docs/architecture.md](docs/architecture.md)** -- the system blueprint:
  operational lifecycle, the engagement model, the monolithic-kernel layers,
  implants and profiles, the build pipeline, OPSEC, transports, security, and the
  sensitive-capability boundary.
- **[docs/roadmap.md](docs/roadmap.md)** -- milestones and the ordered build plan.
- **[docs/glossary.md](docs/glossary.md)** -- terminology.
- **[docs/decisions/](docs/decisions/)** -- architecture decision records.

## License

Licensed under the [Apache License, Version 2.0](LICENSE).
