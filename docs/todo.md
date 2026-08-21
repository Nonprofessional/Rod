# Rod -- Todo

Open work only. An item leaves this file the moment it ships -- its record
is the commit history, and the design it followed lives in
[architecture.md](architecture.md); the one designed-but-deferred item
(sealing) stays in Sec 9. Nothing here is an archive of the done.

Each item names the architecture section it serves and carries a one-line
acceptance criterion (_AC:_), so "done" stays testable. Keep the
[repository conventions](../AGENTS.md): small focused commits, English
only, the offensive-tradecraft boundary (architecture.md Sec 13), and cite
the architecture section, never a historical milestone id, from commit
bodies.

Lean is the standing default, not an afterthought: the established
platforms earn their reach with a small surface, and Rod does the same
(Sec 4's deliberate rejections -- no ASP.NET Identity, no per-engagement
RBAC -- are the house style). An addition must say what an engagement
cannot do without it; refactors, deletions, and answering with docs
instead of code are first-class items here, equal to features.

## Operational readiness (architecture.md Sec 8, Sec 9, Sec 12.1)

- [ ] **Walk a rehearsal engagement on production-shaped infrastructure.**
      Every green test today runs against TestServer and loopback binds, so
      nothing verifies the platform on the shape it will run in: an
      externally provisioned CA (Sec 9), Postgres persistence (Sec 12.1), a
      redirector front with a listener repoint (Sec 8), and evidence
      recovery. Walk one full lifecycle -- enroll, task, collect, restart
      the teamserver mid-engagement, repoint behind a burned redirector,
      tear down, export the report -- and record the procedure as an
      operations runbook rather than new code where possible.
      _AC:_ the lifecycle completes on that infrastructure, the audit chain
      verifies after the restart, and the walked procedure lives in
      docs/operations/.
- [ ] **Review the exposed surface adversarially.** The platform is
      remote-code-execution infrastructure; an operator cannot responsibly
      point it at a client network on the strength of functional tests
      alone. The operator API and the implant-facing endpoints (enroll,
      handshake, beacon) need an adversarial pass against the Sec 9 threat
      model -- auth bypass, engagement-scope escape, payload tampering.
      _AC:_ findings are triaged and no high-severity finding is open on
      either surface.

## Operator UI live-ops views (architecture.md Sec 4.1)

- [ ] **Surface the online roster and listener state in the UI.** Presence
      and the listener list are API-only today: both endpoints exist and
      are tested, but the UI walks neither, so a multiplayer crew tracks
      live implants and listener health by curl -- which breaks the shared
      situational awareness the operator layer exists for (Sec 4.1).
      _AC:_ the UI renders the per-engagement online roster and the
      listener list from the existing presence and listener endpoints,
      with no new API surface.
