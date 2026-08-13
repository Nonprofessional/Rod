# ADR 0005 -- Task arguments stay a single opaque string

- **Status:** Superseded -- folded into [architecture.md](../architecture.md)
- **Date:** 2026-08-11
- **Superseded by:** [architecture.md §10 (Capability model and tasking)](../architecture.md)

> This ADR's decision and rationale now live in the "arguments stay a single
> opaque string" paragraph of architecture.md §10. This file is kept as a
> tombstone so existing links and git history resolve; the canonical text is the
> architecture section.

**What it decided:** Task arguments stay a single opaque `string` at every
contract boundary (proto, core state, transport DTO, dispatch contract, implant
entrypoint). The verb is the typed discriminator; the string is the verb's own
grammar, parsed by the handler that owns it; and the escape hatch is a per-verb
typed proto arm (not a global structured-arguments field) for the rare verb
whose grammar outgrows a string. A shared typed-argument schema was rejected
because the grammar is per-verb, not per-system.
