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
  reports its host facts. It never happens again for that implant.
- **Check-in** -- every later contact, on the sleep cadence the build baked.
  The implant calls home, picks up queued tasking, and returns results on
  the next cycle. All ordinary operations (shell commands, file transfers,
  process listings, screenshots) ride check-ins.
- **Interactive** -- not "everything else": it is the on-demand live channel
  (`shell.interact`, tunnels) held open over the mTLS stream for real-time
  typing. An implant that never opens one still fully operates through
  check-ins.

Three identity layers fold into the UI, and it pays to keep them straight:
a **device** is the host an implant reported at enroll (hostname, OS/arch,
account -- recorded on the implant, grouped in the fleet), an **implant** is
one enrolled identity (class, kill date, lineage, lifecycle), and a
**session** is the live connection (at most one per implant; its state is
the status dot and the last-seen column).

- **Implants** -- the fleet in one table, one row per implant, grouped by
  device with collapsible group headers (an OS mark and the hostname
  flush-left with the column content, the collapse caret at the far right).
  A toolbar rides the table: free-text search across the identity fields,
  a state filter (online / offline / retired), a class filter, and column
  sorting (implant id, last seen, kill date -- first click the natural
  direction, second flips, third returns to the default fleet order);
  past ten device groups the table paginates. The header row is always
  laid down, so an empty fleet (or a filter that matches nothing) reads as
  a table with a message, not an empty card. The row's dot is the session
  (green while it lives, gray after) and the last-seen column reads the
  freshest stamp -- the presence roster while online, the implant row's
  durable heartbeat after the beacon goes dark -- re-rendered on a quiet
  30 s clock so relative stamps keep moving between live events. Notes and
  retire live on the row, and everything else opens from its context menu
  (right-click or the three-dot button): shell commands, the file browser,
  the process browser, recon, persistence, collection -- each entry gated
  on the implant's class, argument-bearing verbs opening a labeled dialog,
  zero-argument verbs issuing directly.
- **Session console** (`#/engagements/{id}/implants/{implantId}`, the
  Interact link on a row) -- one implant, rendered as a terminal: a title
  bar naming the device, identity, and live state; a scrolling transcript of
  that implant's task history (each task a line with time, status tag, verb,
  and arguments; short output unfolds under the line, long output folds
  behind a line count; the channel pane for interactive tasks opens in the
  same flow above the prompt); and a prompt at the bottom -- a plain line
  runs as a shell command, `help` lists the shortcuts (`interact`, `ps`,
  `kill`, `screenshot`, `hostenum`, `portscan`, `services`, `download`,
  `files`, `raw`). The transcript follows the newest line while the operator
  is parked at the bottom and pins when they scroll up. The Advanced
  disclosure is the raw verb+arguments escape hatch, pinned to this implant.
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
identical command behind it.

## Listeners

An engagement's C2 ingress. Each listener owns two addresses:

- **Bind** -- the socket *this server* opens. Picked from the host's
  interfaces (the dropdown is built from `GET /network/interfaces`): one NIC,
  the all-interfaces wildcard (`0.0.0.0`), or a custom address; the port is
  its own field, defaulted per transport (https 443, mTLS 5443, http 5090,
  DNS 53, TCP 4444). SMB has no interface/port -- its bind is
  a bare pipe name.
- **Public endpoint** -- the address *implants dial*, baked into payloads.
  In production this is typically your redirector
  ([redirectors.md](redirectors.md)); in dev it is usually the bind itself.

Endpoint completion (HTTP-shaped transports only): an empty endpoint derives
from the bind (`bind 10.1.2.3:8443` on https becomes
`https://10.1.2.3:8443`); a bare hostname (`redirect.example`) takes the
transport's scheme and the listener's own port; a `host:port` pair takes the
transport's scheme; a complete URL passes through. The stored form is always
a full URL, so the roster and every build read one uniform shape. A wildcard
bind (`0.0.0.0`) names no dialable address, so it cannot derive -- give it a
hostname. DNS, SMB, and TCP cannot derive at all; their endpoint (zone /
pipe path / host:port) is required.

Every listener is engagement-scoped and persisted -- a restart rebinds it with
the same id -- and enrollment through its socket accepts only that
engagement's tokens. **Repoint** swaps the public endpoint at runtime without
touching the socket (a burned redirector is severed); **Delete** unbinds and
forgets it.

One transport caveat shapes the whole panel: **check-ins are gRPC (HTTP/2
over mTLS) and cannot ride a cleartext socket** (Kestrel serves cleartext
HTTP/2 only on an HTTP/2-only endpoint, which cannot also serve the HTTP/1.x
enrollment). The shapes that follow from that:

- **`HTTPS` is the one-port shape** (the mainstream C2 listener): TLS with
  the client certificate optional at the TLS layer -- enrollment rides the
  socket on the stager token before any certificate exists, and the check-in
  routes demand the enrolled certificate at the application layer. One
  listener, one port, everything on it. Builds against it need no split.
- **`HTTP` (cleartext) carries enrollment and stager fetch only.** A
  cleartext check-in exists for envelope-speaking clients (POST check-ins,
  identity by implant id -- the same anything-with-reach posture as
  DNS/SMB/TCP), and the in-tree .NET implant does not speak it: build
  against it with a beacon listener named (the split-socket shape) unless
  your implant is a Tier-0 envelope client.
