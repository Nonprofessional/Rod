# ADR 0012 -- Implant capability pluggability: class-aware, registry-driven dispatch

- **Status:** Accepted
- **Date:** 2026-08-13
- **Related:** [architecture.md](../architecture.md) Sec 5.1 (profiles baked in at
  generation), Sec 5.2 (implant classes and the "admission is not execution"
  rule), Sec 6 (payload build pipeline), Sec 10.1/10.3 (capability model and
  tasking gate), Sec 13/14 (sensitive-capability boundary and capability bar);
  [ADR 0004](0004-offensive-tradecraft-boundary.md) (the in-repo tradecraft
  boundary this preserves);
  [ADR 0007](0007-placeholder-verbs.md) (contract-only verbs);
  [ADR 0009](0009-single-in-tree-toolchain-dotnet.md) (the .NET toolchain the
  reference implant builds on).

## Context

architecture.md has always described an implant whose capability surface is
**scoped to its class and self-describing**: Sec 5.2 bakes each class's reduced
verb set into the artifact so "a generated payload is self-describing," and
states the standing rule that **"admission is not execution: a verb may be
class-admissible ... yet ship no built-in handler, running only when an operator
supplies an out-of-tree module."** The server half of that design is in place:

- `Rod.CoreState.ImplantClassCapabilities` is the per-class authority (Stage-2
  carries 19 verbs; a stager carries `file.pull`; etc.).
- Task issuance gates on it through `ITaskCapabilityResolver`, and
  `CapabilityRegistryTaskResolver` **widens** the gate when a capability module
  is registered for a verb (Sec 10.3) -- the path that lets the contract-only
  categories (`evasion`, `exploit`, and the `collect.keylog` / LSASS surfaces)
  dispatch without a class rule.
- `Rod.DotNetBuildUnit` bakes the class verb set into the per-implant profile as
  a `verbs` field alongside the endpoint, beacon, and transport profile.

The **reference .NET implant has not caught up to any of this**. Today:

- `Beacon.Caps` is a hardcoded `string[]` of 14 verbs advertised at every
  handshake, regardless of the baked class. A stager artifact would advertise
  the full stage-2 surface.
- `BakedProfileSupport.SeedFromBaked` reads the baked endpoint/beacon/transport
  keys but **never reads the `verbs` field** -- the self-describing verb set is
  written by the build and ignored by the implant.
- `Runner.Dispatch` is a hardcoded `switch`. Adding any verb means editing the
  runner; there is no seam through which an out-of-tree handler can supply a
  verb, and no notion of a verb that is class-admissible but unimplemented.

The capability table and the reference implant have therefore drifted: the
Stage-2 set lists five verbs the .NET implant does not implement
(`file.push`, `file.pull`, `tunnel.open`, `probe.read`, and the contract-only
`collect.keylog`). Under the current implant that drift is invisible because the
implant advertises its own fixed list; the moment it advertises its baked set it
would promise verbs it cannot run.

This ADR closes that loop. It is the prerequisite for two standing requirements:
(1) the reference implant stays **lean** -- its advertised surface matches its
operational purpose, not a fixed maximum; and (2) Rod is **production-usable for
authorized internal penetration testing and red-team exercises** -- an operator
can build a per-engagement artifact that carries exactly the capabilities a task
needs, layer out-of-tree handlers onto a clean core, and trust that what the
implant advertises is what it runs.

## Decision

The reference implant becomes **class-aware and handler-registry-driven**,
closing the loop with the server-side authority that already exists. Four
points, each scoped to preserve the ADR 0004 boundary.

### 1. The implant advertises its baked, class-scoped verb set at handshake

`Beacon.Caps` is derived from the baked profile, not hardcoded.
`BakedProfileSupport` reads the `verbs` field the build already writes and
surfaces it as the advertised capability set. The advertised surface therefore
matches the class the build was issued for: a stager advertises `file.pull`, a
stage-2 advertises the stage-2 set. This is the leanness and OPSEC property --
an artifact never advertises verbs beyond its operational purpose, and a
low-purpose artifact no longer leaks the full stage-2 surface to the teamserver
at handshake.

### 2. The advertised set is the intersection of the baked class verbs and the compiled handlers

