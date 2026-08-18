# Teamserver -- stand up, configure, run

The operational runbook for the Rod teamserver: build, first login, the
configuration reference, and the durability options. The design rationale lives
in [architecture.md](../architecture.md); this file is about running the thing.

## Prerequisites

- .NET SDK 10, pinned by `global.json` at the repo root.
- Node.js 22.12+ only for the operator UI (a backend-only checkout runs
  without it; the host explains at request time how to build the bundle).
- PostgreSQL, only when you opt into durable state (below).

## Build and run

```
dotnet build Rod.slnx     # builds the teamserver; bundles the UI when wwwroot is missing
dotnet run --project src/teamserver/Rod.TeamServer
```

With no `Listeners` configuration the host binds one loopback HTTP listener on
`127.0.0.1:5080` so `dotnet run` works out of the box. The operator UI is
served same-origin at `/`; during UI development, `npm run dev` in
`src/teamserver/Rod.TeamServer/Client` proxies the API to :5080.

## First login

The initial operator is provisioned at startup from the `Operators:Initial`
section and is idempotent (an existing account with a password is never
touched):

```json
{
  "Operators": {
    "Initial": {
      "Handle": "lead",
      "DisplayName": "Engagement Lead",
      "Password": "set-via-environment-in-production"
    }
  }
}
```

In Development a built-in account (`operator` / `operator`) applies when
configuration supplies none. In Production there is **no fallback**: a server
configured without an initial operator starts with no loginable account, and
operators must be provisioned by configuration. Bind the password via
environment (`Operators__Initial__Password`) or a secret store, never inline.

## The dev loop

1. Log in at the UI (or `POST /operators/login`).
2. Create an engagement; mint a stager token.
3. Build a payload for it (class, target OS/arch, beacon profile, malleable
   transport) and download the artifact from the payload store.
4. Run the reference implant for a quick end-to-end check:
   `dotnet run --project src/implant/dotnet -- -enroll-url
   http://127.0.0.1:5080/implants/enroll -token <secret>`, or add `-mode poll`
   for the low-and-slow cadence. It appears on the engagement roster, takes
   tasking, and its results land in the audit trail.

For staged deployment, build the stage-2 first, then build a second payload
with class `stager` naming it (`stage2PayloadId`). The stager is a small
loader: run it with the deployment credential
(`./Rod.Stager -token <secret>`; `-beacon-url`/`-ca-cert` when the beacon sits
behind a different frontend) and it fetches the stage-2 from the teamserver,
verifies the fingerprint baked at build time, runs it, and hands the credential
over -- the stage-2 enrols and appears on the roster. The fetch verifies the
token without spending it; the stage-2's enroll spends it.

## Configuration reference

Opt-in sections of `appsettings.json` (environment variables work through the
standard `Section__Key` mapping):

| Section | What it selects | Default when absent |
|---------|-----------------|---------------------|
| `Listeners` | C2 ingress: one entry per socket -- `Name`, `Transport` (`Http`, `Mtls`, or `Dns`), `BindAddress` (what the host opens), `PublicEndpoint` (what implants dial; typically a redirector; for a `Dns` entry it is the zone the TXT check-ins live under). mTLS entries terminate mutual TLS against the implant CA; DNS entries bind a UDP socket. | One loopback HTTP listener on `127.0.0.1:5080`. |
| `Audit:DataDirectory` | File-backed audit trail, artifacts, and built payloads that survive a restart. Each append writes and flushes one hash-chained record; recovery verifies each engagement's chain and refuses a tampered trail. | In-memory (lost on restart). |
| `ConnectionStrings:Postgres` | The durable PostgreSQL pair replaces the in-memory core-state and audit adapters (EF Core over Npgsql). Apply the schema with `dotnet ef database update -p src/teamserver/Rod.Persistence -s src/teamserver/Rod.TeamServer`. | In-memory. |
| `Pki` | An externally provisioned engagement CA as PEM files (`CaCertificatePath`, `CaPrivateKeyPath`, optional `CaPrivateKeyPassphrase`) -- production leaf issuance. Unparseable or mismatched material fails at startup, not at the first enrollment. RSA only. | The self-signed dev CA (key lives in process -- not for production). |
| `Sessions:Staleness` | `Threshold` and `SweepInterval` for the session sweeper -- the close path for streams that die silently and for poll-mode check-in cadences. | 15-minute threshold, 1-minute sweep. |
| `Tradecraft:Modules` | Out-of-tree capability modules, each a `Namespace.Type, AssemblyName` entry; see [extending/tradecraft.md](../extending/tradecraft.md). | Built-in placeholders only. |
| `Build:Transforms` | Out-of-tree post-build payload transforms, each a `Namespace.Type, AssemblyName` entry, applied in listed order; the fingerprint and `PayloadBuilt` audit event cover the transformed bytes. | The empty chain (no transform runs; bytes stored as built). |

## Production posture

- Terminate the beacon on an **mTLS listener** and front it with a redirector;
  the listener's public endpoint is repointable at runtime so burned
  infrastructure swaps without touching the backend
  ([redirectors.md](redirectors.md)).
- Configure the **external CA**, **Postgres**, and an **operator account from
  a secret store**; run with no Development fallback.
- Every privileged action lands in the engagement-scoped, hash-chained audit
  trail regardless of configuration -- keep `Audit:DataDirectory` (or
  Postgres) on durable storage, since the trail is the report source and
  outlives the operation (architecture.md Sec 11).
