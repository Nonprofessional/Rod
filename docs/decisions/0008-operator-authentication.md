# ADR 0008 -- Operator authentication: cookie sessions over verified credentials

- **Status:** Accepted
- **Date:** 2026-08-12
- **Related:** [ADR 0003](0003-data-access-postgres.md) (the durable store the
  credential adapter plugs into), [architecture.md](../architecture.md) Sec 4
  (the operator layer)

## Context

The walking skeleton identified an operator with a **client-generated id**: the
browser minted a `crypto.randomUUID`, stored it in `localStorage`, and passed
it (plus a self-typed handle) in the body of every write and as query
parameters on the engagement event stream. The server trusted it verbatim. That
is fine for a single-operator development UI and unacceptable for a deployment:
there is no proof the operator is who they claim, and any client can impersonate
any operator by choosing an id.

This ADR covers **authentication only** -- establishing that an operator session
belongs to a verified identity. **Per-engagement role-based access control**
(membership, roles, who may task which engagement) is deliberately out of scope
and deferred to a later decision; every authenticated operator can presently
reach every operator endpoint. Naming that boundary up front keeps this decision
small and reviewable.

Two constraints shape the design:

1. **The domain stays clean.** `Rod.CoreState`'s `Operator` aggregate carries
   identity only -- no password hash, no verifier, no stored-secret shape. This
   is the operator-facing twin of the stager-token hash-only rule: the stager
   service (`IStagerTokenService`) keeps a digest and never the secret, and the
   operator's password verifier lives behind its own port rather than as a field
   on the aggregate. A password is an auth concern, not an identity concern.
2. **Zero new NuGet.** The .NET shared framework already ships ASP.NET Core
   cookie authentication and `IPasswordHasher<T>` (PBKDF2 with a per-hash salt).
   Adding a third-party auth library or a JWT stack would import moving parts and
   a supply-chain surface the server does not need.

## Decision

**Establish operator sessions with cookie authentication over a verified handle
and password, and derive the operator identity on every operator endpoint from
the authenticated principal -- never from the request body or query string.**

The pieces:

- **A hash-only credential port in core state.**
  `IOperatorCredentialStore` (`src/Rod.CoreState/Operators/IOperatorCredentialStore.cs`)
  stores only the opaque hash the auth layer's password hasher produced --
  never a plaintext password -- keyed by operator id. `InMemoryOperatorCredentialStore`
  is the walking-skeleton default; the durable
  `PostgresOperatorCredentialStore`
  (`src/Rod.Persistence/Stores/PostgresOperatorCredentialStore.cs`) replaces it
  when `ConnectionStrings:Postgres` is present, through the same opt-in
  `services.Replace` swap the other eight ports use (ADR 0003). The
  `operator_credentials` table keys on `operator_id` (both primary key and
  cascading foreign key to `operators` -- one credential per operator) and holds
  `password_hash` and `updated_at`.

- **Login verifies the password and issues the cookie.**
  `OperatorAuthService.TryLoginAsync` (`src/Rod.Operators/Auth/OperatorAuthService.cs`)
  resolves the account by handle (`IOperatorRepository.FindByHandleAsync`), loads
  the stored hash, and verifies the presented password with
  `IPasswordHasher<Operator>`. On success it builds a `ClaimsPrincipal` carrying
  the operator id, handle, and display name, which `POST /operators/login`
  persists as the cookie session. An unknown handle and a wrong password fail
  closed and are indistinguishable to the caller.

- **Every operator endpoint is authorized; identity comes from the principal.**
  `POST /operators/login` is anonymous (it is how a session is established);
  `POST /operators/logout` and `GET /operators/me`, and the entire operator
  route group (engagements, tasking, implants, audit, artifacts, timeline,
  report, listeners, payloads), require an authenticated session. Each endpoint
  resolves the acting operator off the principal
  (`HttpContext.User.TryGetOperatorId()`), so the audit `OperatorId` on every
  write is the cookie operator, independent of the request body. The engagement
  Server-Sent Events stream reads identity the same way, which is why it no
  longer carries operator identity in query parameters.

- **A config-seeded first operator.** `OperatorAuthBootstrap`
  (`src/Rod.Operators/Auth/OperatorAuthBootstrap.cs`) runs as a hosted service at
  startup and provisions the `Operators:Initial` account (handle, display name,
  password) when no operator owns that handle yet, so someone can log in before
  any management path exists. It is idempotent. In **Development** a built-in dev
  account (`operator` / `operator`) is seeded when configuration supplies none --
  the same dev-default stance the implant CA takes; in **Production** there is no
  fallback, and a server configured without an initial operator simply starts
  with no loginable account.

- **The browser UI uses the session.** The React client resolves the signed-in
  operator via `GET /operators/me` on load, routes an unauthenticated browser to
  a login view, and signs out through `POST /operators/logout`. The
  self-assigned id, `localStorage`, and `crypto.randomUUID` are removed; no
  operator-identity field is sent in a request body or query string.

