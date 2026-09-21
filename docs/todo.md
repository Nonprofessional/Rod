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

The list is in priority order: start from the top. An item that names
its own blocker (a build host, an environment) is worked the moment the
blocker clears, not skipped.

- **Server-side automation: triggers and scheduled tasking** (serves
  architecture.md Sec 10.3; design lands as a new subsection there before
  any code). What an engagement cannot do without it: act on a cadence or
  on a return while no operator watches -- the overnight screenshot every
  30 minutes, the triage batch on first contact, the chain that reads a
  result and tasks the follow-up. Shape: an engine beside the operator
  layer subscribing to the live event bus (Sec 4.1, layer 4) for event
  triggers and running a scheduler for time triggers; engagement-scoped
  declarative rules (trigger, condition, action) persisted with the
  engagement; every firing issues through `TaskService` so the class,
  carrier, ROE, and closed-engagement gates hold unchanged, attributes to
  a synthetic automation operator, and lands in the audit trail. Guards:
  firing caps, cooldowns, chain depth, no sensitive verbs. Time triggers
  are durable (persisted next-fire stamps); event triggers inherit the
  bus's best-effort posture and say so. A sandboxed script front-end
  (JS/Lua) is a possible follow-on evaluator, not the first one --
  declarative rules are auditable and testable and cover the
  engagement-shown needs.
  _AC:_ a rule that issues shell.exec on one implant every 30 minutes
  survives a teamserver restart, shows automation attribution in the audit
  trail, and is cancelable from the operator API.

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

- **External recon workbench: whois/RDAP, subdomains, port scan** (serves
  architecture.md Sec 10.1 and Sec 11; design lands first). What an
  engagement cannot do without it: scope a target before the first
  foothold -- registration data (whois, RDAP), the subdomain surface
  (certificate transparency plus resolution), and the port map are how the
  operator aims the first implant, and today that work leaves Rod for
  ad-hoc tools whose findings never reach the engagement's attributed
  record. Shape: an operator-layer workbench, not implant tasking -- these
  lookups and scans run on the teamserver against external services,
  engagement-scoped and audited, findings recorded as engagement
  artifacts. Passive lookups (whois, RDAP, CT-log enumeration) are the
  safe defaults; active scanning (port scan) is gated on the engagement's
  ROE target scope and carries an explicit egress note -- where the scan
  originates (teamserver direct, a redirector, or an implant already
  inside, whose host/port recon already exists) is an OPSEC decision the
  runbook documents, never a silent default.
  _AC:_ an operator runs an RDAP lookup and a CT-log subdomain enumeration
  against a named engagement target from the operator API, and the
  findings land as engagement-scoped artifacts in the audit trail.

- **Browser-hook implant class: a BeEF-shaped XSS platform** (serves
  architecture.md Sec 5.2 and Sec 10.1; design lands first). What an
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
  Where the hook script itself lives -- a second reference artifact beside
  the Rust implant, or transport-owned like the webshell adapters -- is
  the first design question.
  _AC:_ a hooked browser on a test page enrolls as a Browser-class implant
  over the envelope carrier, and an operator tasks a fingerprint and a
  cookie read against it, with both results in the audit trail.

- **Target intel and situational awareness layer** (serves
  architecture.md Sec 11 and the operator layer, Sec 4.1; design lands
  first). What an engagement cannot do without it: hold what the
  engagement learns -- today recon findings, loot, and host observations
  live inside task output strings, so the operator re-reads transcripts
  instead of consulting a picture. Shape: labels and operator notes on
  implants and hosts (attributed, part of the trail), typed loot views
  over the exfil artifacts that already exist (credential, file, and
  screenshot renderers -- the collection verbs are in-repo, the
  organizer is what is missing), and a topology view assembled from the
  recon workbench and implant discovery data, pivot links included.
  Everything is engagement-scoped and audit-backed: the layer organizes
  the trail, it does not become a second store of truth.
  _AC:_ an operator tags an implant with a note, opens a captured
  screenshot from the loot view, and both actions carry attribution in
  the audit trail.

- **Out-of-band event notifications** (serves architecture.md Sec 4.1,
  layer 4; design lands first). What an engagement cannot do without it:
  reach the operator who is not at the console -- an implant that
  returns overnight, a caught shell, a failed task are visible only to
  connected operator sessions today. Shape: a subscriber on the live
  event bus forwarding selected event kinds to operator-configured
  channels (webhook first; IM bridges are configuration, not code),
  engagement-scoped, the subscription itself audited. Best-effort like
  the bus it rides -- the audit trail stays the record.
  _AC:_ an operator registers a webhook for session-opened and
  shell-caught events, and a new contact delivers a push to it.

