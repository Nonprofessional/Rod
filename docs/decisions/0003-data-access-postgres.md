# ADR 0003 -- Data access: PostgreSQL via EF Core

- **Status:** Accepted
- **Date:** 2026-08-10

## Context

The teamserver's core state (engagements, operators, implants, sessions, tasks,
stager tokens) lives in process-local `ConcurrentDictionary`s registered as DI
singletons, so a restart loses every engagement, operator, implant, session,
task, and token. The audit trail and its artifacts are durable only when
`Audit:DataDirectory` is set, written as JSON Lines -- a walking-skeleton stand-in
(roadmap M6.4) explicitly marked as "the Postgres stand-in for the skeleton,
behind the same ports."

[architecture.md Sec 12](../architecture.md) names **PostgreSQL** as the
authoritative store for both teamserver state and per-engagement audit. ADR 0001
locked the stack; this ADR records the **data-access** choice: how the .NET
teamserver reaches Postgres, and what that choice implies for the persistence
ignorance of the domain model. Roadmap M10.1 requires this ADR before any
durable-store code lands.

Two hard constraints shape the decision:

1. **The inner ring is zero-package.** `Rod.CoreState` and `Rod.Audit` carry no
   `<PackageReference>` at all, and the architecture tests
   (`LayerDependencyTests`) assert they depend on nothing in-house. M6.4's file
   adapters live inside `Rod.Audit` only because they used BCL
   `System.Text.Json` / `System.IO` -- no package added, no rule bent. Any DB
   client library (Npgsql, EF Core) therefore cannot live in the inner ring.
2. **The ports are the contract.** Every persistence surface is already a port
   (`IEngagementRepository`, `IOperatorRepository`, `IImplantRepository`,
   `ISessionRegistry`, `ITaskRepository`, `IStagerTokenService`, `IAuditStore`,
   `IArtifactStore`). The in-memory and file adapters implement them without the
   callers knowing; the durable adapter must do the same -- callers stay agnostic.

## Decision

Adopt **Entity Framework Core 10** over the **Npgsql** provider
(`Npgsql.EntityFrameworkCore.PostgreSQL`), with all persistence code isolated in a
new project, **`Rod.Persistence`**, that depends inward on `Rod.CoreState` and
`Rod.Audit` only.

