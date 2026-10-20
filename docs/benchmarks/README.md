# Benchmark notes

How Rod's product decisions stay anchored to the field: every major
functional domain starts with a benchmark note (ADR-012), and every new
domain passes the open/close checklist before it exists (ADR-014).
Authority: [architecture.md Sec 15.4](../architecture.md).

## The mechanism (ADR-012)

Before design work opens in a functional domain, a note lands at
`docs/benchmarks/<domain>.md` and names the field it enters. It carries
three things:

- **The mainstream tools** -- what operators actually use in the domain,
  each with its product shape at the interaction layer and its license
  (read from the project's own repository).
- **An adopt list and a skip list** -- every entry names its *why*, and
  "because X has it" is never the why: each entry ties to an engagement
  need or a Rod design fact (an ADR, an architecture section, a shipped
  surface) and names where it lands -- kernel, module layer, or UI.
- **Product and interaction conclusions only** -- workflows,
  decompositions, interaction models, deployment shapes. Never code,
  never internals, never a port.

Two gates apply over every entry:

- **The kernel test** (Sec 15.1): take the capability away -- if an
  engagement can still run from delivery to report with the audit chain
  intact, it is not kernel.
- **License discipline** (Sec 15.2): GPL-family tools are
  conclusions-only, never copied or ported; permissive licenses do not
  widen that line, because ADR-012 draws it at conclusions rather than
  license text; proprietary tools are benchmarked by published product
  behavior only.

A note rides the PR that opens its domain. When the field moves, the
note is rewritten -- stale conclusions are deleted or merged, never
archived beside their replacement (ADR-015).

## Note shape

```
# <Domain> benchmark note

One paragraph: the domain's place in the lifecycle, its anchor
sections, and what Rod ships today.

## The tools
A table: tool / product shape at the interaction layer / license.

## Adopt
- **<conclusion>** (<tool>): <why, tied to a Rod fact; names its
  landing -- kernel, module layer, or UI>.

## Skip
- **<conclusion>** (<tool>): <why>.

## Divergences Rod already owns
Where Rod's shipped design is deliberately ahead of the benchmark set,
so the note cannot read as a copy list.
```

## Domain open/close checklist (ADR-014)

Authority: [architecture.md Sec 15.4](../architecture.md); this section
is its checklist rendering. Run it before a new navigation entry, entity
set, or workflow exists, and re-run it over the whole domain list at
every major version.

Function ships at one of three granularities: **domain** (a navigation
entry, first-class entities, its own workflow), **module** (a capability
package behind the contract), **verb** (the atomic command, the ATT&CK
mount point). A new domain passes the four tests:

| # | Test | Question | Weight |
|---|------|----------|--------|
| 1 | Entity | Does it introduce a first-class entity other domains reference? | Mandatory |
| 2 | Stage | Is it a distinct lifecycle stage with its own working mode? | Mandatory, with 1 |
| 3 | Authorization | Does it carry distinct ROE gating semantics? | Weighted |
| 4 | Benchmark | Is there a tool category to benchmark against? | Weighted |

Tests 1 and 2 pass together or fail together; 3 and 4 strengthen a pass
but cannot save a failure on either mandatory test. What fails lands
somewhere that already exists:

| The function is... | It lands as |
|--------------------|-------------|
| A new data source | An in-domain provider |
| A new delivery carriage (SMS phishing is a campaign channel, not a domain) | A channel extension inside the campaign domain |
| A new operation on an existing entity | A verb or module |
| A new network carriage | A transport provider (ADR-004) |

Anti-patterns, each a recorded way product surfaces die:

- **A domain per tool.** Navigation becomes a tool inventory, and churn
  in the field churns the product.
- **A domain per ATT&CK tactic.** The matrix is a lens, not an org
  chart; tactics move, workflows do not.
- **The god domain.** One entry that answers everything answers nothing;
  navigation stops discriminating.

Re-review runs in both directions at every major version: a module that
never produces cross-domain entities demotes; a provider that grows them
promotes.

## Notes

| Note | Domain | One-line verdict |
|------|--------|------------------|
| [c2.md](c2.md) | C2 tasking and implant management | Adopt the contract-driven tasking interaction and the shared attributed event stream; skip every fat client, embedded dialect, and second deployment shape. |
| [recon.md](recon.md) | Recon | One workbench, one findings grammar: adopt breadth-then-depth and the transform workflow; skip a panel per tool and any second system of record. |
| [phishing.md](phishing.md) | Phishing campaigns | Adopt GoPhish's object decomposition and the per-recipient board; skip the standalone product shape and everything bulk. |
| [browser-hook.md](browser-hook.md) | Browser hook | The hooked browser is an implant like any other: adopt fingerprint-first and compatibility-at-task-time; skip the command zoo and the second console. |