The cookie is `HttpOnly`, `SameSite=Lax`, with `SecurePolicy=SameAsRequest` (so
development over plain HTTP still works and production behind TLS gets the
secure flag). Composition is `AddRodOperatorAuth` + `UseAuthentication` /
`UseAuthorization` in the teamserver pipeline; the transport test host composes
the same layers.

## Rationale

- **Cookies over bearer tokens for a same-origin SPA.** The operator UI is served
  by the teamserver itself (ADR 0002), so the API is same-origin in production
  and proxied to one origin in dev. A cookie session needs no client-side token
  store, no refresh machinery, no `Authorization` header plumbing, and the
  browser manages expiry. A JWT/bearer design would add token storage, refresh,
  and revocation -- moving parts with no payoff when the client is already
  same-origin.
- **PBKDF2 via the shared framework.** `IPasswordHasher<Operator>` is vetted,
  salted per hash, and already in the box. Reaching for a third-party password
  library or a bespoke hash would add a dependency for no gain and a
  roll-your-own-crypto hazard.
- **A hash-only port keeps the blast radius small.** A stolen `operators` row
  reveals nothing useful, and a stolen credential row is offline-crackable but
  contains no plaintext. Splitting the verifier into its own store (rather than a
  column on `Operator`) means the identity aggregate never touches a
  stored-secret shape -- the same discipline the stager-token store already
  follows.
- **Config seed before a management path.** Without a first account the server
  is unreachable, so the seed is a bootstrap necessity, not a convenience. Making
  it idempotent and Development-only-fallback keeps production honest (an
  operator must be deliberately configured) while keeping local development
  frictionless.
- **Auth-only scope.** Bundling RBAC into this decision would couple identity
  verification to authorization policy and force the membership/role model to be
  designed under deadline pressure. Authentication is a clean, testable unit;
  RBAC is its own decision with its own trade-offs (engagement membership,
  roles, enforcement points) and gets its own ADR.
- **Server-derived identity is the whole point.** Auditing "who did this" is only
  as trustworthy as the binding between the session and the recorded id. By
  making the principal -- not the body -- the single source, a client cannot
  attribute a write to another operator regardless of what it sends.

## Consequences

- **Positive:** an operator session is established by verified credentials, not a
  client-generated id; the audit trail's `OperatorId` is the cookie operator,
  independent of the request body; a provisioned password survives a teamserver
  restart in the durable store; the domain `Operator` stays free of any
  stored-secret shape; no new NuGet enters the supply chain.
- **Negative:** there is **no per-engagement RBAC yet**. Every authenticated
  operator can create engagements, task any implant, retire implants, mint stager
  tokens, build payloads, and read every engagement's audit trail. This is the
  deferred scope, not an oversight; it is acceptable only behind a trusted
  operator set. There is also **no password-management UI**: changing a password
  means reconfiguring and reseeding (or a future management endpoint).
- **Risk:** a stolen cookie is a live session until it expires or the operator
  signs out -- server-side session revocation is not implemented. Mitigation: the
  cookie is `HttpOnly` (no JS access), `SameSite=Lax` (limits cross-site
  delivery), and secure in production behind TLS. A future hardening could add
  server-side session tracking and revocation, and rotate the anti-forgery/cookie
  keys on a schedule.
- **Risk:** PBKDF2 work-factor tuning is the framework default. If threat-model
  analysis later demands a memory-hard verifier (Argon2) or a higher iteration
  count, the `IPasswordHasher<T>` seam is the single swap point; the port and the
  endpoints do not change.

## Alternatives considered

- **JWT/bearer tokens.** Rejected for the same-origin SPA: they require
  client-side token storage, a refresh flow, and explicit revocation state, none
  of which the cookie design needs. Cookies are simpler and the browser manages
  the lifecycle. A bearer design would be justified if the API had to serve
  non-browser, cross-origin clients; the operator API does not.
- **Bundling per-engagement RBAC into this decision.** Rejected as scope creep.
  Authentication and authorization are separable, and the membership/role model
  deserves its own design (and its own ADR) rather than being rushed as a rider.
- **A password-hash field on the `Operator` aggregate.** Rejected: it violates
  the clean-domain / hash-only discipline (the aggregate would carry a
  stored-secret shape), and it couples identity to auth concerns. The separate
  `IOperatorCredentialStore` port keeps the two apart, exactly as the stager
  service keeps its digest out of any aggregate.
- **ASP.NET Core Identity (the full `IdentityDbContext` / user-manager stack).**
  Rejected: it imports its own EF Core data model, its own user/role/login
  tables, and a large surface that conflicts with the project's own ports and
  layered store. The project already has `IOperatorRepository`,
  `IOperatorCredentialStore`, and typed ids; layering Identity on top would
  duplicate identity storage and blur the layer boundaries the architecture tests
  enforce.
