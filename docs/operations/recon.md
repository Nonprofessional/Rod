# External recon workbench (pre-foothold scoping)

The teamserver carries an operator-layer workbench for the scoping that
precedes the first foothold (architecture.md Sec 11.4): RDAP registration
lookups, certificate-transparency subdomain enumeration, and a port scan --
run on the teamserver, engagement-scoped, their findings landed as
engagement artifacts that join the intel layer's topology projection. This
runbook covers the egress decisions an operator makes before arming each
half, the configuration, the ROE target scope that gates the scan, and what
lands in the audit trail.

Three routes, all task scope, all refused on a closed engagement:

```
POST /engagements/{engagementId}/recon:rdap        {"target": "example.com"}
POST /engagements/{engagementId}/recon:subdomains  {"target": "example.com"}
POST /engagements/{engagementId}/recon:portscan    {"target": "10.0.0.5", "ports": "22,80,443"}
```

## The egress decisions come first

Even a passive lookup leaves the teamserver under its own address carrying
the target's name. Which services the workbench rides is the operator's
call, never a silent default: each half stays closed (its route answers
`503` naming the configuration section) until its service is named.

- **RDAP** (`Recon:RdapBaseUrl`) -- the registration-data egress. A public
  redirector (for example `https://rdap.org`) resolves the right registry
  per query and sees every lookup; a registry-direct endpoint (for example
  `https://rdap.verisign.com/com/v1`) narrows what any one party observes
  but only serves its own zone; a fronted mirror (an operator-run proxy in
  front of either) keeps the teamserver's address off the service's logs
  entirely. Pick per engagement, per OPSEC posture.
- **Certificate transparency** (`Recon:CtBaseUrl`) -- the subdomain census
  rides the crt.sh JSON shape (`{base}/?q=%.{domain}&output=json`); crt.sh
  itself or any mirror answering that shape serves it. The same
  direct-or-fronted tradeoff applies, plus volume: a CT mirror is a public
  service under no engagement's control, so front it when the census
  itself should not read as reconnaissance from your address.
- **The scan's origin** (`Recon:ScanOrigin`) -- where the scan's
  connections egress from is its own decision, separate from the lookups.
  The only origin this teamserver ships is `Teamserver`: the scan dials the
  target directly from the teamserver process. That is the honest default
  shape for a lab or a controlled engagement network, and the wrong shape
  the moment the target's edge should not see the teamserver's address --
  in that posture, scope with the passive halves and scan through an
  implant already inside (`recon.portscan` tasking) instead. Any other
  origin value is refused with a `503` naming this runbook: a
  redirector-originated scan is a future choice with its own decision, not
  a fallback.

The scan is additionally gated on the engagement's ROE target scope
(architecture.md Sec 9, below); the passive lookups are deliberately not --
the target scope is often what the lookup is building, so gating the lookup
on the scope it informs would deadlock the scoping flow.

## Configuration

| Key | Default | Meaning |
|-----|---------|---------|
| `Recon:RdapBaseUrl` | -- | The RDAP service (`{base}/domain/{name}`). Unset: the route answers `503`. |
| `Recon:CtBaseUrl` | -- | The CT mirror answering the crt.sh JSON shape. Unset: `503`. |
| `Recon:ScanOrigin` | -- | `Teamserver` arms the scan; unset or any other value keeps it at `503`. |
| `Recon:RequestTimeoutSeconds` | `30` | Per-request budget for the passive lookups. |
| `Recon:MaxSubdomainNames` | `5000` | How many distinct names one enumeration records. |
| `Recon:ScanConnectTimeoutMilliseconds` | `1500` | Per-port connect budget. |
| `Recon:ScanConcurrency` | `128` | How many ports one scan probes at once. |
| `Recon:ScanMaxPorts` | `4096` | How many ports one request may name; a full-range sweep is several requests. |

Example (environment):

```
Recon__RdapBaseUrl=https://rdap.org
Recon__CtBaseUrl=https://crt.sh
Recon__ScanOrigin=Teamserver
```

## The scan's ROE target scope

The port scan is the workbench's active half, so it carries the gate the
passive lookups skip: the engagement's ROE profile
(`PUT /engagements/{id}/roe`) gains `permittedTargets`, and a scan naming
a target outside it is refused with `422` before any connection opens,
the refusal recorded in the trail as a `ReconScanRefused` event naming the
violated rule.

Entry shapes, each matching only its own kind of target:

- **Exact hostname**, case-folded (`web01.example.com`).
- **Exact IP** (`203.0.113.10`).
- **CIDR block** (`10.0.0.0/24`, IPv4 or IPv6); an entry with a `/` that
  does not parse as a network is refused at the apply route -- a mistyped
  CIDR that silently matched nothing would narrow the scope to zero.

An empty list means unrestricted. A hostname matches by name only, never by
resolution: a DNS answer is not an authorization fact, and the scope must
not depend on what the target's own nameservers say. Symmetrically, a CIDR
entry never matches a hostname target -- resolve first and name the
address, or list the name.

## Findings and audit

Every run lands its findings as a task-less engagement artifact under the
acting operator's attribution -- pre-foothold there is no task to join --
and every attempt in the trail:

- `recon:rdap` -- a JSON registration record (registrar, status, dates,
  nameservers, secureDNS), artifact `recon.rdap:{domain}`, event
  `ReconLookupCompleted`.
- `recon:subdomains` -- JSON lines, one `{"host": name}` per name, the
  documented recon grammar (extending/tradecraft.md), artifact
  `recon.subdomains:{domain}`, event `ReconLookupCompleted`. The names join
  the topology projection as observed hosts; wildcard certificate literals
  name no concrete host and are not findings.
- `recon:portscan` -- JSON lines, one `{"host": h, "port": p,
  "state":"open"}` per open port (closed and filtered ports are not
  findings), artifact `recon.portscan:{host}:{ports}`, event
  `ReconScanCompleted`. The default port set (no `ports` named) is a
  curated stable list; name `ports` as a comma list with hyphen ranges
  (`22,80,443`, `1-1024`) for anything else.

The audit payload names the lookup and the target but never the egress
endpoint -- configuration names the endpoint, this runbook the decision.
The outcome is the findings artifact id on success, `failed:{reason}` on
failure; the refusal the ROE gate causes is its own `ReconScanRefused`
event. The findings appear in the loot view (task-less, attributed to the
running operator) and in the topology projection beside implant-side recon.

## Evolution notes

- **DNS resolution answers** -- which of the enumerated names live -- are
  the natural passive widening: same route shape, same egress decision, a
  resolver base URL beside the CT mirror.
- **Redirector- or implant-originated scans** arrive as new `ScanOrigin`
  choices with their own decisions recorded here, not as changes to the
  route.
- **Whois behind the RDAP flag** is another passive half the same shape
  would carry; RDAP is the modern registration surface and ships first.
