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
`127.0.0.1:5080` so `dotnet run` works out of the box. That configuration
names the **operator front only** -- it carries no implant ingress, and
enrollment is refused on it outright. Implant-facing listeners are
**engagement-scoped**: created through the engagement's Listeners panel (or
`POST /engagements/{id}/listeners`) against exactly one engagement, persisted
so a restart rebinds them, unique across ports, and enforced at enrollment --
anything but that engagement's own token is refused whole on the socket
(architecture.md Sec 8). A payload build names its engagement's listener and
the baked endpoint comes from the listener's record; a build against a
cleartext `Http` listener also names the mTLS listener its beacon dials
(`beaconListenerId`/`beaconEndpoint` -- the split-socket shape, because the
gRPC beacon cannot ride a cleartext socket; see
[operator-ui.md](operator-ui.md)). The build also mints and
bakes the artifact's enrollment credential, so the artifact deploys with zero
run-time arguments. The manual mint endpoint stays server-side for the
rotation and re-entry drills, but it is no operator surface. The operator UI
is served same-origin at `/`; its panels and every build/listener field are
documented in [operator-ui.md](operator-ui.md). During UI development,
`npm run dev` in
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
   for the low-and-slow cadence. It appears in the fleet (grouped under the
   host it reported at enroll), takes tasking, and its results land in the
   audit trail.
5. For a live shell, open the implant's session console (its Interact link in
   the fleet) and type `interact` -- or issue `shell.interact` from the
   implant's context menu, or `POST /engagements/{id}/tasks` -- and the
   Interact pane opens: type into the
   input line and watch the transcript stream. Over the API the same loop is
   `POST /engagements/{id}/tasks/{taskId}/input` with `{"data": "<base64>"}`
   per line and `{"eof": true}` to close stdin -- the task completes with the
   whole session as its output (architecture.md Sec 10.3). Interactive
   channels need a stream-mode check-in; a poll-mode implant reports the task
   Failed with the reason.
6. To reach a host only the implant can see, issue `tunnel.forward` with
   arguments `<host> <port>`: the same Interact pane carries the tunnel --
   input posts are relayed to the peer and its answers land on the transcript,
   `eof` half-closes the send side, and the task ends when the peer closes,
   with the relay summary as its final output (architecture.md Sec 10.1).
7. To let an unmodified tool ride that tunnel, bind a relay port onto the
   dispatched task: `POST /engagements/{id}/tasks/{taskId}/relay` (body
   `{"bindAddress": "loopback", "port": 0}` -- loopback and an ephemeral port
   are the defaults) and point the tool at the returned endpoint. Its bytes
   cross the channel without a single API call per byte, the answers come
   back raw, and the bind and its close (with byte tallies) land in the
   engagement trail. On `tunnel.forward` the relay bridges one connection to
   the task's fixed destination; `DELETE /engagements/{id}/tasks/{taskId}/relay`
   ends it early (architecture.md Sec 10.1). Binding off-loopback (`any` or an
   IP literal) exposes the tunnel to every host that can reach the port --
   name the address only when the operator path needs it.
8. For arbitrary destinations through one task, issue `tunnel.socks` (no
   arguments) and bind the same relay route: the endpoint speaks SOCKS5
   (no auth, CONNECT only), so a browser pointed at it as its proxy -- or
   proxychains, or any SOCKS-configured client -- reaches whatever the
   implant can reach, each connection under its own id on the one channel.
   The task's final output is the proxy's record: the destinations dialed
   and the bytes moved (architecture.md Sec 10.1). Close it with an eof
   input post; the bind dies with the task.