Advertise `(baked class verbs) ∩ (compiled-in handlers)` -- never a verb the
implant cannot execute. This reconciles the table/implant drift mechanically:
the five unimplemented stage-2 verbs are simply absent from what a stock
reference build advertises. A class-admissible verb with no compiled handler is
**not advertised**; per Sec 5.2 it "runs only when an operator supplies an
out-of-tree module," which in implant terms is a different build that compiles
that handler in (point 4). The contract is unchanged on the wire -- the
teamserver still gates on the class set and the registry -- but the implant
never promises what it cannot deliver, so an operator never sees `unknown verb`
for something the handshake offered.

### 3. Dispatch routes through an implant-side handler registry, not a switch

Introduce an implant-side handler contract -- the implant analog of the server's
`Rod.Tradecraft.Modules.ICapabilityModule` -- that each verb handler implements:

```
string Verb { get; }
(TaskOutcome, string, IReadOnlyList<ExfilChunk>) Dispatch(string arguments);
```

The fourteen current handlers become instances; `Runner` holds a verb-to-handler
registry populated at composition and dispatches by lookup. An unknown verb
(advertised in neither the baked set nor the registry) reports `Failed: unknown
verb` exactly as today -- the dispatch contract and its graceful failure are
preserved. The exfil out-of-band chunk path is unchanged: handlers return chunks
and the beacon loop writes them after the `TaskResult`, as it does today.

This is the seam for growth: a new verb is a new handler plus a registration,
not an edit to a central switch, and the registry is the single place that
defines the compiled-in surface point 2 intersects with the baked set.

### 4. The reference implant stays clean; out-of-tree handlers compile into a separate artifact

ADR 0004 is unchanged: `collect.keylog`, the `evasion` and `exploit` categories,
and LSASS dumping get **no in-repo handler**, ever. Pluggability is the
mechanism by which they are used, not a way to smuggle them in-repo. An
authorized operator builds a per-engagement artifact that adds out-of-tree
handler implementations against the same `IVerbHandler` contract and the same
wire protocol, registered into the same registry; the build pipeline's
per-class bake is the existing foundation, and per-build module selection
(layering out-of-tree handler source into a `DotNetBuildUnit` build) is the
follow-on this ADR points at but does not design.

## Production-usability properties

This ADR exists to make the implant usable in real engagements, not merely tidy.
The properties that follow are load-bearing, not incidental:

- **What it advertises is what it runs.** The advertised ∩-implemented rule
  removes the class of failure where the teamserver tasks a handshake-advertised
  verb and the implant answers `unknown verb`. That is the difference between a
  demo implant and one an operator can rely on under time pressure.
- **Per-class leanness is an OPSEC property, not just tidiness.** A stager or
  ephemeral artifact carries only its purpose's verbs, so its in-memory and
  on-disk surface -- and what a defender recovers from a beacon -- is the
  minimum the task requires.
- **Compile-time registration, not runtime loading.** Handlers are registered
  into a static registry at composition. There is no dynamic assembly load, no
  reflection-based plugin discovery. This keeps the artifact static and
  Native-AOT-friendly (ADR 0009), avoids a load-failure-mid-engagement
  reliability hazard, and avoids the on-disk and behavioral artifacts a runtime
  loader would introduce.
- **The core is untouched by modules.** Enrollment, the mTLS beacon stream,
  sleep/jitter/kill-date, and the dispatch spine are fixed and independently
  tested. A handler bug cannot break check-in; a bad handler returns `Failed`,
  it does not crash the beacon loop.
- **The build stays hermetic and reproducible.** `DotNetBuildUnit` already
  copies the source tree to a unique temp dir, isolates `NUGET_PACKAGES`, and
  skips `bin`/`obj`. Module selection composes with that by adding handler
  source into the copy, not by mutating the real tree or introducing host state
  -- concurrent builds still do not race, and two builds of the same params
  still match.
- **Out-of-tree DX.** An operator writing a capability for an internal exercise
  implements one `IVerbHandler`, registers it, and rebuilds -- no core edits, no
  wire-protocol change. The contract is the same surface every build unit and
  every implant language builds against.

## Rationale

