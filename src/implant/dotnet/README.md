# Rod.Implant -- reference .NET stage-2 implant (M3.3)

The reference **stage-2 implant for Windows** in the .NET language
([architecture.md Sec 5](../../../docs/architecture.md)). It is a benign, readable
implant that enrolls over the teamserver's HTTP enroll endpoint (submitting its
own public key), beacons over mTLS, and runs the `shell.exec` core verb.

It performs **no evasion, no obfuscation, no persistence, and no destructive
behavior** ([RESPONSIBLE-USE.md](../../../RESPONSIBLE-USE.md),
[architecture.md Sec 7](../../../docs/architecture.md)). It exists to prove the
end-to-end slice -- enroll, beacon, task -- against the real teamserver, and to
give the `.NET build unit` (`Rod.DotNetBuildUnit` in `Rod.BuildPipeline`)
something real to compile per payload-build request.

This project is an **external component**, coupled to the teamserver only by the
wire protocol: it is intentionally not part of `Rod.slnx`, mirroring how the Go
[`implant/`](../../../implant/) tree is its own module. The protobuf/gRPC wire bindings
are generated at build time from the canonical teamserver proto
(`src/teamserver/Rod.Protocol/protos/rod.proto`) via `Grpc.Tools`; no generated code is
committed, `rod.proto` is the single source of truth.

## Build

The implant is built by `Rod.DotNetBuildUnit`, which runs `dotnet publish` over
this tree and bakes the per-implant profile into a generated source file. To
build it directly for development:

```
dotnet build Rod.Implant.csproj
dotnet run --project Rod.Implant.csproj -- -enroll-url http://127.0.0.1:5080/implants/enroll -token <stager-token>
```