- **`mTLS`** is the strict beacon-only socket (the certificate is demanded
  at the TLS layer); pair one with an `HTTP` listener for enrollment when
  you want the hard posture.

Every payload build pins the teamserver CA into the artifact, so the
implant's first contact (enroll) validates the server it dials against the
C2's own CA -- no system-trust assumptions.

## Build

The main path is the mainstream shape: pick the **callback front** (the
listener the implant calls home to) and the **target** (OS/arch; x86 pairs
with Windows only), leave the rest at the defaults, and build. The artifact
is a self-contained single-file executable with its enrollment credential
baked in -- drop it on the target and run, zero arguments. A small diagram
under the picks draws the traffic shape the build bakes and follows them
live.

Two words carry the form: the **callback front** is where the implant calls
home (it registers there once and checks in there for the rest of its
life), and the **interactive front** is the optional second socket for live
channels. Everything else -- "enroll", "check-in", "beacon" in the hover
texts -- names the two moments inside that one relationship.

**Interactive front (mTLS)** is enabled when the picked front is cleartext
`http`: check-ins cannot ride that socket, so the form offers the
engagement's `mTLS` listener for the interactive stream -- the split-socket
shape (registration one socket, interactive channel another). Left empty,
the implant polls the callback front over the envelope POST cycle instead.
An `https` front carries both halves itself and needs no split. The build
API takes the same thing as `beaconListenerId`, or a typed
`beaconEndpoint`, and refuses a cleartext callback endpoint with no beacon
named -- that artifact would enroll and then sit offline forever.

**Class**: `Stage2` is the full implant; `Stager` is a small loader that
fetches a finished Stage2 (picked from the builds below) at launch and runs
it -- the two-stage shape for size-sensitive delivery. A stager bakes only
its expiry date; beacon timing belongs to the Stage2 it fetches.

**Beacon profile**:

- **Mode** -- `stream` holds the connection open (interactive; server-push
  tasking); `poll` checks in, drains queued tasking, closes, and sleeps --
  the low-and-slow shape.
- **Check-in every / Randomize ±** -- the call-home cadence and the random
  slack added to every interval so check-ins are not clockwork. Defaults
  30 s / 10 s.
- **Expiry date** -- the artifact's fuse. Past it the executable stops being
  usable: a leftover copy refuses to run, and a live implant terminates at
  its next check-in. Empty = 30 days from the build; it also bounds the baked
  credential's window.
- **Max uses** -- how many hosts the baked credential may enroll: one spend
  per host, so copies of one executable need one use each. Default 1.

**Advanced** (all defaulted server side; open only to change them):

- **Callback URL (manual)** -- the dial address when you deliberately build
  without naming a listener.
- **Interactive URL (manual)** -- the https host the interactive stream
  dials when it differs from the callback front (empty = check-ins ride the
  callback front's envelope cycle); the typed-URL twin of the Interactive
  front picker above.
- **Fallback fronts** -- backup callback fronts baked in behind the primary
  and dialed in order when it burns; they share the enroll path and the
  fixed check-in route.
- **Enroll path** -- the URI path of the one-time registration POST. The
  only path knob: check-ins ride the fixed `/implants/beacon` route and the
  interactive stream rides the mTLS socket's own gRPC path, so no other path
  exists to set. Change it only when a redirector rewrites to the real
  route. Default `/implants/enroll`.
- **User agent** -- the `User-Agent` the implant presents, to blend with a
  known-good client. Empty leaves the HTTP client's default.
- **Request timeout (s)** -- per-request HTTP timeout. Default 30.
- **Enroll body** -- shapes the ENROLL request body only (the "envelope"
  word elsewhere -- the POST check-in shape -- is a different thing).
  `None` sends the raw JSON body; `Base64` wraps it as one string so the
  body does not read as structured C2; `AES-GCM` encrypts it under a
  per-artifact key minted at build, so the body stays opaque even where
  TLS terminates early (a redirector, a fronting CDN) or on cleartext
  `http`. On direct `https` it is redundant -- TLS already encrypts the
  channel.
- **Credential window (h)** -- how long the baked credential stays
  redeemable. Empty defaults to the artifact's expiry window.

**Recent builds** is the job queue's view: builds run as background jobs, the
list polls while anything runs, and each finished row carries its artifact,
its baked token (revoke it there), and the download. The list is bounded
(50 finished jobs per engagement) and lives for the process lifetime.

## Payloads

The durable library: every payload the engagement ever built, straight from
the payload store -- restart-safe, unbounded by the build queue. Filter by
class, language, target, dial endpoint, or fingerprint. **Download** the
bytes again, **Revoke token** to kill the baked credential (a deployed
artifact that has not yet enrolled will not be able to), or **Delete** the
payload -- the bytes and the row are gone, a stager fetching it 404s from
then on, and the deletion is an audited fact.

## Evidence panels

- **Audit** -- the append-only, hash-chained ledger; tampering with a stored
  event breaks the chain at the next link. Paged and filterable; the raw feed
  the timeline and report render from.
- **Artifacts** -- evidence objects attached to tasks (file pulls, exfil
  chunks): pick a task to list, attach, and download its artifacts.
- **Timeline** -- the same trail as a day-by-day narrative.
- **Report** -- the whole engagement as a reproducible JSON/Markdown export.
