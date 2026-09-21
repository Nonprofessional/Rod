# Rod -- Writing out-of-tree tradecraft

How a third party adds capabilities to Rod without touching the core tree --
including the sensitive categories (evasion, exploit), which exist in the
platform as **contracts only**: the core ships their interfaces, registration,
dispatch, and data shapes, and supplies no concrete techniques
([architecture.md Sec 13](../architecture.md)).
What a module does on the target is the module author's responsibility and
must stay within the authorization the operator holds.

The design goal is that adding a capability is **registration, never
modification**: no core edits, no composition-root changes, no protocol
changes.

## The two halves of a capability

A capability verb has a server-side half (who may issue it) and an
implant-side half (what runs on the target). They meet at the verb string and
the opaque argument string -- nothing else.

| Half | Lives in | Decides |
|------|----------|---------|
| Gate + catalog | teamserver, `Rod.Tradecraft` | whether an operator may issue the verb; what the UI shows |
| Execution | implant, handler registry | what the verb actually does |

The teamserver never executes tradecraft (architecture.md Sec 10.2): it gates,
forwards, and records. Execution lives where the target's filesystem, network,
and credentials actually are.

## Server-side half: register a module

Implement `ICapabilityModule` -- a registration-only contract carrying exactly
one `CapabilityDescriptor`:

```csharp
using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Modules;

public sealed class MyPingModule : ICapabilityModule
{
    public CapabilityDescriptor Descriptor { get; } = CapabilityDescriptor.Of(
        "demo.ping",
        CapabilityCategory.Core,
        "1.0",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Free-form OPSEC metadata; the UI badges known keys.
            ["touches-network"] = "false",
        });
}
```

Build it, drop `MyTradecraft.dll` next to the teamserver binary, and list the
type under `Tradecraft:Modules` in `appsettings.json`:

```json
{
  "Tradecraft": {
    "Modules": [ "MyTradecraft.MyPingModule, MyTradecraft" ]
  }
}
```

That is the whole server-side integration:

- Registration is **last-wins over the placeholder** -- every built-in verb
  (the contract-only `evasion.*` / `exploit.*` included) is held by a
  placeholder module precisely so your module replaces it by registration.
- A registered module **widens the task gate**: the registry-backed resolver
  admits the verb for task issuance even when no implant class's reduced set
  lists it. This is the path the sensitive categories depend on -- they are
  deliberately not class-gated (architecture.md Sec 5.2, Sec 10.3).
- Failures are loud: a wrong type name, a missing assembly, or a throwing
  constructor aborts startup. A red team cannot afford "registered but not
  what the operator deployed".

The loader resolves assemblies **only** by that explicit list (already loaded,
or a same-named dll in the application directory) -- it never scans
directories. A module reaches the process exactly when an operator built it,
placed it, and named it.

## Implant-side half: the plugin seam ahead

The compile-time handler overlay these pages used to teach -- a directory of
`ICapabilityHandler` sources the .NET build unit compiled into every
artifact -- retired with the .NET implant it compiled. The Rust reference
carries its handler set in the crate (`handlers::dispatch`), and the
implant-side extension seam ahead is the C-ABI plugin module on the todo:
a `rod-plugin-sdk` crate (a normal Rust trait plus the macro that emits the
`extern "C"` shim), delivered over the sealed task channel by a module.load
verb and staged the way the old stager staged a stage-2. Until that lands,
the implant-side answer is the escape hatch below: point the build unit at
your own tree, or build it directly with cargo.

The verb families the seam is *for* are the long tail -- recon sweeps,
lateral movement, persistence, credential and screen collection -- the
stateless run-code-return-bytes work a plugin shape holds naturally. The
channel verbs (`shell.interact`, `tunnel.forward`, `tunnel.socks`) and the
file/exec core stay compiled on purpose: a live channel owns process
handles and carriage multiplexing that spans tasking cycles, which no
post-build module can reach (architecture.md Sec 13's line).

The dispatch grammar your code answers either way is the task contract's
own: string arguments in, outcome plus output back, with exfil chunks for
bulk -- the same shape the compiled handlers speak, so an operator's console
reads a module verb exactly like a built-in one.

## Building an artifact that carries your handler

The build unit compiles the Rust crate through the uniform build contract
(`Build:RustSourceDirectory` names your tree on an installed teamserver;
unset keeps the repo walk-up): a hermetic staging copy, the per-artifact
profile baked into `src/baked.rs`, and a `cargo build --release --target
<triple>` for the requested target. Your tree is independent and disposable
-- coupled to the teamserver only by the wire contracts (rod.proto, the
baked profile's base64url JSON, the sealed envelope), so a fork tracks the
contract, not the teamserver's code.

## OPSEC metadata and ROE

Give the descriptor honest attributes (`writes-to-disk`, `touches-network`,
`modifies-defenses`, ...): they render as risk badges in the operator UI, and
operators writing ROE profiles can gate on namespaces (`evasion.*` wildcards
work in `PermittedVerbs`). An engagement whose ROE profile omits your verb
refuses it at queue time with an audit record naming the violated rule --
build the metadata as if the operator's report depends on it, because it does.

## Build-time transforms

Build-time artifact transformation -- the slot where Metasploit put its
encoders and payload encryption -- follows the same pattern as a capability
module, one layer down ([architecture.md Sec 6](../architecture.md)). The
platform ships only the seam; no transform ships in-tree, because each one
owns its key material and its decode contract end to end -- the teamserver
generates none, stores none, and knows nothing about how the bytes unwrap on
the target.

Implement `IPayloadTransform`: name yourself (the name lands on the artifact
and in the audit trail, so make it find the code), take the built bytes plus
the build context (class, target, transport and beacon profiles), and return
the transformed bytes plus a short metadata note:

```csharp
using Rod.BuildPipeline.PayloadBuild;

public sealed class MyWrapTransform : IPayloadTransform
{
    public string Name => "my-wrap";

    public Task<PayloadTransformOutput> ApplyAsync(
        PayloadTransformInput input, CancellationToken cancellationToken = default)
    {
        var wrapped = MyCodec.Wrap(input.Artifact, key: /* yours, not the server's */);
        return Task.FromResult(new PayloadTransformOutput(wrapped, Metadata: "v1"));
    }
}
```

Drop the assembly next to the teamserver binary and list it under
`Build:Transforms`; the listed order is the application order, each
transform's output feeding the next:

```json
{
  "Build": {
    "Transforms": [ "MyTransforms.MyWrapTransform, MyTransforms" ]
  }
}
```

The loader resolves assemblies only by that explicit list -- the same bounded
shape as `Tradecraft:Modules` -- and a wrong entry fails startup loudly. The
chain runs after the build unit and before anything is recorded, so the
stored fingerprint covers exactly the transformed bytes and the
`PayloadBuilt` audit event names every applied transform (with its metadata
note): the engagement report never lies about what a transform produced.
Remember the implant side is still yours -- nothing in-tree unwraps your
bytes, so your decode stub travels with whatever artifact you ship.

## What the core ships, and where your module sits

The reference set is the standard, documented surface (architecture.md
Sec 13); everything beyond it -- in-the-wild zero-days, weaponized PoCs,
novel tradecraft -- lives in modules like yours, arriving through the module
seams. The core keeps the interfaces, registration, dispatch, and data
models; the tradecraft is yours.
