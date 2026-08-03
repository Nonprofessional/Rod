# ADR 0001 -- Stack and architecture

- **Status:** Accepted
- **Date:** 2026-07-30

## Context

Rod is an authorized-use red-team command-and-control (C2) platform: a team of
operators commands a fleet of short-lived, disposable implants on authorized
targets from a central teamserver.

We must choose the technology stack and component architecture before any code
is written. The design priorities that drive it (per-implant OPSEC, a
hash-chained audit trail as the report source, engagement-scoped isolation,
disposable infrastructure) are established in
[architecture.md Sec. 1](../architecture.md); this ADR records the *stack*
consequences.

One constraint rises above the rest here: **the control plane and the implants
have different requirements.** The control plane is a long-lived, stateful,
security-critical service (orchestration, crypto, listeners, registry, RBAC,
audit, UI). Implants are target-resident payloads whose language is dictated by
the target and the objective, not by the control plane. No single language is
best at both.

## Decision

Rod is a **monolithic teamserver with strong internal logical layering**, plus
decoupled per-language build units and polyglot implants. The teamserver is a
single .NET process holding the core; polyglot needs are met by decoupling at the
build boundary, not by splitting the whole system into microservices.

| Concern | Choice |
|---------|--------|
| Teamserver (monolithic kernel) | .NET 10 (LTS), ASP.NET Core, gRPC |
| Data store | PostgreSQL |
| Build units | One per implant language (C#/.NET, Go, C/C++, Nim) |
| Redirectors | Go (latest stable), static single binary |
| Implants | C#/.NET, Go, C/C++, Nim -- per target |
| Operator UI | Web (React), in the teamserver project (see ADR 0002) |

The six internal layers of the teamserver are: core state, transport, payload
build pipeline, operator layer, storage and audit, and pluggable tradecraft. See
[architecture.md Sec. 4](../architecture.md).

Implants are built through a **uniform build contract**: on a payload request the
teamserver sends build params and the relevant language's build unit returns a
compiled artifact. The teamserver language and the implant language are coupled
only by that contract.

The architecture is driven by the red-team operational lifecycle (planning,
infrastructure, generation, beaconing, tasking, lateral, exfiltration, reporting,
cleanup) and the engagement is the isolation unit. See
[architecture.md Sec. 2-3](../architecture.md).

## Rationale

- **Monolithic kernel.** A single process is simpler to deploy, has low
  inter-component latency, and a single state model. It suits a small team and a
  security-critical core that should have one blast radius. The downside --
  harder to hot-swap components -- is acceptable because the parts that change
  most (implant builds, transports, tradecraft) are already decoupled as build
  units, redirectors, and capability modules.
- **Per-language build units.** This is the proven way to compile polyglot
  implants from one control plane. Each language keeps its own toolchain; the
  teamserver stays language-agnostic at the build boundary. It costs more moving
  parts than in-process compilation but is the only clean path to C# + Go + C/C++
  + Nim.
- **.NET 10 for the teamserver.** Strong async networking (Kestrel), first-class
  gRPC, strong typing, a mature web UI story, and LTS support to ~2028. .NET 10
  is the current LTS (the previous LTS, .NET 8, and the STS, .NET 9, are both at
  end-of-life).
- **Go for redirectors.** A static, single-binary forwarder is trivial to deploy
  to a VPS and ships mTLS/HTTP/DNS from the standard library, keeping the
  OPSEC-sensitive edge lightweight.

## Consequences

- **Positive:** one deployable core with clear internal layers; polyglot implants
  without teamserver-language coupling; OPSEC, evidence, and engagement
  isolation are structural rather than bolted on.
- **Negative:** a build contract plus per-language build units is more moving
  parts than a single-language stack; a stable payload-build message schema must
  be defined and maintained; the monolithic core means component-level scaling is
  limited (acceptable for the target use).
- **Risk:** a .NET teamserver is uncommon in the C2 space. Mitigation: keep the
  wire protocol the product (stable, language-neutral), and keep all implant
  toolchains independent of the teamserver language via the build contract.

## Alternatives considered

- **Microservices / container-per-concern.** Decouple agent definition,
  transport profile, translation, and audit sink into separately versioned
  components. More future-proof for a polyglot, evasion-first platform, but
  heavier to operate and secure. Rejected for now: the monolithic kernel can
  enforce the same *logical* boundaries internally, and polyglot needs are met at
  the build boundary. The logical layering is preserved so a future move toward
  services stays open.
- **Single implant language.** Simpler, but forces one language onto every
  target class -- unacceptable for a red-team tool where Windows in-memory
  tradecraft, cross-platform reach, and small footprint each demand a different
  language.

## Sensitive-capability boundary

Evasion and exploit capabilities are part of the capability model as pluggable
contracts only. The core defines their interfaces, registration, and dispatch;
concrete bypass techniques and in-the-wild PoCs are intentionally out of scope
and live in separate, opt-in, out-of-tree modules. See
[architecture.md Sec. 10.2, Sec. 13](../architecture.md).
