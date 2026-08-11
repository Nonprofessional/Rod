# ADR 0006 -- Capability catalog endpoint lives in the tradecraft layer

- **Status:** Accepted
- **Date:** 2026-08-11
- **Implemented:** roadmap M11.1 (`GET /capabilities`, mapped from
  `src/Rod.TeamServer/Program.cs:71`)

## Context

The operator UI needs to discover the capability catalog -- the verbs the
teamserver can dispatch, with their categories, versions, and OPSEC attributes
-- so tasking is data-driven rather than hardcoded. The registry that holds
this data lives in `Rod.Tradecraft` (layer 6, the pluggable-tradecraft layer):
`ICapabilityRegistry.ListAsync` returns `IReadOnlyList<CapabilityDescriptor>`
(`src/Rod.Tradecraft/Registry/ICapabilityRegistry.cs:34-39`), and the default
`InMemoryCapabilityRegistry` is populated with every built-in verb at
composition-root time (`src/Rod.Tradecraft/RodTradecraftHost.cs:134-254`).

The layer rule (`tests/Rod.Architecture.Tests/LayerDependencyTests.cs:82-86`,
architecture.md Sec 4.3) forbids `Rod.Transport` from referencing
`Rod.Tradecraft`: transport may depend only on `Rod.CoreState`, `Rod.Protocol`,
`Rod.Audit`, and `Rod.BuildPipeline`. So the catalog endpoint cannot live in
transport the way most operator API endpoints do, because transport has no way
to reach the registry.

Two patterns are available for an outer layer that owns data transport cannot
see:

- **Pattern A -- an outer layer exposes its own endpoints.** `Rod.Operators`
  already does this: the SSE stream lives in
  `src/Rod.Operators/Endpoints/OperatorEventsEndpoint.cs`, mapped from
  `RodOperatorsHost.MapOperatorEndpoints`, and the composition root calls it
  alongside `MapRodEndpoints` (`src/Rod.TeamServer/Program.cs:64`). No new
  inner-ring port is invented; the outer layer owns the endpoint because it
  owns the data.
- **Pattern B -- a CoreState port implemented by an outer layer.** The implant
  listing follows this: `IImplantRepository` lives in CoreState, transport
  resolves it and hosts `GET /engagements/{id}/implants`
  (`src/Rod.Transport/Endpoints/ImplantEndpoints.cs:41-68`). The same shape is
  already in `Rod.Tradecraft` itself: `CapabilityRegistryTaskResolver`
  implements the CoreState port `ITaskCapabilityResolver`
  (`src/Rod.Tradecraft/Registry/CapabilityRegistryTaskResolver.cs:49-51`,
  swapped in at `RodTradecraftHost.cs:85-86`).

## Decision

Follow **Pattern A**: the catalog endpoint lives in `Rod.Tradecraft` itself, not
in transport, and the composition root maps it alongside the operator layer's
endpoints.

- Endpoint: `GET /capabilities`
  (`src/Rod.Tradecraft/Endpoints/CapabilityEndpoints.cs:26-44`), returning the
  registry's descriptors as `CapabilityDescriptorResponse` records.
- Composition-root wiring: `RodTradecraftHost.MapCapabilityEndpoints`
  (`src/Rod.Tradecraft/RodTradecraftHost.cs:104-108`) and
  `src/Rod.TeamServer/Program.cs:71` (`app.MapCapabilityEndpoints();`), placed
  alongside `AddRodTradecraft` / `AddRodOperators` for the same layer-separation
  reason.
- The catalog is **global, not engagement-scoped**: capability verbs are the
  language-neutral contract implants build against, independent of any one
  engagement.

The rejected alternative is Pattern B with a new `ICapabilityCatalog` port in
`Rod.CoreState`.

## Rationale

- **The catalog is not domain state.** Every existing CoreState port
  (`IEngagementRepository`, `IOperatorRepository`, `IImplantRepository`,
  `ISessionRegistry`, `ITaskRepository`, `IStagerTokenService`,
  `IImplantCertificateAuthority`) is authoritative, engagement-scoped domain
  state. The capability catalog is a process-global read of the loaded module
  set -- registry metadata, not domain state. Putting it in CoreState would be
  the first CoreState port that is neither authoritative domain state nor
  engagement-scoped.
- **The Operators-layer precedent is on point and already shipped.** `Rod.Operators`
  faces the identical constraint (transport cannot reference it) and solved it
  the same way: the SSE endpoint lives in the operator layer, mapped from the
  composition root. `CapabilityEndpoints.cs` cites this precedent by name.
- **Pattern B would force a parallel DTO.** A CoreState `ICapabilityCatalog`
  port could not return `Rod.Tradecraft.CapabilityDescriptor` (that would make
  CoreState depend on Tradecraft, breaking the inner ring). It would need its
  own CoreState DTO, and a Tradecraft adapter would map
  `CapabilityDescriptor` -> that DTO -- pure ceremony for a read-only listing.
  The chosen design maps once, at the endpoint, in
  `CapabilityDescriptorResponse.Of` (`CapabilityEndpoints.cs:60-65`).
- **The UI is already data-driven off the catalog.** `src/Rod.TeamServer/Client/src/api.ts:222-239`
  fetches `/capabilities`; `capabilities.ts` builds the verb table from it,
  including the OPSEC-attribute -> risk-badge table (`KNOWN_ATTRIBUTES`).
  There is no hardcoded verb list in the client. So the endpoint placement is
  load-bearing for the UI and tested through consumption.

## Consequences

- **Positive:** no new inner-ring port; no parallel DTO; the catalog stays
  where its data is; the composition-root assembly pattern (`AddRodOperators`
  + `AddRodTradecraft` + `AddRodTransport`, then `MapOperatorEndpoints` +
  `MapCapabilityEndpoints` + `MapRodEndpoints`) is uniform across the three
  layers transport cannot reach; the catalog is global, matching its semantics.
- **Negative:** the operator API surface is split across three projects
  (`Rod.Transport` for most endpoints, `Rod.Operators` for SSE,
  `Rod.Tradecraft` for the catalog). The composition root pays the assembly
  cost, and a reader looking for "the operator API" must check three places.
  Mitigation: `Program.cs:24,30,64,71` documents the three-way assembly, and
  the `Rod.Operators` precedent already established it.
- **Risk:** if a future endpoint needs the catalog cross-engagement (e.g. an
  operator-scoped capability allowlist), Pattern A's global endpoint is the
  wrong shape. Mitigation: add the engagement-scoped concern as a *separate*
  endpoint (in transport, against a CoreState port), leaving the global catalog
  alone; do not retrofit engagement scoping onto the catalog.

## Alternatives considered

- **Pattern B -- an `ICapabilityCatalog` port in `Rod.CoreState`, implemented
  by a `Rod.Tradecraft` adapter, consumed by a transport-hosted `GET
  /capabilities`.** Layer-legal (CoreState defines the port, Tradecraft
  implements it, transport consumes it -- exactly the `ITaskCapabilityResolver`
  shape) and precedented, but rejected for the reasons in Rationale: the
  catalog is not domain state, the parallel DTO is ceremony, and the
  Operators-layer precedent is cleaner. The right time to revisit is if the
  catalog grows an engagement-scoped concern, which would make it domain state
  and earn the CoreState port.
- **Hardcode the verb table in the client.** Rejected: the catalog is the
  registry's view of what loaded, including any out-of-tree overrides; a
  hardcoded table drifts from the registry and cannot reflect operator-supplied
  modules.
