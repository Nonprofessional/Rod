# ADR 0002 -- Operator UI: React

- **Status:** Superseded -- folded into [architecture.md](../architecture.md)
- **Date:** 2026-08-03
- **Superseded by:** [architecture.md §12 (Technology stack, "Operator UI"
  row)](../architecture.md)

> This ADR's decision and rationale now live in the Operator UI row of
> architecture.md §12. This file is kept as a tombstone so existing links and
> git history resolve; the canonical text is the architecture section.

**What it decided:** The operator UI is React (TypeScript) built with Vite and
served same-origin by the teamserver host (sources in
`src/teamserver/Rod.TeamServer/Client/`, built into `wwwroot/` with an SPA
fallback, Vite's dev server proxying the operator API in development), chosen
over Blazor for the larger React ecosystem and audience reach at the cost of a
Node/Vite CI step and the loss of Blazor's .NET-native service reuse.