- **It closes a loop the architecture already specifies, rather than inventing
  one.** Sec 5.2 already mandates the self-describing baked verb set and the
  "admission is not execution" rule; the server already enforces them. The
  implant is the missing half, and this ADR defines how it catches up without
  changing any contract the server depends on.
- **The registry mirrors a proven in-repo pattern.** The server's
  `ICapabilityModule` / `ICapabilityRegistry` is exactly this shape, down to the
  case-insensitive verb key and "last registration wins." Reusing the pattern on
  the implant keeps one mental model across both halves.
- **Compile-time registration is the lean and reliable choice.** A runtime
  plugin loader would buy flexibility this threat model does not need -- the
  capability set is already selected at build time per class -- while costing
  AOT-compatibility, footprint, and reliability.
- **It serves the capability bar (Sec 14) without crossing the tradecraft
  boundary (Sec 13).** The substrate becomes best-in-class -- per-class OPSEC
  tuning, a clean module seam, reliable dispatch -- while sensitive tradecraft
  stays out-of-tree, exactly as Sec 14's last paragraph requires.

## Consequences

- **Positive:** the implant's advertised surface matches its class, so per-class
  artifacts are lean and OPSEC-minimal; dispatch has a single, tested growth
  seam; the table/implant drift is reconciled by construction; out-of-tree
  capability development stops touching the runner.
- **Positive:** the reference implant can be developed toward the full stage-2
  set (the unimplemented `file.*` / `tunnel.open` / `probe.read` core verbs) by
  adding handlers to the registry, with the advertised set tracking
  automatically.
- **Negative:** two load-bearing invariants must be upheld by review: the
  advertised set must stay the ∩ of baked and implemented (a handler without a
  baked class verb, or a baked verb without a handler, must not leak into the
  handshake), and the reference registry must stay free of ADR 0004 tradecraft.
- **Negative:** the class table, the build bake, and the implant registry become
  a three-way contract that must move together. A verb added to the class table
  does nothing on the implant until a handler compiles in, which is correct but
  must be understood by anyone editing the table.
- **Risk:** drift could recur. Mitigation: a test that asserts the advertised set
  equals `(baked verbs) ∩ (registered handlers)` for each class, and that the
  reference registry contains no ADR 0004 verb.

## Implementation

Open work, tracked in [todo.md](../todo.md) ("Implant-side capability
pluggability"). The sequencing this ADR implies:

1. Read `verbs` in `BakedProfileSupport`; derive `Beacon.Caps` from it.
2. Introduce `IVerbHandler` and a registry; move the fourteen handlers behind it.
3. Intersect baked verbs with registered handlers to form the advertised set;
   add the ∩-invariant test and the no-ADR-0004-verb test.
4. Reconcile `ImplantClassCapabilities` with the reference handler set (the five
   unimplemented verbs become either implemented handlers or documented
   contract-only entries, not silent drift).
5. Out-of-tree follow-on: per-build module selection in `DotNetBuildUnit` so an
   authorized operator can compile extra handlers into a per-engagement artifact
   without forking the reference implant.

## Alternatives considered

- **Runtime dynamic assembly loading for plugins.** Rejected: reflection-based
  discovery breaks Native AOT (ADR 0009), enlarges the artifact, and introduces
  a load-failure hazard and on-disk artifacts inappropriate for the threat
  model. The capability set is already decided per-class at build time, so
  runtime flexibility buys nothing that justifies the cost.
- **Advertise the full baked class set regardless of implemented handlers.**
  Rejected: the implant would advertise verbs it cannot run, recreating the
  `unknown verb`-for-an-advertised-verb failure under load and violating "what
  it advertises is what it runs." The intersection is the whole point.
- **Keep the hardcoded switch; add `collect.keylog` in-repo behind a flag.**
  Rejected on two grounds: it crosses the ADR 0004 boundary (the boundary is by
  technique kind, not by configuration), and it leaves the implant non-lean and
  without a growth seam. Pluggability is how keylog is used; the handler stays
  out-of-tree.
- **Make the implant class-aware but keep the switch.** Rejected: it solves
  advertising but not growth, and leaves no seam for out-of-tree capability
  development. The registry is the part that makes the design durable.
