# AGENTS.md

Conventions for anyone working in this repository. These rules are mandatory;
follow them exactly. This is the canonical instruction file and the **only**
guidance file tracked in git.

---

## 1. Language policy (English only)

- **Everything is English.** Source code, identifiers, comments, XML doc
  comments, log messages, configuration, documentation, PR descriptions, and
  **git commit messages** must be written in English.
- Never hard-code non-English strings. Content stays origin-indistinguishable
  and internationally neutral.

## 2. Writing style

- Write like a person: concise, specific, and direct. No boilerplate filler, no
  over-apologetic comments, no hedging.
- Comments and docs explain *why*, not *what*. If code is self-explanatory,
  leave it uncommented.
- **No attribution markers, anywhere** (commits, files, docs, PRs): never add
  `Co-Authored-By:` trailers, `Generated with ...` / `... generated with ...`
  lines, an AI-tool name or handle, a `Signed-off-by` you were not asked for,
  or any emoji/marker used to flag content as non-human. If a commit comes
  back with such a trailer, redo the commit without it.

## 3. Platform map and tooling

Authoritative design: [docs/architecture.md](docs/architecture.md) --
toolchain and language boundaries in Sec 12.2, capability surface in Sec 13.

- **Teamserver (the control plane): .NET 10 (LTS).** The `Rod.*` projects
  under `src/teamserver/`; `net10.0` TFMs, latest C#, SDK pinned in
  `global.json` for reproducible builds.
- **Redirectors: .NET (Native AOT).** The in-tree single-file forwarder
  lives in `src/redirector/dotnet/` (runbook:
  `docs/operations/redirectors.md`).
- **Implants: Rust.** The reference crate under `src/implant/rust/` builds
  against the wire protocol -- the long-lived, language-neutral contract.
  Community implants in other languages arrive out-of-tree.
- **Build units: one in-tree -- Rust.** Other languages plug in out-of-tree
  through the build contract and the `Language` enum.
- Shared .NET build settings live in `Directory.Build.props` at the repo root
  (`Nullable` enabled, `TreatWarningsAsErrors` on, latest `LangVersion`). Do
  not duplicate these per-project.
- Follow each stack's official guidance and conventions -- Microsoft's for
  .NET, the Rust Book and API guidelines for Rust -- and prefer official,
  first-party libraries and packages when they meet the need; take a
  third-party one only when it adds something specific.
- Prefer the **latest LTS** version for runtimes, libraries, packages, and
  tooling unless an owner picks otherwise.

Anything not named above -- a Go or C++ component, a .NET implant or stager,
a second in-tree language -- is a rejected alternative (Sec 12.2), not a gap
to fill.

## 4. Command-line first

Prefer the CLI for any operation a tool can perform, in every toolchain --
`dotnet` for the .NET stack, `cargo`/`rustup` for the Rust stack. Do not
hand-edit `.csproj`/`.slnx`/`Cargo.toml` for things a command does
(scaffolding, references, dependency versions), and do not copy binaries when
a command exists. Hand-edit only where no tool equivalent exists (a
`Cargo.toml` feature section or profile block, for example, is configuration
rather than a dependency operation, and is hand-written).

| Task | Use this |
|------|----------|
| Add project to solution | `dotnet sln add <path/to/Project.csproj>` |
| Add a project reference | `dotnet add <Project> reference <OtherProject>` |
| Add a NuGet package | register `PackageVersion` in `Directory.Packages.props` (see caveat below) |
| EF Core migration | `dotnet ef migrations add <Name> -p <Infra> -s <Web>` |
| Apply migrations | `dotnet ef database update -p <Infra> -s <Web>` |
| Create a Rust crate | `cargo new <name> --lib\|--bin` (inside the workspace) |
| Add a Rust dependency | `cargo add <crate>` -- version lands in `Cargo.toml`, lock in `Cargo.lock`; both are tracked |
| Add a cross target | `rustup target add <triple>` (never hand-install toolchains) |
| Lint / format (Rust) | `cargo clippy`, `cargo fmt` |
| Format (.NET) | `dotnet format Rod.slnx --verify-no-changes` |

Toolchain caveats (observed SDK behavior, not preference):

- Central package management is on. `dotnet new` templates emit `Version=` on
  their `PackageReference` lines even under CPM (restore fails with `NU1008`),
  and `dotnet add package` refuses with a misleading "cannot define a value
  for Version" error. Strip the attribute after scaffolding, and register
  `PackageVersion` entries directly in `Directory.Packages.props` instead.
- `dotnet format` does not accept `--configuration`; it silently drops to
  printing usage. Formatting is source-level and configuration-independent.
- Under a full parallel `dotnet test` run, the pipeline-build tests that shell
  out to cargo occasionally contend on the shared cargo target-dir lock and
  time out around the two-minute mark; a standalone rerun or a whole-suite
  rerun is green. That is machine scheduling, not a regression -- rerun
  before bisecting.

## 5. Architecture -- monolithic kernel, layered

The teamserver is a single .NET process with six internal layers: core state,
transport, payload build pipeline, operator layer, storage and audit, and
pluggable tradecraft. Dependencies point inward only: tradecraft and operator
layers depend on core state and audit; the build pipeline and transport
depend on core state; core state and audit depend on nothing in-house. Every
project is prefixed with the `Rod.` root namespace. Implants, build units, and
redirectors are independent components coupled to the teamserver only by their
contracts. All domain data is engagement-scoped; cross-engagement access is
impossible by construction.

Authoritative design: [docs/architecture.md](docs/architecture.md).
Architecture tests encode the layer rules; adding a forbidden reference must
fail a test.

## 6. Commits

- Small, focused commits.
- **Subject:** English, imperative mood, self-describing without milestone
  tags -- `Add listener abstraction with HTTP(S) and DNS transports`, not
  `... (M2.2)`. The subject must read well on its own; the rest of the
  message adds context, not identity.
- **Body:** explain the *why* first, then what changed as bullets. Reference
  `docs/architecture.md` for design authority (it is stable). Prefer
  `architecture.md Sec 8` over a historical milestone id -- the commit
  outlives the plan.
- **Historical milestone ids** (`M2.2` etc.) are retired: the roadmap is gone
  and the ids resolve nowhere. Never add new ones to code, comments, or commit
  messages; when touching an old comment that still cites one, drop the id.
- No attribution trailers (see Sec 2).

## 7. Capability discipline

The capability surface is governed by contracts, not by a curated technique
allowlist. [architecture.md Sec 13](docs/architecture.md) is the
authoritative statement.

- **The reference set is the standard, documented surface**: shell execution,
  file transfer in both directions, recon, process enumeration and
  termination, child-implant derivation, access tokens, remote execution,
  persistence, credential collection, screen capture, in-memory payload
  carriage, and C2 exfiltration into engagement-scoped artifact storage.
  Sec 13 itemizes it and draws the implant-core/plugin-domain line.
- **Everything beyond it arrives through the module seams**: server-side
  `CapabilityModule`s, the implant handler overlay, and the payload transform
  chain each define their interface, registration, dispatch, and data model
  there; concrete tradecraft is supplied as separate, opt-in modules the
  operator deploys. The core ships no concrete evasion, exploit, or
  collection tradecraft beyond the reference set.
- All work here assumes an authorized-use context.
