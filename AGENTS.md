# AGENTS.md

Conventions for anyone working in this repository. These rules are mandatory;
follow them exactly. This is the canonical instruction file and the **only**
guidance file tracked in git.

---

## 1. Language policy (English only)

- **Everything is English.** Source code, identifiers, comments, XML doc
  comments, log messages, configuration, documentation, PR descriptions, and
  **git commit messages** must be written in English.
- Never hard-code non-English strings. Content stays origin-indistinguishable and
  internationally neutral.

## 2. Writing style

- Write like a person: concise, specific, and direct. No boilerplate filler, no
  over-apologetic comments, no hedging.
- Comments and docs explain *why*, not *what*. If code is self-explanatory, leave
  it uncommented.
- Commit messages, file headers, file contents, and trailers must carry only
  what a contributor would write -- nothing else. This is absolute and applies
  everywhere (commits, files, docs, PRs): never add `Co-Authored-By:` trailers,
  `Generated with ...` / `... generated with ...` lines, an AI-tool name or
  handle, a `Signed-off-by` you were not asked for, or any emoji/marker used to
  flag content as non-human. If a commit comes back with such a trailer, redo the
  commit without it.


## 3. Platform and tooling

- **Teamserver (the control plane) target: .NET 10 (LTS).** Use `net10.0`
  TFMs and the latest C# language features. The SDK is pinned in
  `global.json` for reproducible builds. .NET exists for the control plane
  only; there is no .NET implant and no .NET stager.
- **Redirectors: .NET (Native AOT).** A single-file native forwarder when one
  ships in-tree; no Go. See architecture.md Sec 12.2.
- **Build units: one in-tree -- Rust.** Additional languages (Go, C/C++,
  Nim) stay available through the language-neutral build contract and the
  `Language` enum, supplied as out-of-tree community units.
- **Implants: the Rust reference is the implant.** The wire protocol is
  the product, so every implant builds against the same contract without
  coupling the teamserver to its language. The Rust crate is the reference:
  static musl on Linux, mingw on Windows, both web carriages, the
  streaming channel set (interactive shell and the tunnel pair), the
  conformance harness's reference candidate. See architecture.md Sec 12.2.
- Shared .NET build settings live in `Directory.Build.props` at the repo root
  (`Nullable` enabled, `TreatWarningsAsErrors` on, latest `LangVersion`). Do not
  duplicate these per-project.
- Prefer the **latest LTS** version for runtimes, libraries, packages, and
  tooling unless an owner picks otherwise.

## 4. Command-line first

Prefer the CLI for any operation a tool can perform, for every toolchain in
the tree -- `dotnet` for the .NET stack, `cargo`/`rustup` for the Rust stack,
each through its own official command line. Do not hand-edit
`.csproj`/`.slnx`/`Cargo.toml` for things a command does (scaffolding,
references, dependency versions), and do not copy binaries when a command
exists.

| Task | Use this |
|------|----------|
| Create solution | `dotnet new sln -n <Name>` |
| Create project | `dotnet new classlib\|webapi\|xunit -n <Name> -o <path>` |
| Add project to solution | `dotnet sln add <path/to/Project.csproj>` |
| Add a project reference | `dotnet add <Project> reference <OtherProject>` |
| Add a NuGet package | `dotnet add <Project> package <PackageId>` |
| Remove a package | `dotnet remove <Project> package <PackageId>` |
| EF Core migration | `dotnet ef migrations add <Name> -p <Infra> -s <Web>` |
| Apply migrations | `dotnet ef database update -p <Infra> -s <Web>` |
| Build / test / run (.NET) | `dotnet build`, `dotnet test`, `dotnet run` |
| Format (.NET) | `dotnet format` |
| Create a Rust crate | `cargo new <name> --lib\|--bin` (inside the workspace) |
| Add a Rust dependency | `cargo add <crate>` -- versions land in `Cargo.toml`, the lock in `Cargo.lock`; both are tracked |
| Build / test (Rust) | `cargo build`, `cargo test` (add `--target <triple>` for a cross) |
| Add a cross target | `rustup target add <triple>` (never hand-install toolchains) |
| Lint / format (Rust) | `cargo clippy`, `cargo fmt` |

Hand-edit only where no tool equivalent exists (a `Cargo.toml` feature
section or profile block, for example, is configuration rather than a
dependency operation, and is hand-written).

## 5. Architecture -- monolithic kernel, layered

The teamserver is a single .NET process with six internal layers: core state,
transport, payload build pipeline, operator layer, storage and audit, and
pluggable tradecraft. Dependencies point inward only: tradecraft and operator
layers depend on core state and audit; the build pipeline and transport depend on
core state; core state and audit depend on nothing in-house. Every project is
prefixed with the `Rod.` root namespace. Implants, build units, and redirectors
are independent components coupled to the teamserver only by their contracts.

Authoritative design: [docs/architecture.md](docs/architecture.md). Architecture
tests encode the layer rules; adding a forbidden reference must fail a test.

## 6. Commits

- Small, focused commits.
- **Subject:** English, imperative mood, self-describing without milestone
  tags -- `Add listener abstraction with HTTP(S) and DNS transports`, not
  `... (M2.2)`. The subject must read well on its own; the rest of the message
  adds context, not identity.
- **Body:** explain the *why* first, then what changed as bullets. Reference
  `docs/architecture.md` for design authority (it is stable). Prefer
  `architecture.md Sec 8` over a historical milestone id -- the commit
  outlives the plan.
- **Historical milestone ids** (`M2.2` etc.) are retired: the roadmap is gone
  and the ids resolve nowhere. Never add new ones to code, comments, or commit
  messages; when touching an old comment that still cites one, drop the id.
- No attribution trailers (see Sec. 2 for the full ban).

## 7. Capability discipline

The capability surface is governed by contracts, not by a curated technique
allowlist. See [architecture.md Sec 13](docs/architecture.md) for the
authoritative statement.

- **The reference set is the standard, documented surface**: shell execution,
  file transfer in both directions (`file.push`/`file.pull`), host/port recon,
  process enumeration and termination (`recon.ps`/`proc.kill`),
  child-implant derivation, Windows access tokens, remote execution,
  persistence (Run key / scheduled tasks / services / cron / systemd),
  standard-store credential collection, screen capture over the standard
  desktop-capture APIs (`collect.screenshot`), in-memory payload carriage
  (assembly loading, memfd execution), and C2 exfiltration into
  engagement-scoped artifact storage.
- **Everything beyond it arrives through the module seams**: server-side
  `CapabilityModule`s, the implant handler overlay, and the payload transform
  chain each define their interface, registration, dispatch, and data model
  here; the concrete tradecraft is supplied as separate, opt-in modules the
  operator deploys. The core ships no concrete evasion, exploit, or
  collection tradecraft beyond the reference set.
- All work here assumes an authorized-use context.

## 8. Where things live

- **Teamserver**: the `Rod.*` .NET projects under `src/teamserver/`, monolithic
  kernel, six internal layers, clean dependency rules.
- **Build units**: the in-tree Rust build unit; community units in other
  languages plug in through the build contract.
- **Redirectors**: the in-tree .NET Native AOT forwarder
  (`src/redirector/dotnet/`) and its runbook
  (`docs/operations/redirectors.md`).
- **Implants**: the Rust reference under `src/implant/rust/`, independent
  and disposable; community implants in other languages arrive out-of-tree.
- **Wire protocol and capability registry**: the long-lived, language-neutral
  contract implants build against.
- All domain data is engagement-scoped; cross-engagement access is impossible by
  construction.
