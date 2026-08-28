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

Production use is the driver now: the gap between the single-host
rehearsal and pointing Rod at a client network closed the same way --
the multi-host rehearsal walk, the install and recovery record, and the
install and recovery record, and the engagement description field all
shipped, and their records are the commits,
[operations/rehearsal.md](operations/rehearsal.md) and
[operations/teamserver.md](operations/teamserver.md); new work here
starts from a gap an engagement surfaces.

## Surface hardening (architecture.md Sec 9, Sec 13)

- [ ] **Walk the win-x64 surface with an adversarial eye.** The
      multi-host walk was the first real Windows round-trip and it
      immediately caught the SChannel client-cert failure -- a class of
      bug Linux runs never see. The platform-specific paths (filesystem
      verbs on real NTFS, persistence surfaces, token and lateral verbs
      against a real desktop) have one walk and no review; the earlier
      adversarial surface review passed the Linux shape only. Pass the
      win-x64 paths once the same way, and ship or file every finding.
      _AC:_ every Windows-only path in the reference implant has been
      exercised on a real Windows host, and each finding is a commit or a
      filed issue -- not a note.
- [ ] **Drill the engagement-CA rotation.** Rotation is documented as file
      replacement plus restart, but what that does to a live engagement
      has never been executed: leafs signed by the retired CA fail the
      mTLS handshake mid-session, and re-enrollment needs a fresh stager
      token because the original was spent. Walk one rotation on the
      installed shape -- swap the CA under a live implant, watch its
      egress walk, re-token and re-enroll it -- and write the true
      procedure and its blast radius into the runbooks.
      _AC:_ a mid-engagement CA swap is executed on the installed shape,
      and the runbook records what survives, what breaks, and the
      re-entry path for live implants.
