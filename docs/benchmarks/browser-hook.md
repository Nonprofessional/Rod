# Browser hook benchmark note

The browser class of implant (architecture.md Sec 5.2): a script served
to a hooked page is an implant whose check-in is the page's lifecycle,
tasking rides the ordinary queue, and the verb set is read-and-steer --
fingerprint, cookies, dom, screenshot, redirect, prompt, with
`reads-input` on the one verb that asks the user a question (Sec 10.4).
Shipped today: the hook serving edge on the listener family, the
class's verb set, screenshots through the exfil-chunk path (Sec 11),
and input capture plus exploit chaining held out of core for modules by
design. ADR-012 names the benchmark: BeEF.

## The tools

| Tool | Product shape at the interaction layer | License |
|------|----------------------------------------|---------|
| BeEF | The reference hook product: a hook server with its own web panel, the hooked-browser list, a module catalog marked per browser for what still works, per-module results shown in the panel | GPL-2.0 (per project documentation; the repository carries no LICENSE file) |

## Adopt

- **The hooked browser as a first-class session in the same console as
  every other implant** (BeEF's hooked-browser list is half the
  lesson). BeEF proves operators want hooked browsers in one list,
  tasked with results inline. Rod finishes the thought the unified
  entity model already decided: a hook session *is* a browser-class
  implant (Sec 5.2, Sec 15.4) -- same tasking lifecycle, same audit
  chain, same timeline, products in the same stores. The separate hook
  console is the part not to copy.
- **Fingerprint first** (BeEF's browser-details panel on hook). The
  first beat after a hook lands is the read-only picture of what the
  page is; the verb set orders itself the same way -- fingerprint and
  cookies before anything that steers, and nothing interactive fires
  before the operator asks (`reads-input`, Sec 10.4).
- **Per-target compatibility surfaced at task time** (BeEF sorts its
  catalog by working and not-working per hooked browser). Browser
  modules die by browser version; the manifest compatibility matrix
  (ADR-009) is the mechanism and the schema-driven UI renders it -- a
  command the hooked page cannot carry reads disabled with a reason,
  never fails at run time.
- **Evidence over panel output** (against BeEF's shape). Hook products
  are evidence: screenshots and DOM captures land as artifacts through
  the exfil-chunk path (Sec 11), cookies and DOM text join the
  engagement record, and command history rides the timeline. A result
  that dies with the panel is a demonstration, not an engagement fact.

## Skip

- **The command zoo as the core story** (BeEF's large, aging module
  catalog, much of it dead against a current browser). The shipped verb
  set is the standard surface (Sec 13); input capture and exploit
  chaining arrive as modules (Sec 15.1) precisely because they chase
  browser versions -- exactly the churn the module layer exists to
  absorb.
- **The standalone hook server product** (BeEF: its own panel, its own
  REST API, its own extension ecosystem). The hook serving edge rides
  the listener family like every public edge (Sec 8), in the lure
  shape of Sec 11.5: the unguessable route is the credential and the
  ingress listener's engagement scopes the socket. No second standing
  console.
- **Demo framing and delivery gimmicks** (BeEF's interstitial hooked
  pages and demo hooks). Delivery is the campaign domain's business --
  a lure page carries the hook (Sec 11.5) -- and the hook domain's job
  is the session after the fetch, not the fetch.
- **Persistence against the tab** (BeEF's popunders and confirm-dialog
  tricks). Survival past the page is tradecraft that chases browser
  hardening -- module-domain, opt-in, never a kernel verb.

## Divergences Rod already owns

In BeEF a session lives in the panel's memory and its results die with
it. In Rod a hook session is an implant row with parentage -- the
campaign that delivered it -- every verb audited, and every product in
the engagement's evidence: the difference between a demonstration tool
and an engagement record.
