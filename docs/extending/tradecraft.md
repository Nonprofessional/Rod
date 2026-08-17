# Rod -- Writing out-of-tree tradecraft

How a third party adds capabilities to Rod without touching the core tree --
including the sensitive categories (evasion, exploit), which exist in the
platform as **contracts only**: the core ships their interfaces, registration,
dispatch, and data shapes, and supplies no concrete techniques
([architecture.md Sec 13](../architecture.md), [RESPONSIBLE-USE.md](../../RESPONSIBLE-USE.md)).
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
registration is compile-time by design (no runtime assembly loading: it would
break Native AOT, enlarge the artifact, and put plugin files on disk;
architecture.md Sec 5.3):

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

The handshake advertisement is the baked class verb set intersected with the
compiled handlers, so the implant never advertises a verb it cannot run. Two
consequences for extension authors:

- A verb inside the baked class set (adding a new `recon.*` handler, say)
  is advertised automatically.
- A contract-only verb (`evasion.*`, `exploit.*`) is not in any class set, so
  it does not appear in the handshake advertisement today. Dispatch does not
  depend on the advertisement -- a registered module's verb is issuable and
  delivered regardless -- but the roster shows the narrower set. Closing that
  cosmetic gap is on the todo list (bake the class set plus the registered
  contract-only verbs).

## Building an artifact that carries your handler

Today: maintain the handler registrations in an overlay or fork of
`src/implant/dotnet` (adding the registrations alongside the reference set in
`HandlerRegistry.Default`) and point `DotNetBuildUnit` at your tree, or build
the fork directly -- it is an independent, disposable component coupled to the
teamserver only by the proto. The build unit bakes the per-artifact profile
(mode, endpoint, sleep/jitter/kill date, class verbs) into whatever tree it
compiles, and publishes a self-contained single-file executable for the
requested OS/arch with no target-side runtime.

Planned (todo): a configured extension directory the build unit overlays onto
the staging tree, so a handler drops in as a source file and every build
carries it -- no fork to maintain.

## OPSEC metadata and ROE

Give the descriptor honest attributes (`writes-to-disk`, `touches-network`,
`modifies-defenses`, ...): they render as risk badges in the operator UI, and
operators writing ROE profiles can gate on namespaces (`evasion.*` wildcards
work in `PermittedVerbs`). An engagement whose ROE profile omits your verb
refuses it at queue time with an audit record naming the violated rule --
build the metadata as if the operator's report depends on it, because it does.

## What stays out of the core, and why

The boundary is technique-kind, not category (architecture.md Sec 13):
standard, documented, mainstream techniques ship in the reference implant;
in-the-wild zero-days, weaponized PoCs, and novel detection-evasion live in
modules like yours. When unsure which side a technique falls on, keep it
out-of-tree -- tightening later is cheap; loosening under pressure is how the
line erodes. All use assumes an authorized context
([RESPONSIBLE-USE.md](../../RESPONSIBLE-USE.md)).