9. To represent a host that cannot run an implant at all, derive a Pivot
   child from a stage-2 parent (`lateral.move` with arguments
   `<token> Pivot`) and task the child directly: its tasking executes on the
   parent's beacon stream, marked with the child's id and attributed to the
   child end to end (architecture.md Sec 5.2). The child never appears in
   presence -- it has no process -- so its liveness is the parent's.

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
| `Operators:Initial` | The first loginable account, provisioned idempotently at startup (`Handle`, `DisplayName`, `Password`). Bind the password through the environment (`Operators__Initial__Password`), never inline. | Development: the built-in `operator`/`operator` account. Production: **no account** -- a server configured without one has no login. |
| `Listeners` | The **shared tier** only: the operator front, plus any deliberately shared ingress (e.g. the certificate-less enroll edge). One entry per socket -- `Name`, `Transport` (`Http`, `Https`, `Mtls`, `Dns`, `Smb`, or `Tcp`; `Https` is the one-port shape -- TLS with the client certificate optional at the TLS layer, enrollment on the token and check-ins on the certificate, both halves on one socket), `BindAddress` (what the host opens), `PublicEndpoint` (what implants dial; typically a redirector; for a `Dns` entry it is the zone the TXT check-ins live under). mTLS/Https entries terminate TLS against the implant CA; DNS entries bind a UDP socket; `Smb`/`Tcp` entries bind a pipe or raw socket under the certificate-less identity posture. Implant-facing listeners are engagement-scoped and created through the operator API, not configuration (architecture.md Sec 8). Keep `Http` entries on loopback: the operator API and the certificate-less beacon ride them in the clear, and a non-loopback bind logs a startup warning (architecture.md Sec 8). | One loopback HTTP listener on `127.0.0.1:5080`. |
| `Audit:DataDirectory` | File-backed audit trail, artifacts, and built payloads that survive a restart. Each append writes and flushes one hash-chained record; recovery verifies each engagement's chain and refuses a tampered trail. | In-memory (lost on restart). |
| `ConnectionStrings:Postgres` | The durable PostgreSQL pair replaces the in-memory core-state and audit adapters (EF Core over Npgsql). Apply the schema with `dotnet ef database update -p src/teamserver/Rod.Persistence -s src/teamserver/Rod.TeamServer`. | In-memory. |
| `Pki` | An externally provisioned engagement CA as PEM files (`CaCertificatePath`, `CaPrivateKeyPath`, optional `CaPrivateKeyPassphrase`) -- production leaf issuance. Unparseable or mismatched material fails at startup, not at the first enrollment. RSA only. | The self-signed dev CA (key lives in process -- not for production). |
| `Sessions:Staleness` | `Threshold` and `SweepInterval` for the session sweeper -- the close path for streams that die silently and for poll-mode check-in cadences. | 15-minute threshold, 1-minute sweep. |
| `Tradecraft:Modules` | Out-of-tree capability modules, each a `Namespace.Type, AssemblyName` entry; see [extending/tradecraft.md](../extending/tradecraft.md). | Built-in placeholders only. |
| `Build:Transforms` | Out-of-tree post-build payload transforms, each a `Namespace.Type, AssemblyName` entry, applied in listed order; the fingerprint and `PayloadBuilt` audit event cover the transformed bytes. | The empty chain (no transform runs; bytes stored as built). |
| `Build:ImplantExtensionDirectory` | The tradecraft extension kit's implant half: a directory of out-of-tree handler sources overlaid onto every implant-class build ([extending/tradecraft.md](../extending/tradecraft.md)). A configured-but-missing directory fails startup loudly. | Empty -- the reference implant builds as-is. |
| `Build:ImplantSourceDirectory` / `Build:StagerSourceDirectory` | An installed teamserver (a publish with no repo above it) names the implant/stager source trees the build unit compiles at request time. | The repo walk-up a checkout uses (`src/implant/dotnet`, `src/stager/dotnet`). |

## Production install and recovery

The installed shape: a self-contained publish under `/opt/rod`, a dedicated
service user, secrets in a root-only environment file, and a systemd unit
supervising the process. Everything below is the executed install path --
each step was walked on a clean tree, then crash-restarted, upgraded, and
restored from backup before being written down.

### Install

The build host needs the pinned SDK; the deploy host needs neither SDK nor
runtime, only PostgreSQL reachability:

```
dotnet publish src/teamserver/Rod.TeamServer/Rod.TeamServer.csproj \
  -c Release -r linux-x64 --self-contained true -o /tmp/rod-publish
```

Lay the host out (the service user's home is where the DataProtection key
ring lands -- see the backup trio):

```
sudo useradd -r -d /var/lib/rod -M -s /usr/sbin/nologin rod
sudo mkdir -p /var/lib/rod/data /etc/rod/pki /opt/rod
sudo chown rod:rod /var/lib/rod /var/lib/rod/data
sudo cp -a /tmp/rod-publish /opt/rod/teamserver          # + appsettings.Production.json below
sudo cp ca.crt ca.key /etc/rod/pki/ && sudo chown root:rod /etc/rod/pki/* \
  && sudo chmod 640 /etc/rod/pki/ca.crt /etc/rod/pki/ca.key
```

Non-secret configuration rides `appsettings.Production.json` next to the
binary (the Pki paths, `Audit:DataDirectory`, and the listener shape);
apply the schema once per deployment from a checkout, not the install
tree:

```
dotnet ef database update -p src/teamserver/Rod.Persistence \
  -s src/teamserver/Rod.TeamServer --connection "<rod connection string>"
```

The unit (`/etc/systemd/system/rod-teamserver.service`):

