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

## Implant-side half: register a handler

Implement `ICapabilityHandler` (or use the `CapabilityHandler` delegate
wrapper) and register it in `HandlerRegistry.Default`'s `additional` seam --
registration is compile-time by design (no runtime assembly loading for
handler plugins: it would break Native AOT, enlarge the artifact, and put
plugin files on disk; the in-memory loading the tree does have is the
loader's stage-2 carriage, not a plugin mechanism -- architecture.md
Sec 5.3):

```csharp
var registry = HandlerRegistry.Default(
    enroll: enrollBundle,
    additional: new[]
    {
        new CapabilityHandler(
            "demo.ping",
            args => (TaskOutcome.Succeeded, $"pong at {DateTimeOffset.UtcNow:O}")),
    });
```

The handler owns its argument grammar -- the argument string arrives opaque
and unparsed by anyone upstream (architecture.md Sec 10). Return a result and,
for bulk data, `ExfilChunk` frames; the beacon loop writes them to the
engagement artifact store on your behalf.

The handshake advertisement is the baked verb set -- the class set plus the
contract-only verbs no class gates -- intersected with the compiled handlers,
so the implant never advertises a verb it cannot run. Two consequences for
extension authors:

- A verb inside the baked set (adding a new `recon.*` handler, say) is
  advertised automatically.
- A contract-only verb (`evasion.*`, `exploit.*`) rides along in every bake,
  so a handler you compile in for one advertises at handshake -- and an
  artifact without the handler still claims nothing.

## Building an artifact that carries your handler

Point the build unit at an extension directory: a folder of handler sources
you maintain outside the repository, named as a path under
`Build:ImplantExtensionDirectory` in `appsettings.json`:

```json
{
  "Build": {
    "ImplantExtensionDirectory": "/opt/rod/extensions"
  }
}
```

Every implant-class build then overlays the directory onto the per-build
staging tree: the `.cs` files compile in, and the build unit generates the
`ExtensionRegistrations` file that feeds `HandlerRegistry.Default`'s
`additional` seam -- dropping a handler source into the directory and building
yields an artifact that runs it, for every class whose verb set admits the
handler's verb (the rule the section below spells out). No fork of the
implant tree to maintain. The
build unit still bakes the per-artifact profile (mode, endpoint,
sleep/jitter/kill date, verb set) into whatever tree it compiles, and
publishes the requested artifact format -- the self-contained single-file
executable default, its trimmed twin, the native AOT binary, or the
in-memory-loadable dll bundle (architecture.md Sec 6).

A handler source follows one authoring shape: a top-level concrete class with
a parameterless constructor whose base list names `ICapabilityHandler`. Any
namespace works -- the generated registrations qualify each class fully:

```csharp
using Rod.Implant.Internal;
using Rod.V1;

namespace MyTradecraft.Evasion;

internal sealed class MyAvoidHandler : ICapabilityHandler
{
    public string Verb => "evasion.avoid";

    public HandlerResult Handle(string arguments)
        => (TaskOutcome.Succeeded, "ack");
}
```

Helper classes, extra files, and subdirectories are fine -- only discovered
handlers register. Sources under `bin/`/`obj/` are skipped, so an extension
built standalone does not compile its output in twice.

Failures are loud on both ends, the same rule as `Tradecraft:Modules`:

- A configured directory that is missing, or that contains no handler class,
  aborts the build -- and a missing directory aborts teamserver startup. An
  operator must never get an artifact that silently lacks the handlers they
  believe it carries.
- A discovered class the compiler cannot instantiate (abstract, nested, or
  without a parameterless constructor) fails the publish with the type named
  in the diagnostic.

The verb each handler serves decides which builds compile it. The bake trims
an artifact to the verbs its class runs (architecture.md Sec 5.2/5.3), and
the overlay reads each handler's expression-bodied `Verb => "..."` declaration
to place it: a verb the class table gates (`collect.keylog`, say) compiles
only into builds whose class carries it, while the ungated contract verbs
(`evasion.*`, `exploit.*`) and any verb no class lists -- your own -- ride
every build. Two practical consequences for authoring:

- One handler class per file keeps the trim clean: a source file whose
  handlers all drop stays behind whole, so a file mixing a kept and a
  withheld handler keeps them both.
- Keep the `Verb` declaration expression-bodied. A shape the scan cannot
  read (a block-bodied property) compiles into every build rather than being
  silently dropped.

Current limits, deliberate: the overlay feeds the one-shot `additional` seam
only -- a staged or channel verb still needs the fork -- and stager-class
builds are never overlaid (a stage-1 loader carries no tradecraft handlers).
The fork itself remains available: `src/implant/dotnet` is an independent,
disposable component coupled to the teamserver only by the proto, and pointing
the build unit at your own tree (or building it directly) is the escape hatch
for anything the overlay does not cover.

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
