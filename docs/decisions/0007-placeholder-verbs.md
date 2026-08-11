# ADR 0007 -- Placeholder verbs: register everything, execute nothing in-repo

- **Status:** Accepted
- **Date:** 2026-08-11
- **Related:** [ADR 0004](0004-offensive-tradecraft-boundary.md) (which verbs
  carry no in-repo handler)

## Context

The capability registry (`ICapabilityRegistry`,
`src/Rod.Tradecraft/Registry/ICapabilityRegistry.cs`) is populated at
composition-root time by `RodTradecraftHost.LoadCapabilitiesAsync`
(`src/Rod.Tradecraft/RodTradecraftHost.cs:134-254`). That routine registers
**every** built-in verb across all eight categories (core, recon, lateral,
persist, collect, exfil, evasion, exploit), regardless of whether the reference
implants carry a concrete handler for it.

Under [ADR 0004](0004-offensive-tradecraft-boundary.md), the verbs that carry
no in-repo handler are: the entirety of `evasion` (`evasion.avoid`,
`evasion.unload`), the entirety of `exploit` (`exploit.invoke`,
`exploit.module`), and `collect.keylog`. These are the "placeholder" verbs.
Every other built-in verb has a concrete handler in both reference implants.

The registry is not descriptor-only: every placeholder is a real
`ICapabilityModule` instance (`PlaceholderCapabilityModule`,
`src/Rod.Tradecraft/Core/PlaceholderCapabilityModule.cs`) whose `ExecuteAsync`
returns `CapabilityResult.Failed("'evasion.avoid' is registered but has no
in-process implementation")`. So a placeholder is never "no module"; it is "a
module whose body is a failure stub." This is verified by per-category tests
(`tests/Rod.Tradecraft.Tests/*CapabilitiesTests.cs`) that assert both
registration and that dispatch returns `Failed` (not `NotFound`).

Two facts about the live task path shape the decision:

1. **The server only gates and forwards; it never invokes a module.** The
   production task path uses `CapabilityRegistryTaskResolver.IsDispatchable`
   (`src/Rod.Tradecraft/Registry/CapabilityRegistryTaskResolver.cs:49-51`) as a
   gate at task issuance, then `TaskService.DispatchNextAsync`
   (`src/Rod.CoreState/Application/TaskService.cs:189-207`) hands the verb and
   arguments unchanged to the beacon stream. The `CapabilityDispatcher`
   (`src/Rod.Tradecraft/Registry/CapabilityDispatcher.cs`) -- the only thing
   that would call a module's `ExecuteAsync` server-side -- is dead code on the
   production path; it is referenced only by tests. A placeholder therefore
   satisfies the gate (it is a registered module) and is forwarded to the
   implant, where the implant's own switch table does not know the verb and
   returns `"unknown verb: " + verb` / `Failed`
   (`implant/internal/exec/runner.go:115-118`,
   `implant-dotnet/Internal/Exec.cs:101`). The friendly placeholder message is
   never produced on the live path.
2. **There is no runtime out-of-tree module loader.** A repo-wide search for
   `Assembly.Load` / `AssemblyLoadContext` / `LoadFrom` / plugin discovery
   returns zero matches. An out-of-tree module today must be a type compiled
   into the teamserver's own process, instantiated and `RegisterAsync`-ed
   against the DI-resolved registry by host code. The last-registration-wins
   rule (`src/Rod.Tradecraft/Registry/InMemoryCapabilityRegistry.cs:33-51`) is
   the override seam, but no loader populates it at runtime. The integration
   test `TradecraftTaskPathTests.cs:189-191` demonstrates the only mechanism
   available: resolve the registry from DI and call `RegisterAsync` by hand.

## Decision

**Register every built-in verb in the default registry, including the
placeholder verbs; execute none of the placeholder verbs in-repo.** A
placeholder is a `PlaceholderCapabilityModule` that satisfies the registry's
module contract (so the catalog lists it and the gate admits it) and returns
`Failed` if ever dispatched. The reference implants carry no handler for any
placeholder verb.

The out-of-tree module path is **contract-only**: the core defines the
interface, registration, dispatch plumbing, and the last-registration-wins
override seam; the concrete tradecraft for a placeholder verb is supplied as a
separate module an operator compiles into the teamserver process and registers
against the DI-resolved `ICapabilityRegistry`. No runtime, file-based, or
assembly-based module loader ships.

This is the deliberate shape: the catalog and the gate see the full verb set
(including the sensitive categories), so an operator-supplied module can
register for `evasion.avoid` or `exploit.invoke` and immediately be taskable
through the same UI and gate as every built-in verb. The placeholders are the
contract surface that makes the out-of-tree path a *registration*, not a
*schema change*.

## Rationale