```ini
[Unit]
Description=Rod teamserver (C2 kernel)
After=network-online.target postgresql.service
Wants=network-online.target

[Service]
User=rod
Group=rod
Environment=ASPNETCORE_ENVIRONMENT=Production
EnvironmentFile=/etc/rod/teamserver.env
ExecStart=/opt/rod/teamserver/Rod.TeamServer
WorkingDirectory=/opt/rod/teamserver
StateDirectory=rod
Restart=on-failure
RestartSec=2
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=/var/lib/rod

[Install]
WantedBy=multi-user.target
```

`sudo systemctl daemon-reload && sudo systemctl enable --now
rod-teamserver`, then accept: `systemctl is-active` reports active and
`POST /operators/login` on the operator listener answers 200. After
debugging a crash-looping start, clear the rate limit with `systemctl
reset-failed rod-teamserver` before starting again.

### Build sources and the operator UI

Two more install-shape facts, both walked end to end on the supervised
install:

**Payload builds compile from source at request time**, and the install
tree has no repo above it, so the deployment names its build source trees
with `Build:ImplantSourceDirectory` / `Build:StagerSourceDirectory`
(a configured-but-missing directory fails startup loudly). The minimal
deployed tree both keys can point at:

```
/opt/rod/src/
  Directory.Build.props  Directory.Packages.props  global.json
  src/implant/dotnet/    src/stager/dotnet/        # build sources, no bin/obj
  src/teamserver/Rod.Protocol/protos/              # the wire contract the implant compiles against
  tests/                 # the repo-root marker the build unit walks up to
```

The service user also needs a `dotnet` on PATH and a warm NuGet cache
(`/var/lib/rod/.nuget/packages`) -- the build spawns `dotnet publish`
and restores into that cache. On a host without registry egress, copy
the cache from the build host at install time.

Acceptance from the executed walk: a stage-2 built through the
supervised install returned its fingerprint, and the downloaded
artifact's sha256 matched it exactly.

**The operator UI is a build-time bundle**: with Node 22.12+ on the
build host, `npm ci && npm run build` in
`src/teamserver/Rod.TeamServer/Client` emits `wwwroot/`, and the
publish carries it -- without Node the install runs API-only. The
executed walk logged into the installed UI, listed the engagements,
opened one, and saw the live presence channel mark its own operator
session online.

### Build provenance

The publish stamps its provenance into the binaries: the product version
(`RodVersion` in `Directory.Build.props`) and the exact source commit the
binary was built from, resolved from git HEAD at build time. A release is a
git tag `v<RodVersion>` cut from the tree it was built from; the stamp is
what ties an installed binary back to that tag -- not a hand-declared source
path in configuration.

The installed teamserver reports the pair two ways:

- at startup, as the first log line -- `Rod teamserver 1.0.0
  (commit <sha>)` in `journalctl -u rod-teamserver`;
- on request, over the operator API, behind a session:

```
curl -s -c jar.txt -H 'Content-Type: application/json' \
  -d '{"handle":"lead","password":"<from the secret store>"}' \
  http://<operator listener>/operators/login > /dev/null
curl -s -b jar.txt http://<operator listener>/build
# -> {"version":"1.0.0","sourceCommit":"<sha>"}
```

Accept an install only when the stamp matches the tag it was cut from. The
deployed redirector reports the same pair via `rod-redirector -version`
([redirectors.md](redirectors.md)). The implant and stager are deliberately
unstamped: a captured artifact must not carry the teamserver's source
provenance.

### Secrets

`Operators__Initial__Password` and `Pki__CaPrivateKeyPassphrase`
([architecture.md](../architecture.md) Sec 9) never live in the
world-readable appsettings. They ride the root-only environment file the
unit injects (`/etc/rod/teamserver.env`, mode 0600):

```
Operators__Initial__Handle=lead
Operators__Initial__DisplayName=Engagement Lead
Operators__Initial__Password=<from the secret store>
ConnectionStrings__Postgres=<rod connection string>
```

The **whole** `Operators__Initial` section must be present: outside
Development there is no fallback account, and an incomplete section
provisions no operator -- the first symptom is a 401 on login. The
section is read once at first boot and never re-read; rotate credentials
through the operator API, not by editing the file.

### Upgrade

Stop, replace, start:

```
sudo systemctl stop rod-teamserver
sudo rm -rf /opt/rod/teamserver && sudo cp -a /tmp/rod-publish /opt/rod/teamserver
sudo cp appsettings.Production.json /opt/rod/teamserver/
sudo systemctl start rod-teamserver
```