- **Operator roles and interaction ownership** (serves architecture.md
  Sec 9 and Sec 10.3; design lands first). What an engagement cannot do
  without it: more than one operator without collisions -- today every
  operator is a peer who can type into any channel, and nothing marks
  who is driving which implant. Shape: per-operator claims beyond the
  current peer model (a read scope, a tasking scope, an approver
  scope), per-implant activity presence extending the existing presence
  service, and exclusive claims on live channel interaction (an
  interactive shell's input half, a tunnel) so two operators cannot
  type into one shell; claims are visible on the live bus and released
  on disconnect.
  _AC:_ two operators on one engagement see each other's claim on an
  interactive shell, the second's input is refused while the claim
  holds, and an operator without the tasking scope cannot issue tasks.

- **Sensitive-verb approval workflow** (serves architecture.md Sec 9
  and Sec 10.2/10.3; design lands first). What an engagement cannot do
  without it: a second pair of eyes where it matters -- sensitive verbs
  require engagement authorization by design, but the authorization is
  configuration-time; there is no in-flow request, approval, and
  release. Shape: a request queue on the existing gate -- an operator
  requests a sensitive tasking, a lead holding the approver scope
  approves or refuses, the approved task enters the queue attributed
  to both, and the whole arc (requested, approved, refused) lands in
  the audit trail and on the live bus. Automation firing a sensitive
  verb is refused outright, never queued for approval.
  _AC:_ a sensitive verb requested by one operator does not queue until
  a second approves it, and both the request and the approval appear in
  the engagement's audit trail.

- **ATT&CK mapping in the capability model and report** (serves
  architecture.md Sec 10.1 and Sec 11; design lands first). What an
  engagement cannot do without it: tell the client what was exercised
  -- a red-team deliverable without technique coverage makes the reader
  map the report by hand. Shape: capability descriptors carry ATT&CK
  technique ids as metadata (the tradecraft registry is the single
  place verbs are described), and the closeout report derives a
  coverage view from the audit trail -- techniques exercised, by which
  verbs, against which targets -- with unmapped verbs surfaced as a
  review list, not silently dropped.
  _AC:_ a closeout export includes technique coverage derived from the
  audit trail, and a verb without a mapping shows up as unmapped rather
  than absent.

- **Shift handoff digest** (serves architecture.md Sec 11; design lands
  first). What an engagement cannot do without it: resume command after
  an absence -- the trail holds everything that happened, but an
  operator returning to the console reconstructs the watch by reading
  it raw. Shape: a time-windowed digest view over the audit trail
  (sessions opened and closed, tasking issued and its outcomes,
  sensitive approvals, annotations), assembled by the reporting layer
  the closeout export already uses; the LLM client item above is the
  natural narrator for it, but the digest stands without it.
  _AC:_ an operator requests the digest for the last watch window and
  gets a single ordered account of sessions, task outcomes, and
  approvals from the audit trail.

- **Console depth for the solo operator** (serves the operator UI,
  docs/operations/operator-ui.md; design lands first). What an
  engagement cannot do without it: speed at the keyboard -- the console
  exposes every capability, but common sequences are typed out verb by
  verb every time. Shape: a command palette with fuzzy reach, task
  snippets -- a named sequence of issue commands, engagement-scoped and
  shareable -- and inline action surfaces on the process and file
  browsers over verbs that already exist (kill, transfer both ways).
  No new verbs, no new gates: this item is console ergonomics only.
  _AC:_ an operator saves a named task snippet once and issues its
  whole sequence with one command from the palette.

- **Delivery campaigns: tracked spear-phish into tasking** (serves
  architecture.md Sec 2, the delivery step of the lifecycle, and
  Sec 11; design lands first). What an engagement cannot do without it:
  open the door -- the first foothold arrives by delivery, and today
  that happens outside Rod entirely (a manual mailbox, a separate
  phishing platform), so the causal chain from lure to implant lives
  across two tools and the attribution story breaks at the seam. Shape:
  an engagement-scoped campaign entity -- a sending profile (SMTP
  relay), a target list, a message template with per-recipient merge --
  where each recipient's link or attachment binds to a per-recipient
  stager token the build pipeline already mints, so an implant -- or a
  browser hook, the item above -- that follows the lure enrolls already
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

- **Rehearsal refresh against the settled surface** (serves
  [operations/rehearsal.md](operations/rehearsal.md); the walk's own
  dated header already queues this). What an engagement cannot do
  without it: a pre-deployment walk that matches what would actually be
  deployed -- the recorded walk predates the four-family decision
  (architecture.md Sec 8), the engagement-scoped listener model, and
  the Rust artifact, and reads through patch notes instead of as one
  procedure. Shape: re-execute the single-host walk and the redirector
  composition on the current surface (Https one-port listener, the
  envelope and WebSocket beacons, raw-TCP and DNS/DoH fronts,
  pipeline-built Rust payloads), then rewrite the record as the
  procedure it now is, dated-header patch notes folded in or dropped;
  the CA rotation drill carries over where it still holds. The
  multi-host Windows leg waits for the host the item below names;
  everything else runs from Linux now.
  _AC:_ the single-host walk and the redirector composition re-executed
  end to end on the settled surface, the refreshed record quoting
  acceptance evidence from the new run rather than the retired one.

- **Windows host verification of the Rust implant** (serves
  architecture.md Sec 12.2's reach story and the rehearsal walk above;
  blocked on a Windows host the developer provides). What an engagement
  cannot do without it: confidence on the OS engagements actually land
  on -- the e2e legs prove the wire on Linux, while the Windows-only
  code paths never execute there: the sensitive verbs (proc.kill,
  inject.shellcode, collect.minidump, collect.keylog) and the
  pipes-backed interactive shell. Shape: run a pipeline-built win-x64
  Rust artifact on the provided host against a teamserver and exercise
  shell.exec, file.push and file.pull, the channel verbs over the
  raw-TCP carriage, and the sensitive four, with the same adversarial
  eye the retired win-x64 surface pass applied; the outcome lands as
  the refreshed walk's multi-host Windows legs.
  _AC:_ the sensitive verbs and the channel verbs answer tasking from a
  Windows host through the same e2e shape the Linux legs run, recorded
  in the rehearsal walk.

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
  stager stages a stage-2 (memfd on Linux, a manual PE map on Windows) and
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

- **iOS shell for the Rust implant** (serves architecture.md Sec 12.2;
  blocked on a macOS build host -- the Apple link needs Xcode's SDK, which
  the Linux host cannot carry). Shape: the same library-plus-shell pattern
  as the Android item against aarch64-apple-ios; delivery is inherently
  sideloading territory (a signed carrier app or a jailbroken device), an
  operational constraint the runbook documents rather than something the
  build can remove.
  _AC:_ cross-compiling from a macOS host produces a static library a
  carrier app links, and the enrollment leg runs.
