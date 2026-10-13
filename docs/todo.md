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

- **Deployment admin role for the account machinery** (serves
  architecture.md Sec 4.5). The trusted-operator model leaves every
  authenticated operator equal on the global surfaces, so any account --
  however low its engagement tiers -- can re-provision anyone's password
  and take over the teamserver, with no audit trail. Split the account
  machinery into two deployment roles: the bootstrap-seeded account is
  **admin**, every account created through it is a **member**, and acting
  on other operators' accounts (provision, password set, credential
  revoke, disable/enable) becomes admin-only. Members keep self-service
  over their own password and tokens; removal stays disable-shaped -- the
  account row anchors audit attribution and is never hard-deleted. The
  deployment-management surfaces (redirector fleet mint/revoke/retire,
  runtime settings writes) take the same admin gate; System stays
  readable to all; engagement membership tiers are untouched.
  Bootstrap simplifies to what a deployment actually sets -- account name
  and password (`Operators:Initial`, display name derived from the
  handle). Unset, both environments seed `admin`: dev with password
  `admin`, Production with a random password logged exactly once at first
  boot, replacing today's no-fallback lockout posture.
  _AC:_ a member draws 403 on every other-operator account action and on
  fleet/settings writes while an admin does not; an unset Production boot
  logs one admin password, later boots log none; accounts created through
  the console come up as members.

## On hold

- **iOS shell for the Rust implant** (serves architecture.md Sec 12.2).
  Parked on a macOS build host: the Apple link needs Xcode's SDK, which
  the Linux host cannot carry. Shape: the same library-plus-shell pattern
  as the Android item against aarch64-apple-ios; delivery is inherently
  sideloading territory (a signed carrier app or a jailbroken device), an
  operational constraint the runbook documents rather than something the
  build can remove.
  _AC:_ cross-compiling from a macOS host produces a static library a
  carrier app links, and the enrollment leg runs.
