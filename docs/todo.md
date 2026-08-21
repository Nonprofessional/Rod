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

## Operator experience (architecture.md Sec 3, Sec 10)

- [ ] **Operator notes on implants.** Free-text, attributed notes per
      implant -- the "whose beacon is this" memory every mainstream client
      carries -- recorded as audit events and rendered in the client.
      _AC:_ a note added in the client survives a teamserver restart via
      the audit store and shows on the implant view.
- [ ] **Cancel queued tasking before dispatch.** An operator can retract a
      queued task before the implant wakes; the cancellation is audited
      and the dispatch wake skips it. _AC:_ a cancelled queued task is
      never delivered and appears in the audit trail as cancelled.

## Lean surface (architecture.md Sec 4, Sec 14)

- [ ] **Audit the shipped surface for deletions.** Walk the verb catalog,
      the operator API endpoints, and the configuration keys against the
      tests and the docs; anything nothing exercises or documents gets
      deleted rather than carried. _AC:_ every verb, endpoint, and config
      key either has an exercising test or a documented consumer, or it is
      removed.
