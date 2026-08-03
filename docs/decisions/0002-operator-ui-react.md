# ADR 0002 -- Operator UI: React

- **Status:** Accepted
- **Date:** 2026-08-03

## Context

ADR 0001's stack table left the operator UI as "Web (Blazor or React), in the
teamserver project." Both were acceptable against the documented constraints
(web front end, lives in the single teamserver process), and the choice was
deferred. The M1.5 "minimal operator UI" milestone forces the decision: the
walking skeleton needs a concrete front end.

## Decision

The operator UI is **React** (TypeScript), built with Vite and served same-origin
by the teamserver host. The React sources live in
`src/Rod.TeamServer/Client/`; the production build emits into the host's
`wwwroot/`, which `Rod.TeamServer` serves as static files, with a fallback to
`index.html` so the client owns deep links. In development, Vite's dev server
proxies the operator HTTP API back to the host, so the browser sees one origin.

## Consequences

- A Node toolchain (npm/Vite) is now part of the build. CI builds the UI
  (`npm ci && npm run build`) before `dotnet build` so the bundle is in
  `wwwroot/` when the .NET host is packaged. The build output is gitignored.
- The UI talks to the operator HTTP API over `fetch`; it cannot inject .NET
  services directly. That is the right boundary for a browser SPA, and it keeps
  the operator API honest as the single integration point a future external UI
  would also use.
- We forgo Blazor's .NET-native story (no second toolchain, server-component
  reuse of services). The trade is a browser-native SPA with the larger React
  ecosystem and an audience that more often knows React than Blazor. Same-origin
  hosting still keeps the single-process, single-deployment model intact.
- Operator auth (M2.4), real-time push over websockets, and a richer console are
  later work; M1.5 ships a minimal read-and-task UI with light polling.
