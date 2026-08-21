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
