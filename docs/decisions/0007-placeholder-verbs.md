# ADR 0007 -- Placeholder verbs: register everything, execute nothing in-repo

- **Status:** Superseded -- folded into [architecture.md](../architecture.md)
- **Date:** 2026-08-11
- **Related:** [ADR 0004](0004-offensive-tradecraft-boundary.md) (which verbs
  carry no in-repo handler)
- **Superseded by:** [architecture.md §10.2 (Sensitive-capability
  boundary)](../architecture.md)

> This ADR's decision and rationale now live in the expanded §10.2 of
> architecture.md. This file is kept as a tombstone so existing links and git
> history resolve; the canonical text is the architecture section.

**What it decided:** Every built-in verb is registered in the default registry,
contract-only ones as real `PlaceholderCapabilityModule`s that satisfy the gate
and return `Failed` (never `NotFound`) until an operator supplies a module;
there is no runtime assembly loader (out-of-tree is compile-in-and-register,
bounding the teamserver's runtime attack surface by its compile-time inputs);
and the server only gates and forwards on the live task path -- execution stays
on the implant, never in a server-side module invocation.