Sessions survive: the operator cookie is sealed by the DataProtection
key ring and stamp-checked against the credential store, and both are
durable state outside the process. The same is true of a crash --
`Restart=on-failure` brings the process back (verified with `kill -9`:
new pid, cookie still accepted) while the fronts keep splicing and
implants retry.

### Backup and restore: the trio that moves together

Three pieces of durable state must move as a set. Restoring any one
alone yields broken logins or lost evidence:

1. **The Postgres dump** -- operators, engagements, tasks: everything
   the durable core-state pair owns (`ConnectionStrings:Postgres`).
2. **The evidence data directory** (`Audit:DataDirectory`) -- the
   hash-chained audit trail, artifacts, and the payload store.
3. **The DataProtection key ring** --
   `/var/lib/rod/.aspnet/DataProtection-Keys` under the service user's
   home (the teamserver pins no path, so the ASP.NET Core default
   applies). It seals operator cookies and API tokens; a fresh ring
   silently invalidates every issued cookie.

Take the set with the service stopped, so the three members agree:

```
sudo systemctl stop rod-teamserver
pg_dump -h <host> -U rod rod > rod.sql
sudo tar -C /var/lib/rod -czf data.tgz data
sudo cp -a /var/lib/rod/.aspnet aspnet-keys
```

Restore onto the wiped host (drop and recreate the empty database
first; the dump carries the schema):

```
sudo systemctl stop rod-teamserver
sudo -u postgres psql -c "DROP DATABASE rod;" -c "CREATE DATABASE rod OWNER rod;"
psql -h <host> -U rod -d rod -f rod.sql
sudo rm -rf /var/lib/rod/data /var/lib/rod/.aspnet
sudo tar -C /var/lib/rod -xzf data.tgz
sudo cp -a aspnet-keys /var/lib/rod/.aspnet
sudo chown -R rod:rod /var/lib/rod/data /var/lib/rod/.aspnet
sudo systemctl start rod-teamserver
```

Accept: a pre-backup operator cookie authenticates the first request
after the restore, and the engagement roster reads back. The failure
modes are structural, not flaky: the dump without the key ring leaves
the old cookie unreadable (401), the key ring without the dump fails the
per-request stamp check against the credential store (401), and the
data directory without the database orphans the audit chain the report
reads.

The restore has its own health check: at startup the teamserver verifies
the database against the persistence model -- every mapped table present
and carrying its primary key -- and aborts the boot naming the offending
tables otherwise. This closes a failure class the win-x64 surface walk
caught live: a database carrying every table and row but none of its
constraints boots apparently healthy and dies at the first task
dispatch, where the replay-nonce reservation's ON CONFLICT meets its
missing arbiter. Verify a restore the same way the install verifies a
first boot: `systemctl is-active` plus one login, and the schema guard
has already vouched for the store underneath.

## Closing out an engagement

A finished engagement leaves through the close-out (architecture.md Sec 2
step 10, Sec 11): freeze, export the evidence package, verify it, retire.
All three are operator API actions over the operator listener, logged in the
session cookie:

```
# 1. Freeze: the engagement stops accepting new tasking, enrollments, and
#    token mints, so its trail is final. In-flight results still land.
curl -s -b jar.txt -X POST http://<operator listener>/engagements/<id>:freeze

# 2. Export: one ZIP with audit.jsonl (the full hash-chained trail),
#    artifacts.jsonl, report.json + report.md, and the manifest that pins
#    every file's bytes.
curl -s -b jar.txt -X POST \
  http://<operator listener>/engagements/<id>:evidence-package \
  -o rod-evidence-<id>.zip

# 3. Verify offline -- on any host, with nothing Rod running on it. The
#    binary recomputes every digest, re-runs the chain check, and validates
#    the artifact records; exit 0 is a verified package.
dotnet Rod.TeamServer.dll --verify-evidence rod-evidence-<id>.zip

# 4. Retire: terminal, and refused until the engagement is frozen.
curl -s -b jar.txt -X POST http://<operator listener>/engagements/<id>:retire
```

Acceptance from the walk above: the exported package re-verifies byte-exact
on a host with no Rod infrastructure running, and every step of the close-out
(`EngagementFrozen`, `EvidenceExported`, `EngagementRetired`) is itself an
audited, attributed event in the trail the package carries. The exported
trail ends one event before the export's own record -- a later re-export
(before retire) carries it, so late-landing results are not lost.

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
- Pin the install to its source: accept a deployment only after the build
  stamp matches the release tag it was cut from (see "Build provenance"
  above) -- the binaries name their commit, so provenance is read off the
  running system, not off deployment notes.
- Before pointing the stack at a client network, walk the full lifecycle on
  the production shape once -- the procedure and its acceptance evidence
  live in [rehearsal.md](rehearsal.md).
