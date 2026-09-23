# Operator UI -- the panels and their fields

The operator UI keeps its panels to a headline and a one-line standfirst;
everything else lives in hover text on the field it describes. This page is
the long form: what each panel shows, and what every build and listener field
does, in one place. Screenshots-age; this text is the reference.

## Operating surfaces

Three words describe every conversation an implant has with the teamserver,
and the UI uses them precisely:

- **Enroll** -- the one-time registration. A dropped artifact redeems its
  baked credential exactly once, receives its identity and certificate, and
  reports its host facts and its baked contact cadence (sleep/jitter). It
  never happens again for that implant.
- **Contact** -- every later contact, on the sleep cadence the build baked.
  The implant calls home, picks up queued tasking, and returns results on
  the next cycle. All ordinary operations (shell commands, file transfers,
  process listings, screenshots) ride contacts. Every contact's handshake
  re-advertises the implant's *current* cadence, so the fleet's detail
  strip shows the live sleep/jitter pair -- a `beacon.sleep` retune lands
  on the record at the next contact.
- **Interactive** -- not "everything else": it is the on-demand live channel
  (`shell.interact`, tunnels) for real-time typing -- held open over the
  stream on a stream-mode build (the WebSocket beacon on a web front, the
  held socket on a TCP front), or carried store-and-forward on a poll build's
  contacts (operator input arrives on the next response, at the contact
  cadence). An implant that never opens one still fully operates through
  contacts.

Two builds of that contact cadence: **poll** mode makes each contact a
short request-response cycle (call home, drain tasking, sleep), while
**stream** mode holds one long-lived connection open instead -- not "one
contact", but one connection that never ends: tasking is pushed down it in
real time and the session's last-seen advances by touches on the same
stream. Either way every ordinary task rides the same verb grammar; the
difference is only how the bytes travel.

