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

- **Teamserver target: .NET 10 (LTS).** Use `net10.0` TFMs and the latest C#
  language features. The SDK is pinned in `global.json` for reproducible builds.
- **Redirectors: Go** (latest stable), single static binary.
- **Build units: one per implant language** (C#/.NET, Go, C/C++, Nim), each with
  its own toolchain. Coupled to the teamserver only by the build contract.
- **Implants: polyglot** -- the language fits the target (see
  docs/architecture.md). Never couple the teamserver to a single implant language.
- Shared .NET build settings live in `Directory.Build.props` at the repo root
  (`Nullable` enabled, `TreatWarningsAsErrors` on, latest `LangVersion`). Do not
  duplicate these per-project.
- Prefer the **latest LTS** version for runtimes, libraries, packages, and
  tooling unless an owner picks otherwise.

## 4. Command-line first

Prefer the CLI for any operation a tool can perform. Do not hand-edit
`.csproj`/`.slnx`, hand-create scaffolding, or copy binaries when a command
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
| Build / test / run | `dotnet build`, `dotnet test`, `dotnet run` |
| Format | `dotnet format` |
| Go build/test | `go build ./...`, `go test ./...` |

Hand-edit only where no tool equivalent exists.

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
  tags -- `Add listener abstraction with HTTP(S) and mTLS transports`, not
  `... (M2.2)`. The subject must read well on its own; the rest of the message
  adds context, not identity.
- **Body:** explain the *why* first, then what changed as bullets. Reference
  `docs/architecture.md` for design authority (it is stable), not the roadmap
  (it is a plan and gets reworked). Prefer `architecture.md Sec 8` over a
  roadmap id -- the commit outlives the roadmap.
- **Roadmap milestone ids** (`M2.2` etc.) are development-time breadcrumbs only.
  Never put them in the subject. If mentioned, put them once in the body as a
  trailing `Roadmap: Mx.x` line, and never as the sole reference -- the commit
  must still make sense after the roadmap is reworked or removed.
- No attribution trailers (see Sec. 2 for the full ban).

## 7. Sensitive-capability discipline

- Evasion (AV/EDR avoidance), exploit (PoC integration), and related offensive
  modules are **pluggable capability contracts**: define their interface,
  registration, dispatch, and data model; do **not** commit concrete bypass
  techniques, weaponized code, or in-the-wild PoCs to this repository.
- When implementing such a module, build the contract, the registration, and the
  dispatch path. Concrete tradecraft lives in separate, opt-in, out-of-tree
  modules the operator supplies.
- All work here assumes an authorized-use context; see RESPONSIBLE-USE.md.

## 8. Where things live

- **Teamserver**: the `Rod.*` .NET projects, monolithic kernel, six internal
  layers, clean dependency rules.
- **Build units**: per-language implant compilers, driven by the build contract.
- **Redirectors**: Go forwarders under the infrastructure tree.
- **Implants**: per-language build units, each independent and disposable.
- **Wire protocol and capability registry**: the long-lived, language-neutral
  contract implants build against.
- All domain data is engagement-scoped; cross-engagement access is impossible by
  construction.
