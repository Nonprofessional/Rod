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

## Close-out and release (architecture.md Sec 2, Sec 11)

- [ ] **Export and verify the engagement evidence package.** The report
      endpoint verifies the live chain, but nothing exports the evidence a
      finished engagement must leave behind: the hash-chained audit trail,
      the artifacts, and the report, as one package that survives
      infrastructure teardown (Sec 14) and re-verifies offline. Add the
      close-out path: freeze the engagement, export the package, verify it
      against the chain, then retire the engagement.
      _AC:_ a closed engagement's exported package re-verifies byte-exact
      on a host with no Rod infrastructure running.

## Operational quality (architecture.md Sec 4, Sec 5)

- [ ] **Run the implant end-to-end suite on a Windows runner.** CI proves
      the .NET implant on ubuntu only; the win-x64 adversarial walk
      (rehearsal.md Sec 5) caught a class of Windows-only defects --
      SChannel leaf presentation, the recon.ps snapshot marshaling,
      native-tool payload quoting -- that today's CI structurally cannot
      catch. The DotNetImplantTests harness already exists; add a
      windows-latest lane.
      _AC:_ CI runs the implant end-to-end suite on Windows, and each
      defect class the Sec 5 walk caught would have failed it.
- [ ] **Push live updates to the operator UI.** The roster and tasking
      views refresh on polling intervals, so check-ins and task results
      surface only on the next tick during a live engagement. Stream
      roster and tasking deltas over SSE so operators watch the engagement
      as it happens.
      _AC:_ an operator sees an implant's check-in and its task result in
      the UI without a manual refresh and without waiting out a poll
      interval.
