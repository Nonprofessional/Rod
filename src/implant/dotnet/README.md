# Rod.Implant -- reference .NET stage-2 implant

The reference **stage-2 implant** in the .NET language, cross-platform
([architecture.md Sec 5](../../../docs/architecture.md)). It is a benign, readable
implant that enrolls over the teamserver's HTTP enroll endpoint (submitting its
own public key), beacons over mTLS with the baked-in mode/sleep/jitter/kill-date
profile (`-mode stream` holds the connection, `-mode poll` checks in on the
baked cadence), and runs the standard-category verb set: shell exec, file transfer
(push/pull), recon, lateral (child derivation, token inspection, remote exec
over admin channels), persistence (Run key / scheduled tasks / services / cron
/ systemd), credential-store enumeration, and exfil to the engagement artifact
store (architecture.md Sec 10.1).

It performs **no evasion, no obfuscation, and no destructive behavior**
([RESPONSIBLE-USE.md](../../../RESPONSIBLE-USE.md),
[architecture.md Sec 7](../../../docs/architecture.md)); keyboard capture and
LSASS dumping stay out-of-tree by the Sec 13 boundary. It exists to prove the
end-to-end slice -- enroll, beacon, task -- against the real teamserver and to
give the .NET build unit (`DotNetBuildUnit` in `Rod.BuildPipeline`)
something real to compile per payload-build request.

## Capabilities

Dispatch and advertising are registry-driven
([architecture.md Sec 5.3](../../../docs/architecture.md)). The beacon loop
calls `HandlerRegistry` -- the implant analog of the server's
`ICapabilityModule` -- and advertises the baked class verb set (the build
unit's `verbs` profile key, read via `ROD_VERBS`) intersected with the
compiled handlers, so the implant never advertises a verb it cannot run. A dev
build with no bake advertises its full compiled set. Adding a verb is a handler
plus one registration in `HandlerRegistry.Default`, never an edit to the
beacon loop; the reference registry carries no Sec 13 boundary verb.

This project is an **external component**, coupled to the teamserver only by the
wire protocol: it is intentionally not part of `Rod.slnx`. The protobuf/gRPC
wire bindings are generated at build time from the canonical teamserver proto
(`src/teamserver/Rod.Protocol/protos/rod.proto`) via `Grpc.Tools`; no
generated code is committed, `rod.proto` is the single source of truth.

## Build

The implant is built by `DotNetBuildUnit`, which runs a self-contained
single-file `dotnet publish` for the requested target OS/arch over this tree
and bakes the per-implant profile into a generated source file -- the produced
binary runs on a stock target with no .NET installed. To build it directly for
development (framework-dependent, from the source tree):

```
dotnet build Rod.Implant.csproj
dotnet run --project Rod.Implant.csproj -- -enroll-url http://127.0.0.1:5080/implants/enroll -token <stager-token>
```

Add `-mode poll` for the low-and-slow check-in cadence (sleep/jitter between
check-ins instead of a persistent connection); the default `-mode stream` is the
interactive shape.
