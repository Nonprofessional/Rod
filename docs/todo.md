# Rod -- Todo

Open work only. An item leaves this file the moment it ships -- its record
is the commit history, and the design it followed lives in
[architecture.md](architecture.md); the one designed-but-deferred item
(sealing) stays in Sec 9. Nothing here is an archive of the done.

Each item names the architecture section it serves and carries a one-line
acceptance criterion (_AC:_), so "done" stays testable. Follow the
[repository conventions](../AGENTS.md).

Lean is the standing default: an addition must say what an engagement
cannot do without it; refactors, deletions, and answering with docs
instead of code are first-class items here, equal to features. New work
starts from a gap an actual engagement surfaces.

- [ ] **Push live updates to the operator UI.** The roster and tasking
      views refresh on polling intervals, so check-ins and task results
      surface only on the next tick during a live engagement. Stream
      roster and tasking deltas over SSE so operators watch the engagement
      as it happens.
      _AC:_ an operator sees an implant's check-in and its task result in
      the UI without a manual refresh and without waiting out a poll
      interval.
