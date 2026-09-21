<p align="center"><img src="docs/assets/rod-logo.png" alt="Rod" width="200"></p>

<h1 align="center">Rod</h1>

<p align="center">
  An <b>authorized-use red-team command-and-control (C2) platform</b> for
  penetration tests, red-team operations, and security research.<br>
  One teamserver, a fleet of disposable implants, hosts behind NAT and
  firewalls over implant-initiated connections --<br>
  and an audit trail that becomes the report.
</p>

<p align="center">
  <a href="https://github.com/Nonprofessional/Rod/actions/workflows/ci.yml"><img src="https://github.com/Nonprofessional/Rod/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache--2.0-blue" alt="License: Apache-2.0"></a>
</p>

> **Authorized use only.** Rod is remote-code-execution infrastructure. It
> must **only** be used against systems and networks you own or are
> **expressly authorized** to test. Unauthorized use is illegal in most
> jurisdictions.

> **Status: implemented; sensitive tradecraft is out-of-tree.** The
> teamserver, reference implant, build pipeline, operator UI, and durable
> state are in place; the reference implant runs the standard, documented
> capability set (shell, file transfer, recon, lateral movement,
> persistence, collection, exfiltration, tunneling), while exploits,
> evasion, LSASS dumping, and input capture remain contracts that separate
> opt-in modules implement ([architecture.md Sec 13](docs/architecture.md)).

## What you get

- **Eight transports, one task grammar.** `http`, `https`, `mtls`, `dns`,
  `smb`, `tcp`, `quic`, and `doh` listeners ship in-tree; every verb works
  over every carrier, with live channels on the stream transports and
  store-and-forward channels on the poll transports.
- **Disposable implants with baked profiles.** Each implant generates its
  own keypair at first run (no key material ships in the artifact), carries
  a build-time profile -- contact mode, beacon cadence and jitter, kill
  date, transport shape -- and enrolls on a one-time credential baked at
  build, so a dropped artifact needs zero run-time arguments.
- **Web shells and caught reverse shells.** The build panel renders
  placement scripts in PHP, JSP, ASPX, or classic ASP -- sealed under a
  baked AES-256-GCM key, or in the universal eval shape -- and a placed
  script registers as an implant driven through the same tasking and audit
  surface. A `shellcatch` listener catches raw reverse-shell one-liners
  (`nc`, bash `/dev/tcp`, ...), fingerprints the OS, and hands back
  paste-ready lines that upgrade the catch into a full implant.
- **A stager for delivery discipline.** First contact runs a tiny stage-1
  loader that fetches the stage-2 artifact, verifies it against the sha256
  baked at build, and runs it ([architecture.md Sec 5.2, Sec 6](docs/architecture.md)).
- **Polyglot by contract.** The wire protocol is the product: the .NET
  reference implant is one implementation, and Go, C/C++, or Nim implants
  build against the same language-neutral contract without coupling the
  teamserver to their toolchains.
- **A multiplayer operator console.** A React web UI -- fleet view, tasking,
  interactive shells, file and process browsers, listeners, payload and
  webshell builds, evidence panels -- with server-sent-event updates and
  every action attributed to the operator who took it.
- **Evidence as a first-class output.** A hash-chained audit trail, the
  engagement timeline, generated reports (JSON + Markdown), and a close-out
  evidence package: what happened, who did it, and what it proved.
- **Swappable infrastructure.** Redirectors are near-stateless .NET Native
  AOT single binaries fronting the listeners; a burned one is replaced, not
  mourned.

## The shape of an operation

Everything is scoped to an **Engagement** -- the unit of tenancy,
isolation, authorization, and evidence. Implants enrol into exactly one
engagement; data, tasking, artifacts, and audit records never cross that
boundary, by construction. An engagement runs the red-team lifecycle:
stand up listeners, build payloads, operate the fleet (shell, transfer,
recon, lateral movement, persistence, collection, exfiltration,
tunneling), then close out -- freeze, export the evidence package, retire.

## Components

| Component | Stack | Notes |
|-----------|-------|-------|
| Teamserver | .NET 10 (LTS), ASP.NET Core, gRPC | Monolithic kernel, six internal layers. |
| Operator UI | React 19, Vite | Lives in the teamserver project; served same-origin. |
| Implants | .NET reference; Go/C/C++/Nim out-of-tree | Short-lived, disposable; implant-generated keys. |
| Web shells | PHP, JSP, ASPX, classic ASP scripts | Placement scripts with baked credentials; synchronous tasking. |
| Stager | .NET | Fetch-and-exec only; verifies stage-2 against the baked sha256. |
| Build units | .NET in-tree; others out-of-tree | Language-neutral build contract ([architecture.md Sec 12.2](docs/architecture.md)). |
| Redirectors | .NET Native AOT, single static binary | Tiny VPS footprint; no runtime install. |
| Data store | In-memory and file-backed by default; PostgreSQL opt-in | `ConnectionStrings:Postgres` switches in durable state and audit. |

## Quick start

### Development

Prerequisites: .NET SDK 10 (pinned by `global.json`) and Node.js 22.12+
for the operator UI.

