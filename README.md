<p align="center"><img src="docs/assets/rod-logo.png" alt="Rod" width="200"></p>

# Rod

Rod is an **authorized-use red-team command-and-control (C2) platform** for
red-team operations, penetration tests, and security research. A team of
operators drives a fleet of short-lived, disposable implants on authorized
targets from a central teamserver, reaching hosts behind NAT and firewalls over
implant-initiated connections.

> **Status: the framework is implemented; concrete tradecraft is out-of-tree.**
> The teamserver, reference implant, build pipeline, operator UI, and durable
> state are in place across the six internal layers. The capability contracts
> are wired and dispatched; concrete offensive behavior is supplied as separate,
> opt-in modules. See [docs/architecture.md](docs/architecture.md) for the
> blueprint, [docs/roadmap.md](docs/roadmap.md) for the archived (complete)
> milestone plan, and [docs/todo.md](docs/todo.md) for open work.

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
- **Build units** -- per-language units driven by the teamserver through a
  uniform, language-neutral build contract. .NET ships in-tree; Go, C/C++, and
  Nim arrive as out-of-tree community units against the same contract, so
  implants stay polyglot without coupling them to the teamserver language.
- **Implants** -- short-lived, disposable payloads on targets, untrusted by
  default, each with a unique key and a profile (beacon parameters, kill date,
  transport shape) baked in at generation.
- **Redirectors** -- near-stateless forwarders that front listeners for OPSEC and
  infrastructure flexibility; the in-tree direction is a .NET Native AOT single
  binary, and burned redirectors are swappable.
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
- **Polyglot implants from one control plane**: a .NET teamserver driving a .NET
  reference implant, with the language-neutral build contract open to out-of-tree
  Go, C/C++, or Nim implants for targets .NET does not fit.
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
| Build units | .NET (in-tree); Go/C/C++/Nim out-of-tree | One in-tree toolchain; polyglot by contract, no teamserver-language coupling (architecture.md Sec 12.2). |
| Redirectors | .NET Native AOT, single static binary | Tiny VPS footprint, no runtime install; burned redirectors swappable (architecture.md Sec 8). |
| Implants | .NET (reference); Go/C/C++/Nim out-of-tree -- per target | Short-lived, disposable, per-implant keys. |
| Operator UI | Web (React) | Lives in the teamserver project; served same-origin. |

See [docs/architecture.md](docs/architecture.md) for the rationale behind these
choices.

## Documentation

Start here:

- **[docs/architecture.md](docs/architecture.md)** -- the system blueprint:
  operational lifecycle, the engagement model, the monolithic-kernel layers,
  implants and profiles, the build pipeline, OPSEC, transports, security, and the
  sensitive-capability boundary.
- **[docs/roadmap.md](docs/roadmap.md)** -- the archived milestone plan (M0.1
  through M11.1, all complete); kept as the historical acceptance-criteria
  record.
- **[docs/todo.md](docs/todo.md)** -- post-roadmap work: implant verb coverage,
  production hardening, and architecture-gap audits.
- **[docs/glossary.md](docs/glossary.md)** -- terminology.

## License

Licensed under the [Apache License, Version 2.0](LICENSE).
