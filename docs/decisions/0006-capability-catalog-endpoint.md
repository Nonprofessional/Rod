# ADR 0006 -- Capability catalog endpoint lives in the tradecraft layer

- **Status:** Superseded -- folded into [architecture.md](../architecture.md)
- **Date:** 2026-08-11
- **Superseded by:** [architecture.md §4.3 (Source-tree map, layering
  consequence note)](../architecture.md)

> This ADR's decision and rationale now live in the layering-consequence note
> after the §4.3 source-tree map. This file is kept as a tombstone so existing
> links and git history resolve; the canonical text is the architecture section.

**What it decided:** `GET /capabilities` lives in `Rod.Tradecraft` itself (not
transport), mapped at the composition root the same way `Rod.Operators` maps its
SSE endpoint, because transport may not depend on tradecraft and the catalog is
process-global registry metadata (not engagement-scoped domain state) -- so it
earns no CoreState port and no parallel DTO. An engagement-scoped capability
concern would be a separate endpoint, not a retrofit onto the global catalog.
