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

- [ ] **Walk the rehearsal again on the real deployment shape.** The
      executed rehearsal ran on one host with two stand-ins: loopback
      plain HTTP where the operator's TLS-terminating edge belongs, and a
      same-host redirector. Neither is the shape that faces a target
      network (Sec 7/8). Compose the stack once on real infrastructure --
      the enroll ingress and the operator API behind an actual
      terminating edge, the reference redirector on a separate host
      fronting the mTLS listener across a network hop, and a win-x64
      implant built from the pipeline checking in from a real Windows
      machine. Enroll, task, collect, burn-and-repoint, retire; write the
      multi-host shape back into docs/operations/rehearsal.md alongside
      the single-host walk, which stays the fast pre-engagement baseline.
      _AC:_ the full lifecycle completes with a TLS-terminating edge
      inline and a remote redirector carrying the beacon, and the Windows
      implant round-trips a task and a chunked exfil end to end.
- [ ] **Record the production install and recovery basics.** An operator
      deploying for real hits these in minute zero, and all of them are
      today implicit (Sec 12.1): a service-unit definition supervising
      the teamserver (crash-restart is tested behavior; the installed,
      supervised shape is unwritten), the upgrade procedure (sessions
      survive a restart -- name stop, replace, start), the restore trio
      that must move together (Postgres dump, evidence data directory,
      DataProtection keys -- restoring any one alone yields broken
      logins), and the secret-store path for
      `Operators__Initial__Password` and the Pki key passphrase (Sec 9).
      Answer with docs and a unit-file example; no new code path unless a
      gap shows itself.
      _AC:_ a fresh host reaches a supervised running teamserver by
      following the recorded procedure alone, and a restore from the
      recorded backup set accepts a pre-backup operator cookie on the
      request after restore.
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