The interactive shell (`shell.interact`) is a real terminal where the
platform allows it: on Linux/macOS the reference implant runs the shell
under a pseudo-terminal, so prompts and line editing appear and the pane's
^C button sends the interrupt byte that becomes SIGINT for the foreground
program. Where no pseudo-terminal wrapper exists (a stripped container,
Windows before a ConPTY handler lands), the channel falls back to
byte-transparent pipes: fully usable, but without echo or signal
semantics. It opens as a **dialog** (the menu's "Interactive shell", the
console's `interact`, or the fleet menu): every shell session the implant
ever ran stays visible as a folded history block -- the transcript is the
task's own record -- with a separator line above the live session marking
each break. A shell does not survive its channel: when the operator closes
stdin, the shell exits, or the idle window closes an abandoned session
(default 10 minutes without operator input; `ROD_SHELL_IDLE_SECONDS`
overrides for lab runs), the next shell starts fresh -- a new process in
its home directory -- under the separator. Closing the dialog keeps a
running session alive server-side; reopening continues it.

## Naming -- the fixed vocabulary

The three connection behaviors above are the whole vocabulary, and the two
nouns are **listener** (the server-side object, created on the Listeners
page) and its **public endpoint** (the address implants dial). Every
address-carrying field names the noun it picks and, where it matters, the
behaviors it carries in parentheses. These names are **locked**: they
appear here, in the Build form, the Payloads library, and the listener
form, and they do not drift per panel. Changing any of them is a
deliberate act that updates this section first.

- **Listener (enroll + contact)** -- the Build form's first field: the
  listener whose public endpoint gets baked. The implant registers on it
  once and contacts on it for the rest of its life, interactive riding
  the same front (the WebSocket beacon on a web front's stream build, the
  contacts themselves on a poll build). The
  Payloads library's **Listener** column names the same thing
  per artifact.
- **Public endpoint (enroll + contact, manual)** -- the typed-address
  twin of the pick, under Advanced, for an address this teamserver does
  not serve (a redirector you control elsewhere).
- **Fallback public endpoints** -- backup enroll + contact addresses
  baked behind the primary and walked in order when it burns.
- **Public endpoint** -- the listener form's own field of the same name:
  what the listener will be to implants. "Callback" appears nowhere; it
  was retired as a synonym that meant three things.

Transport labels in the listener form follow the same rule -- they name
what each transport carries, not how a build makes it ride: every
transport -- HTTPS, cleartext HTTP, raw TCP, and the DNS family (TXT over
UDP, the same grammar over HTTPS on DoH) -- carries **enroll + contact +
interactive** (the socket enrolls on its opening exchange and holds the
live session on a stream build; the DNS family enrolls through a chunked
TXT exchange and opens the session itself -- a DNS-only target's whole
lifecycle rides the one carrier). Interactive rides the stream fronts
live or the poll cycles store-and-forward -- over DNS, the input arrives
on the TXT answers and the output chunks up as queries, at the query-rate
cadence: the slowest wire that carries it, carried anyway. How each
behavior rides is the build's pick -- mode and carrier -- and the Build
form's summary spells that out per build.

Three identity layers fold into the UI, and it pays to keep them straight:
a **device** is the host an implant reported at enroll (hostname, OS/arch,
account -- recorded on the implant, grouped in the fleet), an **implant** is
one enrolled identity (class, kill date, lineage, lifecycle), and a
**session** is the live connection (at most one per implant; its state is
the status dot and the last-seen column). A session whose last contact
rode the DNS carrier wears a **degraded · dns** note beside its status dot
-- the degraded-mode contract (architecture.md Sec 8): presence, short
tasking, and chunked results only, channel tasks queued until a stream
carrier returns.

- **Implants** -- the fleet in one table, one row per implant, grouped by
  device with collapsible group headers (an OS mark -- Windows, Apple, Linux,
  or the neutral chip -- and the hostname flush-left with the column content,
  the full OS description the implant reported at enroll beside it, the
  collapse caret at the far right).
  A toolbar rides the table: free-text search across the identity fields
  (applied on Enter or its Search button), a state filter (online / offline /
  retired), a class filter, and column
  sorting (implant id, last seen, kill date -- first click the natural
  direction, second flips, third returns to the default fleet order);
  past ten device groups the table paginates. The header row is always
  laid down, so an empty fleet (or a filter that matches nothing) reads as
  a table with a message, not an empty card. The row's dot is the session
  (green while it lives, gray after), the **User** column is the account the
  implant process runs under, and the last-seen column reads the
  freshest stamp -- the presence roster while online, the implant row's
  durable heartbeat after the beacon goes dark -- re-rendered on a quiet
  30 s clock so relative stamps keep moving between live events. The kill
  date column shows the artifact's own fuse as reported at enroll, "none"
  for open-ended builds. A stream
  that closes cleanly drops Online immediately; a stream that dies
  silently holds Online until the staleness sweep closes its session
  (default 15 minutes of silence, swept every minute -- adjustable at
  runtime on the **Settings** page, boot-defaulted from
  `Sessions:Staleness:Threshold` / `SweepInterval`) -- the standfirst and
  the dot's hover text say so, and Last seen always tells the truth in the
  meantime. Devices group by the hostname reported at enroll, so two hosts
  reporting the same hostname (cloned machines) share a group; the group
  header's hover says so, and the rows underneath stay per-identity. Notes and
  retire live on the row, and everything else opens from its context menu
  (right-click or the three-dot button): shell commands, the file browser,
  the process browser, recon, persistence, collection -- each entry gated
  on the implant's class, argument-bearing verbs opening a labeled dialog
  (marked with a right-edge ellipsis, the native "asks for more" menu
  convention), zero-argument verbs issuing directly.
- **Session console** (`#/engagements/{id}/implants/{implantId}`, the
  Interact link on a row) -- one implant, rendered as a terminal: a title
  bar naming the device, identity, and live state; a scrolling transcript of
  that implant's task history (each task a line with time, status tag, verb,
  and arguments; short output unfolds under the line, long output folds
  behind a line count; a channel task's pane opens in the
  same flow above the prompt); and a prompt at the bottom -- a plain line
  runs as a shell command, `help` lists the shortcuts (`interact`, `sleep`,
  `ps`, `kill`, `screenshot`, `hostenum`, `portscan`, `services`,
  `download`, `upload`, `files`, `raw`); `upload` opens the picker dialog,
  because its argument is a local file. `sleep <interval> [jitter]` retunes
  the live contact cadence without a rebuild (Go durations or bare seconds;
  `sleep 0 0` polls back-to-back -- the near-interactive posture over a poll
  build), and the same verb rides the row menu's **Beacon → Contact
  interval** entry as a labeled dialog. The transcript follows the newest line while
  the operator is parked at the bottom and pins when they scroll up. The
  Advanced disclosure is the raw verb+arguments escape hatch, pinned to
  this implant.
