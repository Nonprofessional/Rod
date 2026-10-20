# Recon benchmark note

The scoping stage before the first foothold (architecture.md Sec 11.4,
with the post-foothold view in Sec 11.2): registration data, the name
and address surface, and the port map, landed as engagement artifacts
that join the topology projection. Shipped today: RDAP with a whois
fallback, certificate-transparency subdomains, DNS resolution, and the
ROE-gated teamserver connect scan, all behind explicit egress
configuration ([operations/recon.md](../operations/recon.md)). ADR-012
names the benchmark set: nmap, masscan, Amass, Maltego.

## The tools

| Tool | Product shape at the interaction layer | License |
|------|----------------------------------------|---------|
| nmap | The default interactive scanner: CLI flags as the interface, scan recipes held by memory and habit, XML output as the interchange format | NPS License (custom, source-available) |
| masscan | Rate-first sweep scanner: whole-range breadth at a configured packets-per-second, list output | AGPL-3.0 |
| Amass | OSINT enumeration suite: enumeration and intel modes, many data sources each with its own API keys, its own graph store | Apache-2.0 |
| Maltego | Commercial link-analysis workbench: entities on a canvas, transforms that expand them, a transform marketplace, commercial tiers for sharing | Proprietary |

## Adopt

- **Breadth-then-depth as the workflow shape** (masscan feeding nmap).
  The field's actual pipeline is a fast sweep that finds the shortlist
  and a deep scan that follows it. Rod's contribution is the join: both
  halves land in the one recon grammar and the one topology projection
  (Sec 11.4), so the pipeline is a saved workflow over one record
  rather than a copy-paste hop between tools.
- **Parse, don't embed** (nmap XML, masscan lists). Standing tools keep
  machine-readable output precisely so orchestration can consume them;
  Rod imports output as provider findings and never carries a foreign
  engine inside the teamserver process. The kernel test (Sec 15.1) and
  the deployment shape forbid it, and nmap's NPSL and masscan's AGPL
  make bundling a legal non-starter on top.
- **The transform workflow without the canvas** (Maltego). The
  productive core of link analysis is running an enrichment action on
  an entity and getting new linked entities back -- adopt that as verbs
  over the recon asset, results linking to their source (unified entity
  model, Sec 15.4). The canvas itself is a render: the optional topology
  view (ADR-013), never the primary navigation.
- **Egress and rate as explicit operator decisions** (masscan's rate as
  the headline control; Amass's source selection). Where a scan
  originates and how loud it is are OPSEC decisions, and the benchmark
  shows they belong in the operator's face, not buried in a config
  file. Rod's posture -- egress configured per service, scan origin a
  named choice, ROE target scope gating the active half (Sec 11.4) --
  is the adoption.
- **The OSINT census as a first-class pre-foothold flow** (Amass).
  Aiming the first implant at a name and address surface before
  anything touches the target is the stage's own working mode; RDAP,
  certificate transparency, and resolve ship it (Sec 11.4). Amass's
  source sprawl is the caution: each new source is an in-domain
  provider (ADR-014), added behind the grammar with its own egress
  decision -- never a new panel.

## Skip

- **A panel per tool** (the nmap tab beside the Amass tab). One
  workbench, one findings grammar, one topology projection; a new data
  source is not a domain (ADR-014) -- it lands as an in-domain
  provider.
- **A second system of record for the asset graph** (Amass's graph
  store, Maltego's saved graphs). Findings land as engagement
  artifacts and the topology projection renders them (Sec 11.2,
  Sec 11.4); the record is Rod's, the graph is one view of it, and
  shared-case collaboration is engagement tenancy plus the timeline,
  already owned.
- **Maltego as an integration target** (proprietary, no open
  contract). Conclusions only, per the standing license rule; the
  topology render module (ADR-013) is the one piece worth having.

## Divergences Rod already owns

Every lookup and scan is a single attributed, audited act on an
engagement's record. The field's ad-hoc recon leaves findings in
operator home directories, so the attribution story breaks at the seam
between scoping and the engagement -- the exact gap Sec 11.4 exists to
close.
