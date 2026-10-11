# Rod -- Todo

Open work only. An item leaves this file the moment it ships -- its record
is the commit history, and the design it followed lives in
[architecture.md](architecture.md); the designed-but-deferred items (the
redirector control plane, Sec 8; sealing, Sec 9) stay where they are
designed. Nothing here is an archive of the done.

Each item names the architecture section it serves and carries a one-line
acceptance criterion (_AC:_), so "done" stays testable. Follow the
[repository conventions](../AGENTS.md).

Lean is the standing default: an addition must say what an engagement
cannot do without it; refactors, deletions, and answering with docs
instead of code are first-class items here, equal to features. New work
starts from a gap an actual engagement surfaces.

Active is advisory order, not a queue: depth before breadth -- making the
platform's standing promises real (the compiled capability surface, the
advertised reach) ahead of widening the story, the small bounded items
behind those -- and the right item to take is still the one an engagement
actually needs. On hold is parking, not rejection: each item there names
the fact or decision that reopens it, and a cleared condition prompts the
move back into Active -- it does not make the move on its own.

## Active

(Nothing queued. The delivery campaign shipped 2026-10-11 as Sec 11.5,
per [operations/campaigns.md](operations/campaigns.md) -- the Sec 2
delivery boundary it amended is the design's own record.)

## On hold

- **Android shell for the Rust implant** (serves architecture.md Sec 12.2,
  the reach story). Parked on demand, not a blocker: the Linux build host
  can carry the whole NDK cross itself, so what is missing is an engagement
  that names an Android target -- it reopens when one does, with its design
  bill paid first (where the carrier app lives in-tree and what builds it;
  whether the memfd-exec module load survives Android's SELinux and bionic
  constraints, or the module family stays compiled-but-dormant on that
  leg). What an engagement cannot do without it: a presence on an Android
  device -- the lab and the target base both carry phones, and today Rod
  has no artifact for them. Shape: the Rust crate grows a
  `cdylib`/`staticlib` output and the NDK cross (aarch64-linux-android via
  the SDK's toolchain, wired through the build unit's cargo environment
  like the musl crosses), plus a thin carrier app shell that loads the
  library and keeps the contact loop alive under Android's background
  execution limits (a foreground service is the documented shape).
  Enroll/contact behavior is the shared wire, unchanged; the shell is
  plumbing, not protocol.
  _AC:_ the library cross-compiles for aarch64-linux-android from the Linux
  build host, and loaded by a carrier app on a device it enrolls and
  answers tasking through the same e2e the desktop legs run.

- **iOS shell for the Rust implant** (serves architecture.md Sec 12.2).
  Parked on a macOS build host: the Apple link needs Xcode's SDK, which
  the Linux host cannot carry. Shape: the same library-plus-shell pattern
  as the Android item against aarch64-apple-ios; delivery is inherently
  sideloading territory (a signed carrier app or a jailbroken device), an
  operational constraint the runbook documents rather than something the
  build can remove.
  _AC:_ cross-compiling from a macOS host produces a static library a
  carrier app links, and the enrollment leg runs.
