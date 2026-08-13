# ADR 0001 -- Stack and architecture

- **Status:** Superseded -- folded into [architecture.md](../architecture.md)
- **Date:** 2026-07-30
- **Superseded by:** [architecture.md §4 (Component architecture) and §12
  (Technology stack)](../architecture.md)

> This ADR's decision and rationale now live in architecture.md §4 (the
> monolith-vs-microservices and polyglot-build-contract rationale) and §12 (the
> stack table). This file is kept as a tombstone so existing links and git
> history resolve; the canonical text is the architecture sections.

**What it decided:** Rod is a monolithic .NET teamserver with strong internal
logical layering, plus decoupled per-language build units and polyglot implants.
The monolith was chosen over a container-per-concern split for blast-radius and
operational simplicity, and a uniform build contract was chosen over a single
implant language so C#/.NET, Go, C/C++, and Nim payloads compile from one
language-agnostic control plane.

Two parts of the original ADR moved to later ADRs rather than into
architecture.md: the tradecraft-boundary section was superseded by
[ADR 0004](0004-offensive-tradecraft-boundary.md), and the Go edge it assumed
was superseded by [ADR 0009](0009-single-in-tree-toolchain-dotnet.md).
