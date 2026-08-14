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
- **Redirectors: .NET (Native AOT).** A single-file native forwarder when one
  ships in-tree; no Go. See architecture.md Sec 12.2.
- **Build units: the in-tree unit is .NET.** Additional languages (Go, C/C++,
  Nim) stay available through the language-neutral build contract and the
  `Language` enum, supplied as out-of-tree community units -- not maintained
  in-tree.
- **Implants: one .NET reference implant, polyglot by contract.** The in-tree
  reference implant is .NET; the wire protocol is the product, so a community
  implant in any language builds against the same contract without coupling the
  teamserver to its language. See architecture.md Sec 12.2.
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
  `docs/architecture.md` for design authority (it is stable). Prefer
  `architecture.md Sec 8` over a historical milestone id -- the commit
  outlives the plan.
- **Historical milestone ids** (`M2.2` etc.) are retired: the roadmap is gone
  and the ids resolve nowhere. Never add new ones to code, comments, or commit
  messages; when touching an old comment that still cites one, drop the id.
- No attribution trailers (see Sec. 2 for the full ban).

## 7. Sensitive-capability discipline

The boundary between in-repo and out-of-tree tradecraft is decided by
**what kind of technique it is**, not by capability category. See
[architecture.md Sec 13](docs/architecture.md) for the
authoritative rule; this section summarizes it.

- **In-repo: standard, mainstream, documented techniques.** Mechanisms that are
  documented in OS vendor references (Win32 API, MSDN, systemd, cron, OpenSSH,
  etc.), widely covered in offensive-security curricula and tooling, and have a
  legitimate system-administration or defensive-research side. The reference
  implants implement these directly so the framework is useful for learning,
  research, and authorized red-team work out of the box. Current in-repo surface:
  shell execution, host/port recon, child-implant derivation, Windows access
  tokens, remote execution, persistence (Run key / scheduled tasks / services /
  cron / systemd), file and standard-store credential collection, and C2
  exfiltration into engagement-scoped artifact storage.
- **Out-of-tree: sensitive tradecraft only.** In-the-wild zero-days, weaponized
  proof-of-concepts, novel or unpublished detection-evasion and bypass
  techniques, LSASS memory dumping for credential theft, and input capture
  (keyloggers) stay out of the core. These are **pluggable capability
  contracts**: define their interface, registration, dispatch, and data model
  here; the concrete tradecraft lives in separate, opt-in, out-of-tree modules
  the operator supplies.
- All work here assumes an authorized-use context; see RESPONSIBLE-USE.md. When
  in doubt about which side a technique falls on, default to out-of-tree and
  raise the question.

## 8. Where things live

- **Teamserver**: the `Rod.*` .NET projects under `src/teamserver/`, monolithic
  kernel, six internal layers, clean dependency rules.
- **Build units**: the in-tree .NET build unit; community units in other
  languages plug in through the build contract.
- **Redirectors**: the in-tree .NET Native AOT forwarder
  (`src/redirector/dotnet/`) and its runbook
  (`docs/operations/redirectors.md`).
- **Implants**: the .NET reference implant under `src/implant/dotnet/`,
  independent and disposable; community implants in other languages arrive
  out-of-tree.
- **Wire protocol and capability registry**: the long-lived, language-neutral
  contract implants build against.
- All domain data is engagement-scoped; cross-engagement access is impossible by
  construction.