| Concern | Choice |
|---------|--------|
| Access technology | EF Core 10 (`Microsoft.EntityFrameworkCore` 10.x) |
| Provider | `Npgsql.EntityFrameworkCore.PostgreSQL` 10.x |
| Home project | `Rod.Persistence` (new; depends on `Rod.CoreState`, `Rod.Audit`) |
| Schema control | EF Core migrations (`dotnet ef migrations add ...`) |
| Domain model | Persistence-ignorant: no EF attributes, no concurrency fields on entities |
| Id columns | Postgres `uuid`, mapped through per-id value converters |
| Enum columns | `int` (matches the audit hash canonical form's `(int)Kind`) |
| Membership | Owned collection against the private `_members` field |
| Selection | `ConnectionStrings:Postgres` present opts into the durable adapters |

`Rod.Persistence` is wired at the composition root in
`src/Rod.TeamServer/Program.cs`, after `AddRodTransport(...)` registers the
in-memory defaults, by **replacing** the port registrations for whichever stores
have a durable implementation. This is the same pattern `Rod.Operators` already
uses to swap `NullLiveEventBus` for `InMemoryLiveEventBus`. With the connection
string absent, the in-memory adapters stay registered and every existing test is
unchanged.

The layer dependency tests gain a `Persistence_Dependencies_PointInwardOnly` rule
(allowing only `Rod.CoreState` and `Rod.Audit`), mirroring the rule already
enforced on `Rod.Operators` and `Rod.Tradecraft`.

### Concurrency

The domain entities carry no row-version or ETag today, and adding one to the
inner ring would leak persistence detail into it. Instead, concurrency is
introduced **at the adapter**, where it is actually needed:

- **Stager-token redeem** -- atomic check-then-consume, so two concurrent redeems
  of a single-use token cannot both pass.
- **Task FIFO** -- a monotonic enqueue sequence orders dispatch and history,
  matching the in-memory adapter's behavior.

These live in `Rod.Persistence`; the domain model stays free of them.

### Audit chain stays storage-agnostic

The hash math (`AuditChain.Chain` / `ComputeHash` / `VerifyTrail`) lives in
`Rod.Audit` and is unchanged. A durable audit store recovers each engagement's
chain head from the highest-sequence row on startup -- the database analogue of
the file store recovering the head from the last line -- and stamps new appends
through the same `AuditChain.Chain` call. The reloaded trail round-trips through
`VerifyTrail` unchanged.

### Tests

The M10.1 acceptance test needs a live Postgres. It is provisioned by
**Testcontainers** (an ephemeral container per test), gated to **skip** (not fail)
when Docker is unavailable, so CI without Docker stays green and the rest of the
suite is portable.

## Rationale

- **EF Core over raw Npgsql or Dapper.** EF Core's migrations, change tracking,
  and value-converter story handle this codebase's entity shapes -- private and
  parameterized constructors, get-only and `private set;` properties, strongly
  typed id structs, owned collections -- without the boilerplate a micro-ORM or
  raw driver would require for six aggregates plus audit/artifact. It is also the
  choice the repo already presupposes: AGENTS.md Sec 4 documents the EF migration
  command (`dotnet ef migrations add ... -p <Infra> -s <Web>`), and the new
  `Rod.Persistence` is exactly that `<Infra>` project with `Rod.TeamServer` as
  `<Web>`. EF Core 10 is the LTS match for the pinned `net10.0` SDK.
- **A dedicated persistence project.** The only way to satisfy the zero-package
  inner ring is to keep the EF/Npgsql dependency in a project the layer rules
  permit to reference `Rod.CoreState` and `Rod.Audit`. This is the same structural
  reason `Rod.Operators` and `Rod.Tradecraft` are separate projects wired at the
  composition root rather than folded into `Rod.Transport`.
- **Persistence-ignorant entities.** The domain model is the architecture's stable
  center (architecture.md Sec 4.1). Keeping EF configuration, value converters,
  and the `DbContext` out of `Rod.CoreState` means a future store swap or a second
  read model does not touch the domain.
- **`int` enums.** The audit canonical form already hashes `(int)Kind`, so storing
  enums as `int` keeps the chain stable and avoids native Postgres enum migration
  churn when a member is added.
- **Testcontainers.** Reproducible, self-contained test infra; the skip-not-fail
  gate keeps the suite honest about its Docker dependency without making Docker a
  hard build requirement.

## Consequences

- **Positive:** a restart leaves core state in place and the audit chain still
  verifies (the M10.1 AC); the in-memory path stays as the default and the test
  host is unchanged; the domain model gains no persistence coupling; the
  EF-migration workflow the repo already documents is now real.
- **Negative:** a new project and four packages enter the repo (EF Core, EF Core
  Design, the Npgsql provider, and Testcontainers for tests); `dotnet-ef` becomes
  a local tool manifest entry; schema evolution is now migration-driven.
- **Risk:** EF Core materializing types with private/parameterized constructors
  and get-only properties is well-supported but fiddly for the owned
  `Engagement._members` collection. Mitigation: the full entity configuration is
  written up front so the `InitialCreate` migration captures the complete schema
  in one pass, and the trickiest mapping (engagement membership) is proven in the
  first delivered stores.

## Alternatives considered

- **Raw Npgsql.** Minimal overhead and full SQL control, but loses migrations and
  the value-converter/construction story, and contradicts the EF-migration command
  already committed to in AGENTS.md Sec 4. Rejected: the boilerplate cost across
  six aggregates plus audit/artifact outweighs the dependency savings, and the
  inner ring is protected equally well either way (the dependency lives in
  `Rod.Persistence` regardless).
- **Dapper over Npgsql.** A lighter mapping layer than EF Core, still hand-written
  SQL. Shares raw Npgsql's migration and construction drawbacks for a smaller
  saving. Rejected for the same reason.
- **A managed Postgres-as-a-service abstraction.** Defer the access choice to a
  future cloud-managed store. Premature: it adds a layer without resolving how the
  .NET host reaches the database today, which is the question this ADR answers.
