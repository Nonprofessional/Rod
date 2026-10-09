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

## Implant-side half: write a plugin module

The crate fork (below) adds a handler at build time. The plugin seam adds
one to an artifact already deployed (architecture.md Sec 5.4): a C-ABI
module written against the `rod-plugin-sdk` crate
(`src/implant/rust/plugin-sdk/`), delivered over the sealed task channel by
`module.load`'s staged content, dispatched like any compiled verb. The
authoring surface is a plain Rust trait plus one macro:

```rust
use rod_plugin_sdk::{rod_plugin, Plugin, Verb};

pub struct Sweep;

impl Plugin for Sweep {
    fn name(&self) -> &'static str { "sweep" }
    fn verbs(&self) -> Vec<Verb> {
        vec![Verb::new("recon.portscan", portscan)]
    }
}

fn portscan(arguments: &str) -> Result<String, String> {
    // arguments is the opaque task string; Ok is the task's output,
    // Err its failure. Print recon findings in the JSON-lines grammar
    // below and the topology view picks them up.
    Ok(sweep(arguments))
}

rod_plugin!(Sweep);
```

Build it as a `cdylib` for the target's own platform (`cargo build
--release` on the target host or with its cross triple), then issue the
load with the library's bytes as the task's content: `module.load sweep`.
The loader stages the bytes in a memfd and resolves the entry family
(`dlopen` on a dynamic libc host, a manual PE map on Windows), the verbs
join the dispatch table, and the next contact advertises them -- the
operator's console reads a module verb exactly like a built-in one.
`module.list` reports what is loaded, and `module.unload sweep` retracts
it (best-effort: routes drop immediately, a running handler finishes).

The reference module in-tree (`src/implant/rust/modules/hostenum/`) is the
worked example to copy: `recon.hostenum` against the SDK, benign and
readable.

The rules the loader enforces, all reported on the load task itself:

- **Namespaced verbs only.** `namespace.action`, the grammar every Rod
  verb follows.
- **The compiled set and the channel verbs stay compiled.** A module
  cannot register a verb the artifact compiles (the compiled arm would
  shadow it forever) or a channel verb (`shell.interact`,
  `tunnel.forward`, `tunnel.socks`) -- a live channel owns process
  handles and carriage multiplexing that span tasking cycles, which no
  post-build module can reach (architecture.md Sec 13's line).
- **Replacement is last-registration-wins.** A later module's verb
  replaces an earlier module's route, the same rule the server-side seam
  applies.
- **Panics are fenced.** The shim catches a panicking handler and reports
  it as the task's failure; build without `panic = "abort"`, which turns
  the fence off.

**The platform boundary.** The seam rides the host platform's own
in-process loading: it works on dynamic-libc builds (the glibc dev shape)
and on Windows, while the static musl artifact -- every fielded Linux
build -- carries no `dlopen` and the Rust toolchain produces no musl
cdylib, so its `module.load` fails cleanly naming that boundary. For the
musl artifact the crate fork below remains the extension path.

**Pairing with the server half.** A module verb in a standard namespace
(recon, lateral, persist, collect, exfil) is class-admissible for the
Implant class already, so it tasks with no server-side ceremony. A module
carrying a novel namespace pairs with a server-side descriptor module
(the `ICapabilityModule` half above) that widens the issuance gate -- two
halves, one verb string, nothing else shared.

The dispatch grammar your code answers either way is the task contract's
own: string arguments in, outcome plus output back, with exfil chunks for
bulk -- the same shape the compiled handlers speak, so an operator's console
reads a module verb exactly like a built-in one.

## Recon output grammar (the topology contract)

Recon findings are the one task output the server reads back structurally:
the engagement's topology view ([architecture.md Sec
11.2](../architecture.md)) parses the completed output of `recon.portscan`
and `recon.hostenum` tasks into the picture's observed hosts and ports.
The grammar is JSON lines -- one finding per line, `host` required,
everything else optional, unknown fields ignored:

```json
{"host":"10.0.0.5","port":445,"state":"open","service":"smb"}
{"host":"web01","addresses":["10.0.0.5"],"os":"linux","arch":"x86_64"}
```

A line carrying a `port` is a portscan finding; one carrying `addresses`
or `os`/`arch` is a hostenum finding. A line that does not parse is not a
finding: it stays in the transcript, and nothing errors -- print your
sweep in this shape and the operator's picture assembles itself, print
anything else and the transcript remains the read, which is the honest
fallback. The parse is bounded (ten thousand lines per task), so a sweep
against a /24 may print freely.

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

Four of those attributes also decide the verb's **unattended posture**
([architecture.md Sec 10.4](../architecture.md)): declare `reads-input`,
`reads-memory`, `executes-code`, or `modifies-defenses`, or register under
the Evasion/Exploit categories, and the verb never fires without a human --
the automation engine refuses to build a rule on it. The stance is deliberate: the
metadata you declare is the whole contract, so a module nobody anticipated
gets the right posture at registration and no list anywhere needs an edit.
Attributes the policy does not key on (`reads-credential`, `persists`,
`reads-screen`, ...) leave the verb automatable -- describe what the verb
does, and the platform decides; do not game the vocabulary.

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