- **Discoverability.** Listing every verb in `/capabilities` (ADR 0006) means
  the operator UI surfaces the full capability model -- including the sensitive
  categories -- without hardcoding. An operator adding an out-of-tree module
  sees the verbs it can override; a reader of the catalog sees the platform's
  full contract surface, not a subset trimmed to what the reference implants
  happen to ship.
- **The gate is registry-backed.** `CapabilityRegistryTaskResolver` admits a
  verb when the class set allows it **or** a module is registered. A placeholder
  is a registered module, so the gate admits the verb on any class. This is what
  makes evasion and exploit -- which are deliberately not class-gated
  (architecture.md Sec 5.2, Sec 10.1) -- taskable at all once a module arrives.
  Without the placeholder, the gate would refuse the verb before the operator
  could register a module for it.
- **The failure is graceful and local.** A placeholder tasked before any module
  is registered produces `Failed` on the implant (today, as `"unknown verb"`).
  The task is admitted, forwarded, executed nowhere, and recorded as a failed
  task in the audit trail. No crash, no hang, no silent success.
- **The dead `CapabilityDispatcher` is intentional.** The teamserver is not the
  execution site for post-exploitation verbs; the implant is. The dispatcher
  exists so an in-process capability *could* run server-side (and so tests can
  exercise the contract), but the production task path deliberately does not
  invoke it. This keeps execution on the implant, where the target's filesystem,
  network, and credentials actually live.
- **Contract-only out-of-tree loading matches the sensitivity.** The placeholder
  verbs are the ones ADR 0004 keeps out-of-tree: in-the-wild 0days, weaponized
  PoCs, novel evasion, LSASS dumping, keylogging. A runtime loader that pulled
  arbitrary assemblies into the teamserver process would be the wrong shape for
  this material -- it would invite the most sensitive code to land in the
  teamserver's own blast radius. Compile-in-and-register keeps the operator
  deliberately choosing to link the module, and keeps the teamserver's attack
  surface bounded by what was compiled into it.

## Consequences

- **Positive:** the catalog, the gate, and the UI all see the full verb set;
  an out-of-tree module registers against a verb and is immediately taskable
  with no schema change; the teamserver's runtime attack surface is bounded by
  its compile-time inputs; the failure when a placeholder is tasked without a
  module is graceful and audited.
- **Negative:** the operator-facing message for a placeholder tasked without a
  module is the implant's blunt `"unknown verb: " + verb`, not the
  placeholder's friendlier "registered but has no in-process implementation."
  This is because the server forwards without invoking the module, so the
  placeholder's body never runs on the live path. Mitigation: the audit trail
  records the failed task with the verb, so the operator can see *which*
  placeholder was tasked; the catalog (`/capabilities`) lists which verbs are
  placeholders by their category (evasion and exploit are contract-only by
  category, `collect.keylog` by ADR 0004).
- **Risk:** an operator who tasks a placeholder expecting the placeholder's
  friendly failure gets the implant's blunt one instead. This is a UX defect,
  not a correctness defect. Mitigation as above; a future improvement could
  have the server short-circuit a task whose verb resolves to a
  `PlaceholderCapabilityModule` and fail it server-side with the friendly
  message, without forwarding to the implant. That is a behavior change, not a
  design change, and is deferred.
- **Risk:** the absence of a runtime loader means "out-of-tree module" is
  heavier than it sounds -- it is a recompile, not a drop-in. This is
  deliberate (see Rationale) but should be documented for operators. Mitigation:
  the `ICapabilityRegistry` doc-comment already states the
  last-registration-wins override rule; this ADR records the loader's absence
  so it is not assumed.

## Alternatives considered

- **Register only verbs with concrete handlers; leave placeholders out.**
  Rejected: the catalog would hide the sensitive categories, the gate would
  refuse evasion/exploit verbs even after an operator registers a module (the
  registry would never be consulted, because the verb is not in the catalog the
  resolver reads), and the out-of-tree path would require a schema change
  rather than a registration. The placeholder is what makes out-of-tree a
  registration.
- **Add a runtime assembly loader for out-of-tree modules.** Rejected for the
  sensitive categories (see Rationale): it would pull the most sensitive
  tradecraft into the teamserver's own process at runtime, expanding the blast
  radius of a teamserver compromise. Compile-in-and-register is the deliberate
  shape. A loader could be added later for *non-sensitive* out-of-tree modules
  if a use case emerges, but the sensitive set stays compile-in.
- **Have the server invoke `CapabilityDispatcher` on the live task path and
  return the placeholder's friendly failure.** Rejected: it would put the
  teamserver in the execution path for post-exploitation verbs, which the
  architecture (Sec 10.3) keeps on the implant. The friendly-message UX gap is
  a smaller cost than blurring the execution boundary. Revisit as a behavior
  change if the UX defect bites.
