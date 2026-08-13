# Redirectors -- build, deploy, rotate

Operational runbook for the in-tree reference redirector, the **opaque L4 TCP
forwarder** that fronts a teamserver listener ([architecture.md](../architecture.md)
Sec 7 and Sec 8). The
forwarder source is at
[../../src/redirector/dotnet/](../../src/redirector/dotnet/).

The reference redirector does one thing: accept a TCP connection on its public
endpoint and splice the byte stream to the listener's bind address, in both
directions, with correct half-close. It never terminates TLS, never inspects or
alters the payload, and never enforces engagement tenancy. That is the whole
point -- it carries the mTLS beacon channel (HTTP/2 + client cert) and the HTTPS
enroll request through end to end, so the client-certificate authentication the
security model depends on is preserved.

This is the deploy/rotate half of the "Redirector deployment story." The
teamserver-side half -- repointing a listener's public endpoint without touching
the backend bind -- already shipped at M4.4. Together they let an operator swap
a burned redirector **end to end**: deploy a fresh forwarder, repoint the
listener, decommission the old host. No backend restart, no bind change, no
service interruption to live implants.

## 1. Build the forwarder binary

The redirector targets a single static native binary with no runtime install
(architecture.md Sec 12.2). Publish it for the redirector host's RID:

```
dotnet publish src/redirector/dotnet/Rod.Redirector.csproj -r linux-x64 -c Release
# -> src/redirector/dotnet/bin/Release/net10.0/linux-x64/publish/rod-redirector
```

`PublishAot` is set in the csproj, so a `publish` with a RID is all it takes.
CI publishes the binary on every change to prove the AOT property holds. For a
non-`linux-x64` target, pass the matching RID (`linux-arm64`, etc.).

The published `rod-redirector` is a single ~2 MB stripped ELF with no runtime
dependency beyond libc -- copy just that one file to the host.

## 2. Deploy a redirector

Each redirector fronts exactly one listener on one port (v1 is single-rule per
process; multi-port fronting is one process per port). You need:

