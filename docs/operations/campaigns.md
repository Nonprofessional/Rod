# Delivery campaigns (the tracked lure into tasking)

The teamserver carries the lifecycle's delivery step for email-shaped
lures (architecture.md Sec 2 item 4, Sec 11.5): an engagement-scoped
campaign sends per-recipient messages through an SMTP relay, binds each
recipient's link to a per-recipient build, and tracks the recipient into
tasking -- opened, clicked, and executed (the enrollment itself). This
runbook covers the egress decisions an operator makes before launching a
campaign, the workflow, what lands in the audit trail, and the boundary
that stays out of the platform.

Five routes, all engagement-scoped, all refused on a closed engagement:

```
POST   /engagements/{engagementId}/campaigns          create (Draft)
GET    /engagements/{engagementId}/campaigns          list
GET    /engagements/{engagementId}/campaigns/{id}     detail with recipients
POST   /engagements/{engagementId}/campaigns/{id}:launch
POST   /engagements/{engagementId}/campaigns/{id}:revoke
```

The public half rides the implant family, fronted by the engagement's
listener like every other public edge:

```
GET /implants/lures/{lureId}        the lure: serves the recipient's artifact
GET /implants/lures/{lureId}/open   the tracking pixel (1x1 gif)
```

## The egress decisions come first

A campaign's mail is infrastructure that can be correlated: whichever
relay sends it appears in every received header, and the first-hop
address is the sender's story about itself. The relay is named per
campaign, never defaulted by the platform -- an operator decides, per
engagement:

- **Which relay, whose address.** A dedicated engagement relay (a
  throwaway VPS running SMTP, or a bulk provider) keeps the
  teamserver's own address out of every received header. The teamserver
  dialing a relay directly means the relay's logs carry the
  teamserver's IP; a relay behind another redirector keeps one more
  hop between the delivery path and anything reusable. Never point a
  campaign at infrastructure another engagement burned.
- **TLS mode.** `startTls` (the default) upgrades the relay connection
  in the clear; `implicit` expects TLS from the first byte; `none`
  sends credentials and mail in the clear and exists for lab relays
  only. A `none` relay on a real engagement delivers the campaign's
  content to whoever reads the wire.
- **The from address.** It rides every envelope and header, and it is
  the recipient's first filter. It belongs to the pretext, which is the
  operator's content decision, not the platform's (architecture.md
  Sec 13: evasion-grade social engineering stays out-of-tree).
- **The relay password.** Stored on the campaign row in the clear --
  the server must present it again, like a launcher's download
  credential -- never returned on read-back, never in the audit trail,
  and gone when the campaign row goes.

Configuration is engine-level, not egress: `Campaigns:EngineTickSeconds`
(default 5) is the reconciler's scan cadence and
`Campaigns:SendTimeoutSeconds` (default 20) bounds one SMTP attempt.
Neither names a relay; the campaign body does.

## The workflow

1. **Create (Draft).** Name the campaign, name the listener whose
   `publicEndpoint` fronts the lure (the same front the artifact's
   baked endpoint dials), give the relay and the from address, the
   subject/body template, the build profile, and the recipient list.
   Creation validates what delivery would otherwise discover too late:
   the template's merge fields (`{{link}}`, `{{pixel}}`, `{{email}}`,
   `{{name}}` -- `{{link}}` required in the body, unknown fields
   refused), the build profile (the build pipeline's own parser), and
   the relay's shape. The recipient cap (200) keeps a campaign at
   spear-phish scale -- the per-recipient build is the attribution
   price, and a bulk send is not this surface.
2. **Launch.** The engine takes over: one build per recipient, the
   rendered message sent once on the build's completion. Nothing
   leaves until launch; a Draft campaign is free to edit by deleting
   and recreating.
3. **Watch.** Each recipient moves `pending → building → sent` (or
   `failed:{reason}` -- delivery is single-attempt, no retry queue; a
   retry is a new campaign). Evidence timestamps (`openedAt`,
   `clickedAt`, `executedAt`) arrive as recipients react: `opened` when
   the pixel fires (best-effort -- mail clients that block remote
   images never fire it), `clicked` when the lure is fetched, and
   `executed` only when an implant redeems the recipient's baked
   credential -- a click proves a fetch, never a run.
4. **Revoke when it burns.** `:revoke` freezes both halves: no further
   sends, and every lure in the campaign 404s from that moment. The
   already-sent messages and their baked artifacts still exist; revoke
   the per-recipient deploy tokens (the campaign detail lists their
   ids) to kill enrollments from already-executed lures.

## What lands in the audit trail

Every state change is a fact: `CampaignCreated`, `CampaignLaunched`,
`CampaignRevoked`, one `DeployTokenMinted` per recipient (the baked
credential, id-only), `CampaignMessageSent` (delivered or
`failed:{reason}`, attributed to the campaign's creator), and
`CampaignLinkOpened`/`CampaignLinkClicked` (the fetcher's wire facts,
null operator -- the same first-touch shape a hook fetch records). The
enrollment that follows carries the attribution the surface exists
for: the `ImplantEnrolled` fact names the campaign and the recipient,
and the implant row carries both ids from creation. The relay address
and password appear nowhere.

## Boundary

The campaign carries delivery and attribution, not tradecraft: no
pretext content, no evasion-grade lure construction, no
credential-harvest landing page ships in-tree. A landing page built
out-of-tree would capture through the standard store's collection
posture like any other artifact source (architecture.md Sec 13).
