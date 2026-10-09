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

- **External recon workbench: passive lookups -- whois/RDAP and subdomain
  enumeration** (serves architecture.md Sec 10.1 and Sec 11; the design
  lands with the item in architecture.md Sec 11.4). The gap: scope a
  target before the first foothold -- registration data (whois, RDAP) and
  the subdomain surface (certificate transparency plus resolution) are how
  the operator aims the first implant, and today that work leaves Rod for
  ad-hoc tools whose findings never reach the engagement's attributed
  record. Shape: an operator-layer workbench, not implant tasking -- the
  lookups run on the teamserver against external services,
  engagement-scoped and audited, findings recorded as engagement artifacts
  that join the intel layer's topology projection through the seam Sec
  11.2 already holds open (findings-as-artifacts). Even passive lookups
  egress from the teamserver, so which resolver and which CT mirror they
  ride -- direct or fronted -- is the operator's call, configured, and
  documented in the runbook (operations/recon.md), never a silent default.
  _AC:_ an operator runs an RDAP lookup and a CT-log subdomain enumeration
  against a named engagement target from the operator API, and the
  findings land as engagement-scoped artifacts in the audit trail.

- **Port scan from the recon workbench** (serves the same Sec 10.1 and
  Sec 11 surface as the passive workbench above; design in Sec 11.4 too).
  The pre-foothold map only -- an implant already inside carries
  `recon.portscan` for its own segment. The scan is gated on the
  engagement's ROE target scope, a new `PermittedTargets` dimension on the
  profile (Sec 9), with its own update semantics riding the existing ROE
  route; and where the scan originates is an OPSEC decision the runbook
  documents, never a silent default (the shipped origin is
  teamserver-direct, config-gated).
  _AC:_ an operator runs a port scan against a named target inside the
  engagement's ROE target scope from the operator API and the findings
  land as engagement-scoped artifacts, while the same scan against a
  target outside the scope is refused with the refusal in the audit trail.

## On hold

- **Browser-hook implant class: a BeEF-shaped XSS platform** (serves
  architecture.md Sec 5.2 and Sec 10.1; design lands first). Parked on
  sizing, not dependency -- the certificate-less envelope carrier it would
  ride is shipped (Sec 8) -- but the item is a second reference artifact
  with its own serving and storage story (where the hook script lives is
  the first design question), too large to ride along beside the plugin
  seam; it reopens as a deliberate project once that seam lands. What an
  engagement cannot do without it: pivot a script-injection foothold into
  tasking -- the hooked browser is the most common web-facing foothold,
  and today it needs a separate platform (BeEF) with its own operator
  surface, storage, and OPSEC story, disconnected from the engagement
  trail. Shape: a new `Browser` implant class whose artifact is a served
  hook script (`<script src>`), enrolling and contacting over the
  certificate-less envelope carrier (Sec 8) on the poll cadence the
  store-and-forward degraded discipline already models; the reduced verb
  set starts mainstream and documented -- browser fingerprint, cookie
  read, DOM read and screenshot, redirect, prompt -- with the sensitive
  boundary held (Sec 13): input capture and browser-exploit chaining stay
  out-of-tree capability contracts, not core verbs. Every hooked browser
  is an engagement-scoped implant entity, so attribution, live events,
  audit, and the automation engine treat it like any other implant.
  _AC:_ a hooked browser on a test page enrolls as a Browser-class implant
  over the envelope carrier, and an operator tasks a fingerprint and a
  cookie read against it, with both results in the audit trail.

- **Delivery campaigns: tracked spear-phish into tasking** (serves
  architecture.md Sec 2, the delivery step of the lifecycle, and Sec 11;
  design lands first). Parked on the boundary call: Sec 2 item 4 holds
  delivery out of Rod's scope by design, so this item is an amendment to
  that line rather than a quiet drift -- its design lands together with
  the Sec 2 change, and reopening it is that decision made. What an
  engagement cannot do without it:
  open the door -- the first foothold arrives by delivery, and today
  that happens outside Rod entirely (a manual mailbox, a separate
  phishing platform), so the causal chain from lure to implant lives
  across two tools and the attribution story breaks at the seam. Shape:
  an engagement-scoped campaign entity -- a sending profile (SMTP
  relay), a target list, a message template with per-recipient merge --
  where each recipient's link or attachment binds to a per-recipient
  deploy token the build pipeline already mints, so an implant -- or a
  browser hook (a later item) -- that follows the lure enrolls already
  attributed to the campaign and the recipient. Tracking (sent,
  opened, clicked, executed) rides the public ingress that serves
  staging, redirector-fronted like every other public edge; the
  campaign's egress (which relay, whose IP) is an OPSEC decision the
  runbook documents, never a silent default. Credentials a landing
  page captures follow the existing standard-store collection posture;
  evasion-grade social engineering stays out-of-tree.
  _AC:_ a two-recipient campaign mints per-recipient lure links, and
  the recipient who executes the lure enrolls with campaign and
  recipient attribution visible in the audit trail.

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