- The **public endpoint** implants should dial, e.g. `203.0.113.10:443` (the
  redirector host's address and the port you will listen on).
- The **upstream** -- the teamserver listener's bind address, e.g.
  `10.0.0.5:8443`. This is the address the forwarder splices to, and it must be
  reachable from the redirector host. It is *not* the public endpoint.

Copy the binary and run it:

```
rod-redirector -listen 0.0.0.0:443 -upstream 10.0.0.5:8443
```

Flags (each has an `ROD_*` env fallback; flags win over env):

| Flag        | Env           | Meaning                                                                  |
| ----------- | ------------- | ----------------------------------------------------------------------- |
| `-listen`   | `ROD_LISTEN`  | bind endpoint `host:port`; host may be `*` / `0.0.0.0` / `::` or an IP. |
| `-upstream` | `ROD_UPSTREAM`| teamserver listener endpoint `host:port`; host may be a DNS name.       |
| `-allow`    | `ROD_ALLOW`   | optional comma-separated source CIDR allow-list. Empty allows all.      |

`Ctrl-C` / `SIGTERM` stop the accept loop and let in-flight copies drain.

### Running under systemd

A minimal unit (`/etc/systemd/system/rod-redirector.service`) so the forwarder
restarts on crash and starts at boot:

```ini
[Unit]
Description=Rod redirector (L4 TCP forwarder)
After=network-online.target
Wants=network-online.target

[Service]
ExecStart=/usr/local/bin/rod-redirector -listen 0.0.0.0:443 -upstream 10.0.0.5:8443
Environment=ROD_ALLOW=198.51.100.0/24
Restart=on-failure
# Run as an unprivileged user; the binary needs no special privileges for port
# 443 only if the host allows it (e.g. `sysctl net.ipv4.ip_unprivileged_port_start=443`
# or CAP_NET_BIND_SERVICE on the binary). Otherwise listen on a high port and
# NAT 443 to it at the firewall.
User=rod
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
```

### Firewall and allow-list

The redirector's `-allow` CIDR list is the only routing an L4 forwarder can do,
and it is a deployment-time tightening -- **not the security boundary**. The
real identity gate is the teamserver's mTLS handshake (Sec 9): a connection that
reaches the listener without a valid client certificate is refused at the
handshake regardless of what the redirector forwarded. Use the host firewall and
`-allow` together to keep the redirector off public port-scans and to limit the
connection surface; treat engagement identity as the teamserver's job.

## 3. Wire the listener to the redirector

The listener's **public endpoint** is the address implants dial -- the
redirector. Its **bind address** is the socket the teamserver owns and the
redirector splices to. These are decoupled (Sec 8): set the listener's public
endpoint to the redirector host (`203.0.113.10:443` in the example above) and
leave the bind address as the upstream you passed to `-upstream`.

If the listener already exists, repoint it (see the rotation flow below). If you
are standing up a new listener, set its public endpoint to the redirector at
creation.

## 4. Verify the forwarder

Before pointing live traffic at a freshly deployed redirector, confirm it splices
both directions. From a host that can reach the redirector's public endpoint:

```
# The enroll route is HTTPS and mTLS-protected, so a plain TCP connect is enough
# to prove the forwarder reached the teamserver's TLS listener:
openssl s_client -connect 203.0.113.10:443 -servername <expected-host>
# Expect a TLS handshake response from the teamserver listener (the redirector
# is transparent at L4).
```

From the teamserver side, `GET /listeners` shows the listener's `publicEndpoint`
pointing at the redirector and `state` healthy. New implants enrolled against the
redirector's endpoint should open sessions.

## 5. Rotate a burned redirector (the end-to-end swap)

This is the acceptance criterion for the "Redirector deployment story": a burned
redirector is swapped **end to end**, not just in the registry. The flow:

1. **Deploy the replacement** (redirector B) following Sec 2, pointed at the same
   upstream listener bind address. B is now serving on its own public endpoint
   (e.g. `198.51.100.20:443`) but nothing is dialed against it yet.

2. **Repoint the listener** to B. The teamserver-side half (M4.4):

   ```
   POST /listeners/{id}:repoint
   Content-Type: application/json

   { "publicEndpoint": "198.51.100.20:443" }
   ```

   The endpoint requires an authenticated operator session. The Kestrel bind is
   untouched; the registry's public-endpoint lookup now resolves B and no longer
   resolves A. New enrollments and new-beacon dials use B immediately.

3. **Verify** through B (Sec 4) and confirm live implants continue to beacon.
   Implants already connected through A keep their in-flight TCP connections
   until those connections close; the next dial goes to B because the listener's
   public endpoint has moved.

4. **Decommission the burned host (A):** stop the forwarder, tear down the host,
   revoke any credentials that lived on it. A no longer resolves to any
   listener, so even if an implant redials it, the connection goes nowhere -- the
   burned endpoint is severed, not just hidden.

No teamserver restart, no backend bind change, no service interruption: the only
thing that moved is the public endpoint implants dial.

## 6. Security notes

- **The redirector is an untrusted edge.** It has no teamserver credentials, no
  engagement keys, and no access to the payload beyond the opaque bytes it
  forwards -- which are TLS-protected in transit. Treat it as disposable: a
  compromised redirector should yield nothing but a TCP splice. (The future
  Sealing layer, Sec 9, will make this explicit end to end.)
- **Identity is the teamserver's job.** mTLS authenticates the implant at the
  listener; the redirector cannot and does not participate. Never relax the
  mTLS requirement to accommodate a redirector -- if a deployment needs to, it
  has chosen the wrong layer to terminate trust.
- **Allow-list hygiene.** Keep `-allow` as tight as the engagement permits and
  revisit it when the source ranges change. An empty allow-list (allow all) is
  acceptable when a host firewall already restricts the source; do not run both
  open.
- **One process per port.** Each fronted port is its own process (Sec 8). A
  burned port takes down only that process; the others keep serving.
- **No payload awareness, by design.** This forwarder cannot do malleable
  `User-Agent`/URI routing or serve a cover site, because all of that lives
  inside TLS and is invisible at L4. A deployment that needs L7 routing or a
  cover site terminates TLS at its own edge, in front of (or instead of) this
  forwarder. That is an operator deployment concern, not an in-tree capability
  (architecture.md Sec 8).

## See also

- [architecture.md Sec 8](../architecture.md) -- the redirector design
  decision (L4 opaque forwarder, Native AOT, alternatives considered).
- [architecture.md](../architecture.md) Sec 7 and Sec 8 -- transports,
  listeners, and the redirector abstraction.
- [../../src/redirector/dotnet/README.md](../../src/redirector/dotnet/README.md)
  -- flags, env, and build for the forwarder binary.