- **Task log** (beside Implants, in the Operate group) -- the engagement's
  task history as a filterable, live log: by implant (switches to that
  implant's own feed), verb, status, issuing operator, or free text. Each
  row carries a chevron that unfolds the full output (and a one-line
  preview of the answer while collapsed); channels open their pane. The
  log is read-only -- canceling a queued task happens in that implant's
  session console. Issuing happens in the implant menu and the console;
  this tab is for reading.

Two browsing panes open from the menu (and the console): the **process
browser** (`recon.ps` as a filterable table with a confirmed per-row
`proc.kill`) and the **file browser** (`fs.list` walks the tree; upload
rides `file.push`, download rides `file.pull` -- small files inline, larger
ones through the artifact store). Both are snapshots with a refresh, and
their results ride a browse cache: reopening shows the last listing with
its age, a listing in flight when the pane closed is attached to instead
of re-issued (its answer lands in the cache either way -- the cache polls
on its own), walking back up the tree is instant, and the file browser
reopens on the last visited directory. Refresh cancels the queued listing
it replaces and issues a fresh one, so one browse never stacks a second
identical command behind it. An implant binary fielded before a browse
verb existed answers "unknown verb" (verbs are baked into the artifact at
build time); the panes translate that answer into the fix -- rebuild the
payload and redeploy.

## Listeners

An engagement's C2 ingress. Each listener owns two addresses:

- **Bind** -- the socket *this server* opens. Picked from the host's
  interfaces (the dropdown is built from `GET /network/interfaces`): one NIC,
  the all-interfaces wildcard (`0.0.0.0`), or a custom address; the port is
  its own field, defaulted per transport (https 443, http 5090,
  DNS 53, TCP 4444).
- **Public endpoint** -- the address *implants dial* (enroll + contact,
  and interactive), baked into payloads. The create form's
  field of the same name takes a bare host, host:port, or full URL and
  completes it (scheme from the transport, port from the bind); empty
  derives it from the bind. In production this is typically your
  redirector ([redirectors.md](redirectors.md)); in dev it is usually the
  bind itself.

Endpoint completion (HTTP-shaped transports only): an empty endpoint derives
from the bind (`bind 10.1.2.3:8443` on https becomes
`https://10.1.2.3:8443`); a bare hostname (`redirect.example`) takes the
transport's scheme and the listener's own port; a `host:port` pair takes the
transport's scheme; a complete URL passes through. The stored form is always a
full URL, so the roster and every build read one uniform shape. A wildcard
bind (`0.0.0.0`) names no dialable address, so it cannot derive -- give it a
hostname. DNS and TCP cannot derive at all; their endpoint (zone /
  host:port) is required.

The transport dropdown is grouped by role -- payload ingress (https,
http), alternate reach & pivots (DNS, DoH, TCP), catchers
(shellcatch) -- and the form opens on https: the one-port posture that
carries every behavior, so the untouched default is already the recommended
shape.

Every listener is engagement-scoped and persisted -- a restart rebinds it with
the same id -- and enrollment through its socket accepts only that
engagement's tokens. **Repoint** swaps the public endpoint at runtime without
touching the socket (a burned redirector is severed). **Delete** is guarded
twice over: the button itself arms (first click turns it into a "Confirm
delete" that reverts on its own after a few seconds), and the teamserver
records which listener's socket carried each enrollment, refusing with the
count when live implants enrolled through it (retired implants do not count)
-- the operator UI turns that refusal into a final confirmation naming the
dependents, and only its explicit accept (or `?force=true` on the API)
unbinds and forgets the listener. A listener nothing depends on dies in two
clicks and no dialogs.

The panel's transport shapes, one per family:

- **`HTTPS` is the one-port shape** (the mainstream C2 listener): TLS with
  no client certificate requested anywhere -- the handshake is
  indistinguishable from an ordinary website's. Enrollment rides the socket
  on the deploy token and contacts ride the sealed envelope under the
  per-artifact key, both authenticated at the application layer. One
  listener, one port: enroll + contact, and the WebSocket beacon hangs off
  the same front when the build runs stream mode.
- **`HTTP` (cleartext, loopback) carries enroll + contact over the envelope
  POST cycle** (poll mode): every contact body and its response seal as
  AES-256-GCM under the per-artifact key -- the authentication cleartext
  http lacks a TLS layer for. The WebSocket beacon rides it the same way
  for a lab stream build.
- **`TCP`** carries the socket family: enrollment on the opening exchange,
  one connection per contact on a poll build, the held live session on a
  stream build.
- **`DNS`/`DoH`** carries the datagram family: the chunked TXT enroll
  exchange, poll contacts under the zone, the store-and-forward
  interactive discipline at the query-rate cadence.

Every payload build pins the teamserver CA into the artifact, so the
implant's first contact (enroll) validates the server it dials against the
C2's own CA -- no system-trust assumptions.

## Launchers

The engagement's one-liner home: every command an operator copies out, in
one place.

**Catch a shell** renders the paste-ready reverse-shell one-liners per
shellcatch listener -- the classic shapes across the interpreter families a
target is likely to have (`bash -i >& /dev/tcp/...`, `nc`, a Python/Perl/
PHP/socat one-liner, a PowerShell client). No credential is involved: the
address is the listener's public endpoint, and the caught shell lands in
the Shells roster.

**Deliver a beacon** cuts the payload fetch command, so a target with any
shell access beacons without a file landing first. The shell console's
Upgrade render produces the same commands for a shell it already caught;
this panel is where an operator cuts them ahead of any catch. The form and
the **Kept launchers** list below it share one card -- cutting a launcher
and coming back to it is one surface, the same layout the Build tab gives
its form and job strip. The one pick
that matters is the **payload** -- the build the fetch delivers
(the engagement's newest stands in). The rest live behind the "fetch front
& credential" fold because the defaults are almost always right: the
hardened HTTP(S) front for the fetch URL, a single-use credential (one
paste, one download) living 30 minutes. Unfold to name a specific front, to
widen the credential for a many-host deployment (unlimited or a fixed
count, up to a day), or both.

The answer carries the fetch URL, the credential, and one command per
downloader family *for the payload's own target OS*: `curl`, `wget`, and the
python3 memfd family that runs the fetched bytes without landing a file for
Linux builds; PowerShell's `iwr` for Windows builds. A command for another OS
would spend the fetch credential on bytes that cannot run, so it never
renders (a payload with no recorded target keeps every family -- the shell
being pasted into is then the only clue). Copy the one the target's shell
has; the beacon lands in the Implants table on its enrollment, already
reporting its cadence. Over an https front every family's command disables
transport verification -- the front's certificate comes from the engagement
CA, which no stock target toolchain trusts (the implant itself pins that CA);
the fetch credential is the gate.

Why the fetch carries its own credential when the payload bakes one: the
baked credential lives inside the artifact and enrolls the implant after it
runs. The fetch -- downloading those bytes -- presents a freshly minted,
scoped token instead, so a long-lived enrollment secret never rides a
command line or shell history. Each served fetch spends one use of that
token; a refused fetch (wrong front, unknown payload) spends nothing, and
the credential dies by budget, expiry, or revocation.

**Kept launchers** is the list every render lands in: when it was cut, for
which payload (named by the payload library's fingerprint -- the same
identifier the Payloads tab's Fingerprint column shows, so a row matches
its artifact without an id detour; a deleted payload falls back to the
bare artifact id), over which front, and the credential's standing as one
plain line -- `usable · 1 of 1 downloads left · until 19:00`, or `no
downloads left`, `expired 19:00`, `revoked 18:35` when it is done (a
bounded credential whose token has left the store is out of downloads
whichever end it met). **Commands** expands the row's one-liners directly
beneath it (re-rendered from the row's URL and credential, so an old row
always copies in the current shape). **Commands** appears only while the credential still
serves fetches -- a dead credential's one-liner would download nothing
(the route refuses the fetch; the artifact itself stays downloadable to
the operator from the Payloads tab). **Delete** closes the row's whole
lifecycle: the credential dies wherever a copy of the command carries it,
the row goes, and the mint's history stays on the audit trail -- for a
surgical revoke that keeps the row, the API's `:revoke` endpoint remains.
The rows survive a teamserver restart when the durable store is
configured, like every other engagement fact.

## Build

The main path is the mainstream shape: pick the **Listener (enroll +
contact)** and the **target** (OS/arch -- the Rust build unit's supported
set: Linux amd64/arm64/arm/x86, Windows amd64/x86),
leave the rest at the defaults, and build. The artifact is a
self-contained native executable with its enrollment credential
baked in -- drop it on the target and run, zero arguments. A summary
above the **Build payload** button composes from the picks live, labeled
with the fixed vocabulary: the front it enrolls on (name, transport,
public endpoint), how it contacts (envelope POSTs at the picked cadence,
a held WebSocket beacon, a held or per-contact socket connection, or the
DNS TXT carrier), and how
interactive rides (the live channel's stream, or store-and-forward over
the contacts -- the same discipline whichever wire the poll runs on).

The form's pick is the vocabulary's core role: the **enroll + contact
listener** is where the implant calls home (it registers there once and
contacts there for the rest of its life), interactive riding the same
front -- the WebSocket beacon on a web front, the held socket on a
TCP front, sealed frames under the per-artifact key, or the contacts themselves on
a poll build. Everything else -- "enroll",
"contact" in the hover texts -- names the moments inside that one
relationship. (The build API still accepts a `beaconListenerId` for the
split-socket shape; the form no longer offers one.) When the engagement
runs a DNS listener, a **Contact carrier** pick pairs it: contacts step
down to the TXT carrier (presence, short tasking, chunked results; no
channels, no staged transfers) while enrollment keeps riding the web
front, the degraded shape architecture.md Sec 8 documents.

The card's toggle switches to the tab's second artifact kind:
**Webshell script** renders a placement script with its credential baked
in, composed from two picks -- the **language** (PHP, JSP, ASPX, and
classic ASP today; the wire protocol is the same shape in every language
the list grows) and the **encryption**: the Rod family's AES-256-GCM seal
under a 256-bit key baked at generation (the default, PHP and JSP), or
the universal one-liner (the classic eval shape every manager drives,
base64 on the wire, password-gated; PHP, ASPX, and classic ASP -- whose
line is the statement-running `execute` variant, the working shape of
the family on IIS legacy). Generation is instant (no job queue): the
answer shows the credential and the script with Copy and Download, the
artifact lands in the payload store like any build, and the reachable
URL is registered under Web shells once the script is placed. The
payload library's detail row reads the credential back out of the stored
script, so "what was the key" stays answerable long after the generate
panel closed.

**Class**: there is no class pick -- the form builds the full implant
alone. The stager class is retired with the .NET trees: staged delivery
rides the Launchers tab's one-liners, which fetch the artifact over the
token-gated route and (on Linux, via the memfd family) can run it without
landing a file.

**Beacon profile**:

- **Mode** -- `stream` holds the connection open (interactive; server-push
  tasking); `poll` contacts, drains queued tasking, closes, and sleeps --
  the low-and-slow shape. Both modes carry interactive: live over the
  held stream, or store-and-forward over the contacts at the cadence
  below.
- **Contact every / Randomize ±** -- the call-home cadence and the random
  slack added to every interval so contacts are not clockwork. Defaults
  30 s / 10 s.
- **Max uses** -- how many hosts the baked credential may enroll: one spend
  per host, so one executable can seed several machines until the budget runs
  out. `0` = unlimited. Default 1.

**Advanced** (all defaulted server side; open only to change them):

- **TLS trust** -- which roots the artifact's TLS dials trust. `pinned`
  (the default): the engagement CA baked at build is the only root -- the
  self-sufficient posture. `public`: the front is a real domain whose
  certificate a public CA issued (terminated at an edge you run in front
  of the teamserver; the listener's public endpoint names the domain), and
  the artifact validates it like an ordinary client -- the posture that
  survives TLS inspection. Needs an https dial.
- **Public endpoint (enroll + contact, manual)** -- the dial address
  when you deliberately build without naming a listener.
- **Public endpoint (interactive, manual)** -- the stream front the
  held beacon dials when it differs from the enroll + contact address
  (empty = the beacon hangs off the enroll + contact front itself); the
  typed twin of the Interactive listener pick above.
- **Fallback public endpoints** -- backup enroll + contact addresses
  baked in behind the primary and dialed in order when it burns; they
  share the enroll path and the fixed contact route.
- **Enroll path** -- the URI path of the one-time registration POST. The
  only path knob: contacts ride the fixed `/implants/beacon` route and the
  held beacon its own fixed stream route, so no other path exists to set.
  Change it only when a redirector rewrites to the real route. Default
  `/implants/enroll`.
- **User agent** -- the `User-Agent` the implant presents, to blend with a
  known-good client. Empty leaves the HTTP client's default.
- **Request timeout (s)** -- per-request HTTP timeout. Default 30.
- **Enroll body** -- shapes the ENROLL request body only (the "envelope"
  word elsewhere -- the POST contact shape -- is a different thing).
  `AES-GCM` (the default) encrypts it under a
  per-artifact key minted at build, so the body stays opaque even where
  TLS terminates early (a redirector, a fronting CDN) or on cleartext
  `http` -- the same default posture contacts already carry. On direct
  `https` it is redundant -- TLS already encrypts the channel.
  `None` sends the raw JSON body, the lab-debug shape; `Base64` wraps it
  as one string so the body does not read as structured C2.
- **Kill date** -- the artifact's optional fuse. Past it the executable stops
  being usable: a leftover copy refuses to run, and a live implant terminates
  at its next contact. Empty = no fuse: the implant runs until retired -- the
  long-haul default. A pinned date also bounds the baked credential's window
  unless *Valid for* overrides it. The implant reports its baked date at
  enroll, so the fleet's Kill date column shows the artifact's own fuse
  (a dash for open-ended builds).
- **Valid for (h)** -- how long the baked credential can enroll **new**
  implants. Pairs with *Max uses*: that caps how many enrolls, this caps for
  how long. Empty = until the kill date, or a 30-day drop window when there
  is none (an open-ended implant is no reason to leave a dropped credential
  redeemable for years). Enrollment is permanent: an implant that enrolled
  keeps contacting for the rest of its life whatever this window does --
  the window (and the use budget) gate only copies of the binary that have
  not enrolled yet.

**Recent builds** is the job queue's status strip: builds run as background
jobs, the strip polls while anything runs, and it shows the last five -- enough
to watch the running build and grab the artifact you just made. Each finished
row carries its artifact, its baked token (revoke it there), the front it dials
(the listener name when the endpoint matches one), and the download -- or a
"deleted" note once the artifact is removed from the library, because a job row
outlives the payload it produced. The queue itself is bounded (50 finished jobs
per engagement) and lives for the process lifetime; the record of every
artifact that ever finished is the Payloads tab, so the two do not try to be
each other.

## Payloads

The durable library and the record of every build: every payload the
engagement ever built, straight from the payload store -- restart-safe,
unbounded by the build queue. Filter by class, language, target, listener,
or fingerprint. Each row names the **Listener** it dials (the engagement
listener's name, transport, and address when the baked endpoint matches
one; the bare address with a "manual" tag when it was typed for a
redirector this server does not serve) and shows the **Credential**
column -- the baked token's use budget read live off the token store at
list time: how many enrolls were spent out of the minted maximum, how many
are left (a zero budget reads "unlimited"), and the window (an expired or revoked credential reads "no
enrolls left"; on the in-memory dev store a fully spent token reads the
same, because the store drops it at zero).
The row's chevron unfolds the **build parameters** snapshotted at bake
time: mode, contact cadence and jitter, kill date, the credential's
minted shape, enroll path, user agent, request timeout, enroll-body
envelope, contact protection, fallback fronts, and the interactive
endpoint on a split build -- so "what did I build" never depends on
remembering the form.
**Download** the bytes again, **Revoke** to kill the baked credential (a
deployed artifact that has not yet enrolled will not be able to), or
**Delete** the payload -- the bytes and the row are gone, a launcher
fetching it 404s from then on, and the deletion is an audited fact.

## Settings

`#/settings`, beside Engagements in the sidebar -- operator-level runtime
settings, server-wide rather than per-engagement. **Session presence**
explains the fleet's offline behavior and adjusts it live: *Offline
after* is how long a silent session holds its Online dot before the
staleness sweep closes it (1 minute..24 hours; keep it above your
implants' contact interval or every quiet gap flaps offline), *Sweep
every* is how often the check runs (10 seconds..1 hour). Saving applies
on the next sweep pass -- no restart -- and the server persists the pair
(`RuntimeSettings:FilePath`, default `runtime-settings.json` beside the
server) so a restart remembers them; the `Sessions:Staleness` config
section remains the boot default. Bounds violations refuse with the
reason rather than clamping.

## Evidence panels

- **Audit** -- the append-only, hash-chained ledger; tampering with a stored
  event breaks the chain at the next link. Paged and filterable; the raw feed
  the report renders from. (The Timeline tab retired: it was a third
  projection of the same trail -- the narrative rendering lives as the
  Report's timeline section, and the standalone `/timeline` endpoint stays a
  scripting deliverable.)
- **Artifacts** -- evidence objects attached to tasks (file pulls, exfil
  chunks): pick a task to list, attach, and download its artifacts.
- **Report** -- the whole engagement as a reproducible JSON/Markdown export,
  timeline included.

## Deferred work

Design decisions recorded for later rounds; nothing here is built yet.

### Three independent channels (enroll / contact / interactive)

Today enroll and contact always share one listener (the Build form's single
"Listener (enroll + contact)" pick) and only the held beacon can split onto
its own front. The deferred shape generalizes the split: three channel
picks, each with a "same as enroll + contact" checkbox that is checked by
default, so the common case stays one address and the operator only touches
the rows they want to diverge:

- **Enroll** -- the registration listener (always required).
- **Contact** -- a checkbox riding beside the enroll pick; checked means the
  same listener (today's behavior), unchecked reveals its own listener select.
- **Interactive** -- a checkbox riding beside the enroll pick; checked means
  the same listener, unchecked reveals the web-front select (the held beacon
  hangs off any web front; the control disables where it cannot apply).

Server side this needs a separately baked `contactEndpoint` (the transport
profile already models the split for interactive via `BeaconEndpoint`; the
contact route would gain the same), a listener-side decision about which
contact shapes each transport may serve, and the wire-shape diagram extended
to three paths. Naming stays inside the fixed vocabulary: three behaviors,
two nouns, behaviors in parentheses.

### Runtime kill-date changes

The kill date is build-time only today: an optional fuse baked into the
artifact, reported at enroll, enforced on both sides. Retire covers the
"stop this implant now" need for a reachable implant, and the open-ended
default (empty = no fuse) already covers long-haul control -- so the case
for changing the fuse on a fielded implant is narrow: tightening it as a
scheduled fail-safe, or extending one whose window is closing. The honest
shape, if ever needed, is operator-controlled like retire: a row action
sets the new date, the server records it (so its handshake gate agrees),
and the change rides the next contact as tasking the implant applies to
its own fuse -- beacon.sleep's cadence control is the template.
