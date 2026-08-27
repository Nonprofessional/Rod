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

Production use is the driver now: the three items this file last carried
-- the rehearsal engagement, the adversarial surface review, and the
live-ops UI views -- shipped, their records are the commits and
[operations/rehearsal.md](operations/rehearsal.md), and what remains is
what stands between that single-host rehearsal and pointing Rod at a
client network. Nothing below grows framework surface; they answer with
infrastructure, procedure, and defaults.

## Pre-production (architecture.md Sec 7, Sec 8, Sec 12.1, Sec 13)

- [ ] **Pin the per-engagement readiness gate.** Facing a client network
      raises decisions the framework deliberately leaves to the crew --
      credential custody, evidence retention, who authorizes -- and the
      compressed rehearsal needs a standing rule attached to it
      (RESPONSIBLE-USE.md, Sec 13's boundary). Record the defaults and a
      short checklist in RESPONSIBLE-USE.md or a brief operations page:
      the gate an engagement lead walks before first check-in, anchored
      on the rehearsal doc's lifecycle walk as the compressed form.
      _AC:_ the gate exists as a checked-in checklist tied to the
      rehearsal walk, takes under thirty minutes, and every default it
      names agrees with the Sec 13 boundary and RESPONSIBLE-USE.md.

