# Operator UI -- the panels and their fields

The operator UI keeps its panels to a headline and a one-line standfirst;
everything else lives in hover text on the field it describes. This page is
the long form: what each panel shows, and what every build and listener field
does, in one place. Screenshots-age; this text is the reference.

## Listeners

An engagement's C2 ingress. Each listener owns two addresses:

- **Bind** -- the socket *this server* opens. Picked from the host's
  interfaces (the dropdown is built from `GET /network/interfaces`): one NIC,
  the all-interfaces wildcard (`0.0.0.0`), or a custom address; the port is
  its own field, defaulted per transport (http 5090, mTLS 5443, HTTPS
  envelope 8443, DNS 53, TCP 4444). SMB has no interface/port -- its bind is
  a bare pipe name.
- **Public endpoint** -- the address *implants dial*, baked into payloads.
  In production this is typically your redirector
  ([redirectors.md](redirectors.md)); in dev it is usually the bind itself.

Endpoint completion (HTTP-shaped transports only): an empty endpoint derives
from the bind (`bind 10.1.2.3:8443` on https-envelope becomes
`https://10.1.2.3:8443`); a bare hostname (`redirect.example`) takes the
transport's scheme and the listener's own port; a complete URL or `host:port`
pair is stored verbatim. A wildcard bind (`0.0.0.0`) names no dialable
address, so it cannot derive -- give it a hostname. DNS, SMB, and TCP cannot
derive at all; their endpoint (zone / pipe path / host:port) is required.

Every listener is engagement-scoped and persisted -- a restart rebinds it with
the same id -- and enrollment through its socket accepts only that
engagement's tokens. **Repoint** swaps the public endpoint at runtime without
touching the socket (a burned redirector is severed); **Delete** unbinds and
forgets it.

## Build

The main path is the mainstream shape: pick the **listener** the implant dials
and the **target** (OS/arch; x86 pairs with Windows only), leave the rest at
the defaults, and build. The artifact is a self-contained single-file
executable with its enrollment credential baked in -- drop it on the target
and run, zero arguments.

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

- **Endpoint (manual)** -- the dial address when you deliberately build
  without naming a listener.
- **Fallback endpoints** -- backup fronts baked in behind the primary and
  dialed in order when it burns.
- **Enroll path** -- the URI path the implant enrolls on; change it only when
  a redirector rewrites to the real route. Default `/implants/enroll`.
- **User agent** -- the `User-Agent` the implant presents, to blend with a
  known-good client. Empty leaves the HTTP client's default.
- **Request timeout (s)** -- per-request HTTP timeout. Default 30.
- **Envelope** -- `None` sends the raw JSON body; `Base64` wraps it as one
  string so the body does not read as structured C2.
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
