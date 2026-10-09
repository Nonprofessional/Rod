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

- **Implant-side plugin seam: C-ABI capability modules** (serves
  architecture.md Sec 5.3; design lands as a subsection beside Sec 5.3
  first). What an engagement cannot do without it: add a capability to a
  deployed implant without a rebuild-and-redeploy -- a per-engagement
  tradecraft module loads on demand over the task channel and never rides a
  standing artifact. Shape: a `rod-plugin-sdk` crate (the authoring surface
  -- a normal Rust trait plus the macro that emits the `extern "C"` shim;
  the C ABI is the only boundary stable across compiler versions), a
  module.load verb family that carries the module bytes over the existing
  sealed task channel, and a loader that stages the bytes the way the
  launcher's memfd one-liner does (memfd on Linux, a manual PE map on Windows) and
  resolves the entry through dlsym/GetProcAddress. Dispatch keeps the
  string-in/string-out task grammar, so a module verb reads exactly like a
  compiled one; the advertised set widens at load and reports on the next
  contact. The domain is the stateless long tail -- the recon set, lateral
  movement, persistence, credential and screen collection (the verbs the
  retired .NET implant compiled and the Rust core deliberately leaves to
  this seam); the channel verbs and the file/exec core stay compiled,
  because a plugin cannot own a live channel or a carriage.
  Unload is best-effort; replacement is last-registration-wins,
  the same rule the server-side module seam applies.
  _AC:_ a module built against the SDK, delivered through module.load,
  executes a verb the artifact did not compile, and its result lands in the
  audit trail attributed like any task.

- **Android shell for the Rust implant** (serves architecture.md Sec 12.2,
  the reach story). What an engagement cannot do without it: a presence on
  an Android device -- the lab and the target base both carry phones, and
  today Rod has no artifact for them. Shape: the Rust crate grows a
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

- **External recon workbench: passive lookups -- whois/RDAP and subdomain
  enumeration** (serves architecture.md Sec 10.1 and Sec 11; design lands
  first). What an engagement cannot do without it: scope a target before
  the first foothold -- registration data (whois, RDAP) and the subdomain
  surface (certificate transparency plus resolution) are how the operator
  aims the first implant, and today that work leaves Rod for ad-hoc tools
  whose findings never reach the engagement's attributed record. Shape: an
  operator-layer workbench, not implant tasking -- the lookups run on the
  teamserver against external services, engagement-scoped and audited,
  findings recorded as engagement artifacts that join the intel layer's
  topology projection through the seam Sec 11.2 already holds open
  (findings-as-artifacts). Even passive lookups egress from the
  teamserver, so which resolver and which CT mirror they ride -- direct or
  fronted -- is the operator's call, documented in the runbook, never a
  silent default. The scan half is parked below until an engagement needs
  it.
  _AC:_ an operator runs an RDAP lookup and a CT-log subdomain enumeration
  against a named engagement target from the operator API, and the
  findings land as engagement-scoped artifacts in the audit trail.

- **MCP server over the operator surface** (serves architecture.md Sec 4,
  the operator layer). What an engagement cannot do without it: let an
  operator drive Rod from their own agent tooling (any MCP client) instead
  of a hand-switched console -- the same roster, task, and audit reads the
  operator UI makes, discovered and called as standard tools. Shape: an
  MCP endpoint (Streamable HTTP) on the operator front behind the existing
  operator token auth, engagement-scoped by construction; read-only
  toolset first (engagements, implants, sessions, tasks and transcripts,
  audit reads); task-issuing tools are a separate later item with their
  own explicit gate, not part of this one.
  _AC:_ an external MCP client lists an engagement's implants and reads a
  completed task's output through the operator front's auth, and no write
  tool is exposed yet.

- **OpenAI-compatible LLM client for triage and reporting** (serves
  architecture.md Sec 11). What an engagement cannot do without it:
  compress operator attention -- summarize a task's captured output,
  triage a recon sweep, draft report sections from the attributed trail.
  Shape: an opt-in chat-completions client behind
  `Microsoft.Extensions.AI`'s `IChatClient` with a configurable
  OpenAI-compatible base URL and model (cloud or local runtime -- the
  format is the compatibility contract, not the vendor), disabled by
  default; every request is engagement-scoped and recorded in the audit
  trail; the egress decision (which endpoint, local or not) stays the
  operator's and is documented in the operations runbook.
  _AC:_ with the integration enabled, an operator generates a summary of a
  completed task's output from the task read, and the request appears in
  the engagement's audit trail.

## On hold

- **Port scan from the recon workbench** (serves the same Sec 10.1 and
  Sec 11 surface as the passive workbench above). Parked until an
  engagement actually needs a scan originated outside a foothold -- an
  implant already inside carries `recon.portscan` for its own segment, so
  this is the pre-foothold map only. The design bill is real: the scan is
  gated on the engagement's ROE target scope, a dimension the profile does
  not carry today (it has PermittedVerbs and PermittedImplants, Sec 9),
  so it means a new ROE dimension with its own update semantics; and where
  the scan originates (teamserver direct, a redirector, or an implant
  already inside) is an OPSEC decision the runbook documents, never a
  silent default.
  _AC:_ an operator runs a port scan against a named target inside the
  engagement's ROE target scope from the operator API and the findings
  land as engagement-scoped artifacts, while the same scan against a
  target outside the scope is refused with the refusal in the audit trail.

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

- **iOS shell for the Rust implant** (serves architecture.md Sec 12.2).
  Parked on a macOS build host: the Apple link needs Xcode's SDK, which
  the Linux host cannot carry. Shape: the same library-plus-shell pattern
  as the Android item against aarch64-apple-ios; delivery is inherently
  sideloading territory (a signed carrier app or a jailbroken device), an
  operational constraint the runbook documents rather than something the
  build can remove.
  _AC:_ cross-compiling from a macOS host produces a static library a
  carrier app links, and the enrollment leg runs.