```
dotnet build Rod.slnx     # builds the teamserver and the operator UI
dotnet run --project src/teamserver/Rod.TeamServer
```

1. Open `http://127.0.0.1:5080` and sign in as `operator` / `operator` --
   the built-in Development account that applies whenever the `Operators`
   configuration section supplies no initial operator.
2. Create an engagement, then its listener in the engagement's Listeners
   panel. A listener is the engagement's private implant ingress --
   persisted and rebound on restart; the operator front refuses implant
   traffic.
3. Build a payload naming that listener. The build mints the enrollment
   credential and bakes it in; deploy the artifact anywhere in scope and it
   enrolls on run.
4. To try the fleet without a build, run the source-tree dev implant: mint
   a token (`POST /engagements/{id}/stager-tokens`) and run
   the Rust implant (`src/implant/rust`) with `ROD_ENROLL_URL=<listener endpoint>/implants/enroll
   -token ...`.

The full lifecycle walk -- single-host and multi-host runs, the win-x64
adversarial surface walk, the CA rotation drill, with acceptance evidence
at every step -- is [docs/operations/rehearsal.md](docs/operations/rehearsal.md).

### Production

The installed shape (the executed path, walked end to end in
[docs/operations/teamserver.md](docs/operations/teamserver.md) -- install,
upgrade, backup/restore, posture):

```
dotnet publish src/teamserver/Rod.TeamServer/Rod.TeamServer.csproj \
  -c Release -r linux-x64 --self-contained true -o /tmp/rod-publish
```

- Self-contained publish under `/opt/rod`, a dedicated `rod` service user,
  and a systemd unit with the hardening flags; secrets ride a root-only
  environment file (`Operators__Initial__*`, the CA key passphrase, the
  PostgreSQL connection string).
- **Outside Development there is no fallback login.** The first operator
  comes from the `Operators:Initial` section -- the whole section, read
  once at first boot; rotate credentials through the operator API.
- Durable state: PostgreSQL for authoritative state and audit
  (`ConnectionStrings:Postgres`, schema applied once via
  `dotnet ef database update`), a data directory for artifacts and built
  payloads, and the engagement CA (PEM cert + key) under `/etc/rod/pki`.
- Payload builds compile from source at request time: the deployment names
  the implant/stager source trees (`Build:ImplantSourceDirectory`,
  `Build:StagerSourceDirectory`) and keeps the service user's NuGet cache
  warm.
- Binaries stamp their version and source commit (startup log line and
  `GET /build`); accept an install only when the stamp matches the tag it
  was cut from.

Configuration is opt-in sections of `appsettings.json`:

| Section | Effect |
|---------|--------|
| `ConnectionStrings:Postgres` | Durable teamserver state in PostgreSQL, including listener definitions. |
| `Audit:DataDirectory` | File-backed audit trail, artifacts, and built payloads that survive a restart. |
| `Pki` | An externally provisioned engagement CA (PEM cert + key); omit for the dev self-signed CA. |
| `Listeners` | The shared tier only (the operator front). Implant-facing listeners are engagement-scoped, created through the API. |
| `Operators` | Production operator provisioning (`Operators:Initial`); no Development fallback outside Development. |
| `Build` | Deployed build-source trees for request-time payload compilation. |

## Documentation

The doc tree, by what you came for:

- **The design** -- [docs/architecture.md](docs/architecture.md) is the
  blueprint: lifecycle, engagement model, kernel layers, implants and
  profiles, build pipeline, OPSEC, transports, security, and the
  sensitive-capability boundary.
- **Building against Rod** (`docs/extending/`) --
  [implants.md](docs/extending/implants.md) is the wire reference a
  from-scratch implant builds against;
  [transports.md](docs/extending/transports.md) is the contract a new C2
  carrier registers under, in-tree or out;
  [tradecraft.md](docs/extending/tradecraft.md) is how out-of-tree
  capability modules plug in.
- **Running Rod** (`docs/operations/`) --
  [teamserver.md](docs/operations/teamserver.md) is the stand-up,
  configuration, and production runbook;
  [operator-ui.md](docs/operations/operator-ui.md) is the operator
  console's panels and fields;
  [redirectors.md](docs/operations/redirectors.md) is the redirector
  build/deploy/rotate runbook;
  [rehearsal.md](docs/operations/rehearsal.md) is the pre-deployment
  rehearsal walk.
- **Project state** -- [docs/todo.md](docs/todo.md) tracks open work;
  [docs/glossary.md](docs/glossary.md) holds terminology;
  [SECURITY.md](SECURITY.md) covers vulnerability reporting and scope.

## Sensitive tradecraft stays out of the core

Exploit behavior, detection evasion, LSASS dumping, and input capture are
**pluggable capability contracts**: the core defines their interfaces and
dispatch and ships no concrete techniques or in-the-wild PoCs. The
boundary rule -- standard, documented techniques in-tree; novel
tradecraft out-of-tree -- is spelled out in
[architecture.md Sec 13](docs/architecture.md).

Delivery (phishing, host interaction) is likewise out of scope: Rod
ingests the first callback and correlates it to the engagement. Rod
commands authorized targets; it is not a general-purpose platform.

## License

Licensed under the [Apache License, Version 2.0](LICENSE).
