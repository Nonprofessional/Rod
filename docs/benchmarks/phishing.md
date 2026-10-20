# Phishing campaigns benchmark note

The delivery stage (architecture.md Sec 11.5): an engagement-scoped
campaign sends per-recipient lures through a named SMTP relay, binds
each recipient's link to a per-recipient build, and tracks the
recipient into tasking -- sent, opened, clicked, executed. Shipped
today: the campaign row (sending profile, recipient list, template,
build profile, listener), the lure edge on the public listener,
per-recipient enrollment attribution, revoke, and the reconciler engine
([operations/campaigns.md](../operations/campaigns.md)). ADR-012 names
the benchmark: GoPhish.

## The tools

| Tool | Product shape at the interaction layer | License |
|------|----------------------------------------|---------|
| GoPhish | The mainstream campaign product: sending profiles, templates, cloned landing pages, recipient groups, a launch wizard, a per-recipient results dashboard | MIT (Community Edition) |
| Cobalt Strike spear-phish | The floor below, for contrast: a raw send from the teamserver (targets, template, attachment) with no tracking and no landing pages | Proprietary |

## Adopt

- **The reusable object decomposition** (GoPhish: sending profile /
  template / landing page / group). Four named objects composed at
  launch is the interaction model operators already know. Rod carries
  them inline on the campaign row today, and Sec 11.5's evolution notes
  name the widening: a reusable sending-profile entity first, then
  templates and landing pages as engagement entities. Groups land
  Rod's way -- a recipient selection from recon assets (unified entity
  model, Sec 15.4), never a parallel address book.
- **The per-recipient results board as the campaign's main screen**
  (GoPhish). Sent, opened, clicked per recipient, sortable and
  filterable at a glance. Rod's stamps tell the same story plus the one
  GoPhish cannot: executed, the enrollment binding that proves a run
  rather than a fetch (Sec 11.5). The board is the campaign detail
  view's center.
- **Landing-page import with capture toggles** (GoPhish: import a site,
  capture credentials, capture passwords). Cloning a real page and
  choosing what it captures is the minimum working posture for a lure
  that harvests. In Rod it is module-domain -- lure assets and
  generators (Sec 15.1) -- because the campaign carries delivery and
  attribution, never the lure's content tradecraft, and a page that
  captures follows the credential store's collection posture (Sec 13).
- **Test-send to a lab address** (GoPhish). Operators never launch
  blind: template merge and relay posture are verified against an
  address the engagement owns first. Already a named widening in
  Sec 11.5; the benchmark confirms it as the floor, not a nicety.
- **Lifecycle states that read at a glance** (GoPhish's completed
  state, the field's pause buttons). Rod's `Draft → Launched →
  Completed` with `:revoke` freezing sends and 404ing every lure is the
  stronger shape -- a revoked lure answers 404, not a parked page (Sec
  11.5); the benchmark conclusion is only that the states must be
  visible on the board without opening anything.

## Skip

- **The standalone double deployment** (GoPhish: its own server, its
  own database, its own login). Campaigns live in the teamserver beside
  the implants they produce: no export/import hop, no second user
  store, no audit gap between the mail platform and the first callback
  (Sec 11.5's whole reason to exist).
- **Cross-campaign global groups** (GoPhish: org-wide users and
  groups). Recipients are engagement entities; isolation is by
  construction, and a reusable target set is recon-asset selection, not
  a global address book (Sec 2, Sec 15.4).
- **Bulk-send posture** (GoPhish's mass-mail engine and its
  throttles). Rod caps recipients at spear-phish scale deliberately:
  one build per recipient is the price of enrollment attribution
  (Sec 11.5), and bulk changes the product -- a different abuse
  surface and a different attribution math. Awareness-program scale is
  a different product and stays one.

## Divergences Rod already owns

Per-recipient builds with baked single-use enrollment credentials close
the attribution gap every benchmark tool leaves open: GoPhish's
tracking ends at the browser, Cobalt Strike's spear-phish ends at the
send, and Rod's chain runs lure to click to enrollment to first task on
one audited record.
