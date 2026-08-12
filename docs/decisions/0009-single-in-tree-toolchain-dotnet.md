# ADR 0009 -- Single in-tree toolchain: .NET for implant and redirector

- **Status:** Accepted
- **Date:** 2026-08-12
- **Related:** Supersedes the polyglot-in-tree and Go-redirector portions of
  [ADR 0001](0001-stack-and-architecture.md); [architecture.md](../architecture.md)
  Sec 3, Sec 6, Sec 8; [ADR 0004](0004-offensive-tradecraft-boundary.md) (the
  sensitive-capability boundary, unchanged)

## Context

ADR 0001 chose a polyglot implant story -- per-language build units
(C#/.NET, Go, C/C++, Nim) and a Go redirector -- on the principle that the
implant language should fit the target. That principle is sound for a
production C2, and the build contract (the language-neutral wire protocol,
Sec 6) keeps the teamserver decoupled from any one implant language.

In practice the team maintained two full in-tree reference implants (Go and
.NET) in lockstep: every verb shipped twice, every fix applied twice. The two
implants reached feature parity, and the teamserver carried two real build
units (`GoBuildUnit`, `DotNetBuildUnit`) plus their tests. The cost was real
and recurring, and the teamserver is already .NET 10, so the team lives in one
toolchain already for the control plane.

Two facts change the tradeoff ADR 0001 weighed:

1. **.NET is cross-platform.** `dotnet publish -r <rid> --self-contained`
   produces a native binary for Linux, Windows, and macOS from one codebase, so
   a single .NET implant covers the cross-platform reach ADR 0001 assigned to
   Go. "Cross-platform" no longer requires a second language.
2. **.NET Native AOT covers the redirector rationale.** ADR 0001 picked Go for
   redirectors because a static, single-binary forwarder is trivial to deploy
   to a VPS. Native AOT now produces an equivalent single-file native binary
   from .NET, so the property ADR 0001 wanted -- tiny VPS footprint, no runtime
   install -- is available without leaving the toolchain.

## Decision

Rod ships a **single in-tree toolchain end to end: .NET 10.**

- **Reference implant: .NET only.** The in-tree reference implant is the .NET
  implant under `implant-dotnet/`. It is the sole reference implant the project
  maintains. The Go reference implant and its build unit are removed.
- **Redirector direction: .NET Native AOT.** No redirector ships in-tree yet
  (Sec 8). When one does, it is a .NET Native AOT forwarder, not a Go binary.
- **Polyglot by contract, not by in-tree parity.** The wire protocol
  (`src/Rod.Protocol/protos/rod.proto`) remains the language-neutral product.
  The build contract and the `Language` enum (Go/DotNet/C/Nim) stay, so an
  out-of-tree community implant in Go, C, or Nim can register a build unit and
  compile against the same contract. The project no longer maintains in-tree
  reference implants for more than one language; additional languages arrive as
  external, opt-in modules a contributor supplies.

This narrows ADR 0001's polyglot stance from "the project ships reference
implants in several languages" to "the project ships one .NET reference implant
and keeps the contract open for the rest." It supersedes the Go-redirector
choice outright.

## Rationale

- **One toolchain for a .NET-centric team.** The teamserver is .NET 10; making
  the implant and the redirector .NET too leaves a single language, SDK, and
  build pipeline to know and maintain. The doubling of every verb's cost goes
  away.
- **.NET covers cross-platform reach on its own.** Self-contained publishes
  target Linux/Windows/macOS from one source, so the "language fits the target"
  argument does not force a second in-tree language just for reach.
- **AOT matches the redirector's original ask.** A static, single-binary,
  no-runtime forwarder was the whole reason ADR 0001 reached for Go on the
  edge; Native AOT delivers that from .NET.
- **The contract keeps polyglot open.** Because the wire protocol is the
  product and the build contract is language-neutral, dropping the in-tree Go
  implant costs no architectural option: a community Go/C/Nim implant registers
  a build unit and participates the same way the in-tree units did.

## Consequences

- **Positive:** one in-tree toolchain end to end; the implant and teamserver
  share one proto, one language, and one build pipeline; per-verb work lands
  once; CI drops the Go toolchain job.
- **Positive:** polyglot capability is preserved as an opt-in contract path, so
  the framework stays useful for targets the .NET implant does not fit.
- **Negative:** the .NET implant carries a larger self-contained footprint than
  a Go static binary, and the CLR/assembly-load surface is more heavily
  instrumented by Windows AV/EDR (AMSI, ETW). For the reference/learning
  posture (RESPONSIBLE-USE.md, Sec 7) this is acceptable.
- **Mitigation:** Native AOT shrinks the implant and removes the CLR surface
  where its reflection/dynamic-load constraints are acceptable; where they are
  not (e.g. Windows in-memory .NET tradecraft), the AOT tradeoff is real, and
  that class of tradecraft is expected to arrive as an out-of-tree community
  implant in the language that fits it, per the contract this ADR keeps open.
- **Neutral:** the `Language.Go` enum member and the `StubBuildUnit`
  (registered under `Language.Go` as a contract-reference test double) remain;
  they test the build/registry plumbing, not the Go language, and they keep the
  contract path exercisable in unit tests without a Go toolchain.

## Alternatives considered

- **Keep both reference implants in lockstep (status quo, ADR 0001).**
  Maximizes tradecraft fit at the cost of writing and maintaining every verb
  twice. Rejected: the maintenance cost is recurring and the cross-platform
  argument no longer requires it.
- **Collapse to Go instead of .NET.** Go is cross-platform and produces small
  static binaries. Rejected: the control plane is .NET 10, and standardizing on
  .NET keeps the whole stack in one toolchain; the .NET implant already shares
  the teamserver's proto and build tooling.
- **Asymmetric polyglot: .NET full, a second language specialist only.** Keep a
  second in-tree implant but stop forcing feature parity. Rejected as the
  primary path: it still leaves a second toolchain to build and test in CI for
  a small team, and the contract already gives community implants an opt-in
  path without the project carrying the second tree.
