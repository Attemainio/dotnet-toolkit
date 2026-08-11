# Architecture

> **Maintainer-facing.** No agent or skill is told to read this; `agents/dotnet-explore.md` is
> explicitly forbidden from opening it. It documents how the server is built, not how to use it.

How `DotnetToolkit.McpServer` is put together, and the packaging that turns it into a Claude Code
plugin. **Read this when a change touches server internals** — startup order, the two knowledge
tiers, a subsystem you haven't worked in, or how the plugin is delivered. Ordinary tool *usage*
needs none of it: `.claude/rules/dotnet-index.md` routes to the skill that owns it
(`dotnet-read`/`dotnet-write`), and `docs/tools/<tool>.md` has the per-tool manual.

This file is human- and maintainer-facing and is read on demand, so it is deliberately fuller than
`CLAUDE.md`, which carries only the always-applicable rules and points here.

## Startup

The server (`src/DotnetToolkit.McpServer/Program.cs`) starts over stdio and registers tools via
`WithToolsFromAssembly`.

**stdout is reserved for MCP JSON-RPC** — all logging goes to stderr; never write to `Console.Out` in
server code. A stray `Console.WriteLine` corrupts the protocol stream and the session dies with a
parse error that points nowhere near the cause.

`MSBuildLocator.RegisterDefaults()` must run **before** any
`Microsoft.CodeAnalysis.Workspaces.MSBuild` code touches assemblies. This happens at the very top of
`Program.cs`, before the host builder is constructed. Moving it later breaks workspace loading in a way
that looks like a missing-dependency error.

## The two knowledge tiers

Both are built in the background so the MCP handshake completes within Claude Code's ~5s startup
timeout. Tool calls await readiness themselves rather than blocking startup.

- **Syntax index** (`Indexing/ProjectIndex.cs`, `StartInitialization()`) — every `.cs` file parsed with
  Roslyn, no MSBuild needed. Lets `search_index` and `get_symbol` answer almost immediately, marked
  `limitedBy: "index_only"`. It **prunes `bin`/`obj`/`dist`/`.git`/`node_modules` before it scans**, so
  it has no location for a symbol only a source generator declares — while the semantic tier, which
  supplies the symbol rows, does. That asymmetry is why a symbol row carries a `generated` flag
  (`Schema.cs` migration 15, written by `SymbolIndexBuilder` from the declaration's own path): without
  it, `search_index` renders such a hit with an empty file and lines and no way to tell that apart from
  an indexing failure. Like every additive column here, the migration clears the derived symbol index so
  the next build repopulates it — the first start after upgrading re-indexes.
- **MSBuild workspace** (`Workspace/WorkspaceHost.cs`, `StartLoading()`) — the full semantic model.
  Powers `get_references` and `validate_patch`, and the live path of `get_symbol`.

Change detection across both tiers is **mtime-polling**, not filesystem watchers — deliberate, so it
works on WSL `/mnt/*` drives where inotify doesn't fire.

Caches for a target repo live in `.claude/dotnet-toolkit/cache/` under that repo (self-gitignored) and
are always rebuildable from source.

**Bump `IndexDocument.CurrentVersion` whenever `OutlineBuilder` starts emitting something new** — for a
change to what it *produces*, not only to the record shapes it produces into. `cache/index.json` keys
its entries on each file's mtime and length, so an indexer that emits a new field for an unchanged file
keeps serving the old entry indefinitely: the new behavior passes every unit test (which build outlines
directly) and does nothing at all through the server. A missing bump on an added count is worse than a
missing field, since the stale entry deserializes it as `0` — a plausible value, not an obvious gap.

**A changed *value* for an existing field needs the bump just as much as a new field, and hides better.**
Version 8 is exactly that case: `DocLines` became transitive (a type's `D` now counts its members' `///`
lines too), so nothing about a cached entry looked wrong — every field was present and plausible. The fix
shipped, the tests passed, and the server went on serving version 7's numbers until each file's mtime
happened to move. If you find yourself running `touch` over the tree to make a change take effect, the
bump is what you actually needed.

## Subsystems

- `Workspace/SolutionLocator.cs` — auto-discovers the target solution (`*.slnx` > `*.sln` > `*.csproj`,
  root + one level deep) under `CLAUDE_PROJECT_DIR` (the *target* repo — not this repo, when installed
  as a plugin). `SlnxParser.cs` handles `.slnx`.
- `Workspace/PathComparison.cs` — the one definition of what "the same file" means: ordinal on Linux,
  case-insensitive elsewhere. Every path equality test, prefix test, and path-keyed dictionary goes
  through it, because a site that quietly keeps `StringComparer.Ordinal` only misbehaves on the
  platforms this repo's tests don't run on.
- `Workspace/MSBuildRegistration.cs` — picks which installed SDK's MSBuild loads projects (newest wins;
  `DOTNET_TOOLKIT_DOTNET_ROOT` pins one) and registers it before any Roslyn MSBuild code runs. See
  "Environment" below for why the wrong choice degrades silently.
- `Workspace/ToolkitConfig.cs` — optional per-repo `.claude/dotnet-toolkit/config.json`: solution
  override, `devlogDir` (legacy, devlog import only), `excludeGlobs`, and `defaultFormat` (`toon`
  default / `compact` / `json`) via `Output/Formats.Render`.
- `Devlog/` — **legacy, retained only for migration.** The markdown devlog is no longer written or
  queried; `DevlogMigration.cs` imports existing entries into the SQLite `feature_log` once at startup.
- `Store/` — the SQLite knowledge store (`KnowledgeStore.cs`, WAL + migrations in `Schema.cs`): symbol
  index and reference edges (`SymbolStore.cs`), append-only development log (`FeatureLogStore.cs`),
  immutable raw telemetry.
- `Fingerprint/` + `Contracts/` — `SyntaxFingerprint.cs` computes the `decl`/`body` version layers from
  token text (trivia-blind, so comments and formatting move nothing); `ContentVersion.cs` implements the
  layered version token every content response carries.
- `Identity/` — ULIDs and the content-derived `symbolId`; `Workspace/SymbolKey.cs` derives ids from
  Roslyn symbols. See "Id namespaces" below.
- `Indexing/TypeReferenceScan.cs` — the one definition of "which members reference this type", shared by
  `get_references` on a named-type root and `get_call_hierarchy`'s type-root seeding. A named type has no
  call sites of its own, so both tools answer that question instead; keeping one implementation is what
  stops them disagreeing about the same type's blast radius. Also owns `IsCrefLocation`, which separates
  a `<see cref="…"/>` doc mention from a real code site.
- `PluginLocation.cs` — resolves the plugin's own installation directory at runtime, published as
  `workspace_status`'s `pluginRoot`. This is the only route from an always-loaded rule or a subagent to
  the files the plugin ships (`docs/tools/<name>`, `standards/<name>`), because `${CLAUDE_PLUGIN_ROOT}`
  is not expanded inside a rule or an agent definition. Dropping it strands every standards read.
- `Validation/` — the write path: `PatchSandbox.cs` (forked in-memory solution, optionally seeded from a
  draft's proposed text), `ChangeClassifier.cs` (declaration delta → change kinds),
  `EscalationTable.cs` (§13.2 rule table), `ValidationLadder.cs` (levels 1–4, then the analyzer pass),
  `AnalyzerRunner.cs` (runs each project's referenced `DiagnosticAnalyzer`s over the changed documents —
  `Compilation.GetDiagnostics()` runs none of them, so without this every `CA*`/`IDE*` rule an
  `.editorconfig` configures was invisible to validation), `CheckReport.cs` (the `checks` block: which
  rungs ran over what, analyzer findings by severity, and an explicit not-assessed list, so a clean run
  is distinguishable from an unexamined one),
  `DiagnosticDistiller.cs` (root causes, suggested inspections, and the `locations` where each error
  landed in the proposed text's coordinates), `PatchDraftStore.cs` (bounded, 15-minute in-memory store
  of validated-but-unapplied patches — deliberately *not* in SQLite, since a draft describes a fork of
  the currently loaded workspace and is meaningless once that is gone).
- `Output/` — how a response is rendered, never what it contains: `Formats.cs` (the `toon`/`compact`/
  `json` switch and the raw-block splicing TOON needs for source text), `CompactFormatter.cs`,
  `OutlineRenderer.cs`, `SymbolGrouping.cs` (search_index's namespace/file nesting and its collapse
  rules), `ShapeFacts.cs` (the counted facts one symbol's column is built from), `SymbolShape.cs`
  (the `P…M…N…L…O…D…C…A…` column on a search hit or a `get_symbol` member row, plus the legend text
  stated once per envelope) and `ReadAdvice.cs` (the `read` column — the same facts turned into the
  include to pass next, plus the `intent` override; deliberately redundant with `SymbolShape`, since a
  derivation a reader does not perform is not information). The renderer is deliberately kind-blind
  and ungated: every non-zero count
  it is handed is emitted, and **which counts exist is decided where they are gathered** —
  `ProjectIndex.DocSite` for a search hit, `ContextTools.MemberSiteOf` for a member row. A count left
  null is one that kind of declaration cannot have, which is what keeps `M` off a method and `P` off a
  field without a kind-to-letters table here that could drift out of step with either gatherer.
- `Telemetry/` — per-call raw events and the read-side aggregations behind `get_retrieval_metrics`.
- `Git/` — `GitAnalyzer.cs` (git commands, run in a repository it discovers: the solution root when that
  is inside a work tree, otherwise the repos checked out beneath it) + `SemanticDiff.cs`, behind
  `get_semantic_diff`.
- `Control/ControlServer.cs` — a loopback TCP listener (127.0.0.1, OS-assigned port published at
  `CacheDir/control.port`) letting a hook trigger an index rescan (`rescan`, synchronous) or a
  background workspace reload (`reload`, fire-and-forget) without MCP stdio access; consumed by the
  `hook hint-reload-new-cs-file` subcommand through `Hooks/ControlClient.cs`. **Not a security
  boundary** — loopback-only, same trust level as the MCP session.
- `Hooks/` — the five Claude Code hooks, as a `hook <name>` subcommand of this same binary rather than
  as shell scripts. `HookCli.cs` dispatches and owns the fail-open boundary; `CsFileMembership.cs` and
  `BashCommandScanner.cs` carry the logic the read guards share; `WriteChecklistHint.cs` is the one
  matching an MCP tool rather than a built-in. `docs/design/hooks.md`.
- `Tools/` — the MCP surface:

  | File | Tools |
  |---|---|
  | `ContextTools.cs` | `get_symbol`, `get_references`, `search_index` |
  | `FlowTools.cs` | `get_scope`, `get_call_slice`, `get_call_hierarchy`, `get_type_hierarchy` |
  | `GraphTools.cs` | `get_project_graph`, `detect_circular_dependencies` |
  | `HistoryTools.cs` | `get_semantic_diff`, `search_log` |
  | `PatchTools.cs` | `validate_patch` |
  | `RenameTools.cs` | `rename_symbol` |
  | `MetricsTools.cs` | `get_retrieval_metrics` |
  | `ServerTools.cs` | `ping`, `set_output_format`, `workspace_status`, `reload_workspace` |

  `ResponseGuard.cs` is **not** a tool group either: it is `get_symbol`'s one-shot large-source check
  plus the process-wide table of which (symbol, include) requests have already been warned about, so an
  identical repeat is served in full. It lives here rather than in `Output/` because it decides *what*
  a response contains, not how it renders.

  `ToolTelemetry.cs` is **not** a tool group: it is the single place a response becomes a
  `RetrievalEvent`, plus the shared `[Description]` text for the optional `taskId`. The five tools
  taking no `TelemetryRecorder` (`ServerTools`' four and `get_retrieval_metrics`) record nothing by
  design.

## Id namespaces

Four disjoint namespaces, deliberately never sharing a hash space:

| Prefix | Meaning |
|---|---|
| `sym_` | live, doc-comment-derived |
| `symfb_` | `Ids.FallbackSymbolId`, when `GetDocumentationCommentId()` returns null |
| `symidx_` | `Ids.IndexOnlySymbolId`, `get_symbol`'s syntax-only fallback |
| `draft_` | `Ids.Draft()`, a validated-but-unapplied patch |

`validate_patch` rejects any non-`sym_` id in `baseVersions` with `stale_index_only_id` rather than
letting it become a confusing `stale_base` cascade.

## Changing the tool surface

Two consequences that are easy to miss:

- **Tool signature changes are breaking for in-process callers** — the tests call these methods
  positionally.
- **Any change to response shape needs `Contracts/Contract.cs` bumped.**
- **A `[Description]` has a hard ceiling.** The client truncates a method-level description at roughly
  2 KB, silently — `search_index`'s manual pointer was cut off in production for an unknown length of
  time before anyone looked. `tests/DotnetToolkit.McpServer.Tests/ToolDescriptionBudgetTests.cs`
  enforces a 1,900-byte budget over every discovered tool, so the ceiling now fails a build instead of
  a session. Growing a description past it is a signal to move detail into `docs/tools/<tool>.md`,
  which the description then points at — not to raise the constant.

Then run the `dotnet-consistency` skill, which owns the authoritative list of files describing
the tool surface and checks each against `Tools/*.cs`.

## Packaging

`.claude-plugin/plugin.json` is the manifest. `.mcp.json` registers the MCP server as
`dotnet ${CLAUDE_PLUGIN_ROOT}/dist/DotnetToolkit.McpServer.dll` — a bare command plus arguments, because
an MCP stdio server is spawned directly rather than through a shell, so a `.sh` launcher is unrunnable
on Windows. The only requirement is `dotnet` on `PATH`, which the plugin needs anyway.

**The published server in `dist/` is what actually runs — after editing anything under `src/`, re-run
`dotnet publish src/DotnetToolkit.McpServer -c Release -o dist`** (or its `scripts/build-plugin.sh` /
`scripts/build-plugin.cmd` wrapper).

`hooks/hooks.json` ships five hooks — `hook guard-cs-edit`, `hook guard-cs-read`,
`hook guard-cs-bash-read`, `hook hint-reload-new-cs-file`, `hook hint-write-checklist` — all
subcommands of that same published
binary, documented in `docs/design/hooks.md`. They travel with the plugin, so a consuming repo gets
the enforcement from installation alone. They parse their payload with `System.Text.Json` and **fail
open** on anything unexpected: a workflow guard must never wedge editing. Nothing the plugin ships at
runtime requires a shell, a shebang, or `node`/`python3`/`jq`.

Guard deny messages point at `docs/tools/<tool>.md` rather than restating a tool's manual — the message
fires often, the manual is read once.

### How rules load

**`.claude/rules/` holds exactly one file: `dotnet-index.md`.** It is the only always-loaded rule,
because a rule with no `paths:` frontmatter loads unconditionally, and it is deliberately short for
that reason. Both it and `CLAUDE.md` are inherited by every subagent — the harness offers no
opt-out, and only the built-in Explore and Plan agents skip them — so a seven-way parallel review
pays them eight times.

**Why the coding standards are not rules.** A path-scoped rule fires only when the built-in `Read`
tool touches a matching file, and here `.cs` contact goes through the MCP tools or is blocked by the
guards — so `paths: ["**/*.cs"]` almost never fires. Worse, it is not a reliable *suppressor* either:
`guard-cs-read` deliberately allows `Read` on `.cs` files no project compiles (test fixtures,
`<Compile Remove>` exclusions, nested throwaway solutions), and reading one of those would have
matched the glob and injected all thirteen standards — roughly 80 KB — into the session and every
subagent inheriting it. Non-deterministic and invisible.

So the standards live in **`standards/` at the plugin root, outside any rules directory, with no
frontmatter at all**. They are read only by explicit path: the main agent at write time through
`dotnet-write`'s pre-edit step, and the review agent through the absolute `Standards root:` that
`dotnet-review` resolves and injects into each spawn prompt. That injection exists because
`${CLAUDE_PLUGIN_ROOT}` expansion is not guaranteed inside an agent definition, while a skill can
expand it reliably.

Nothing copies them into a consuming repo, so they cannot go stale there, and there is no per-repo
override tier — one copy of each standard exists, which is what keeps the writer and the reviewer
from judging against different text. A repo that needs different rules writes its own guidance into
its own `.claude/rules/`, outside this plugin. These standards are default guidance for **consuming
repos** installing this
plugin, not a description of this repo's own style specifically — though this repo's own code happens
to follow them.

## Environment: build with the same SDK the server uses

`Workspace/MSBuildRegistration.cs` chooses which SDK's MSBuild the workspace loads projects with, before
any `Microsoft.CodeAnalysis.Workspaces.MSBuild` code runs. This was a shell launcher's job until the
plugin had to run on Windows; it is now one implementation for every platform, and the chosen SDK is
logged to stderr at startup (`MSBuild: ...`) because a wrong choice degrades quietly.

`MSBuildLocator.RegisterDefaults()` alone is not enough: its .NET SDK discovery runs relative to the
`dotnet` host that started the process, so a server launched from a system-wide host never sees a newer
user-local SDK. The candidates from that query are therefore pooled with the SDKs under
`~/.dotnet`, and the highest version wins. Setting `DOTNET_TOOLKIT_DOTNET_ROOT` to a .NET install root
pins that install's newest SDK instead, overriding discovery entirely.

The failure this prevents: if the SDK that *built* the projects differs from the one MSBuild loads them
with, `obj/project.assets.json` was written for the wrong MSBuild and the workspace load fails with
`The "ResolvePackageAssets" task failed`. `workspace_status` then reports DEGRADED and semantic results
go silently **incomplete rather than erroring** — the dangerous part, since answers still come back.

`Register()` therefore also publishes `MSBuildRegistration.HostPath`: the `dotnet` executable belonging
to the install it registered, derived from the SDK directory (`<root>/sdk/<version>` → `<root>/dotnet`).
`WorkspaceHost.RestoreAsync` shells out to *that* host rather than resolving `dotnet` on `PATH`, because
the whole point of the discovery above is to prefer an install `PATH` does not point at — so a restore
left to `PATH` writes the very assets cache the registered MSBuild then cannot open, causing this
subsystem to inflict the exact failure it exists to prevent.

`Environment.ProcessPath` cannot carry that on its own: it is the `dotnet` muxer only when the process
was launched as `dotnet <app>.dll`, which is how `.mcp.json` starts the server but *not* how an apphost
starts. The integration-test fixture is the case that found this — xUnit v3 runs the test assembly as
its own executable, so `ProcessPath` was the test app, the restore fell back to `PATH`, and the fixture
hung for its full three-minute load timeout with no diagnostics. For the same reason the fixture calls
`MSBuildRegistration.Register()` rather than `MSBuildLocator.RegisterDefaults()`: the tests must load
projects on the SDK the server would pick, or they are not exercising the shipped configuration.

Check `dotnet --list-sdks` against the `MSBuild:` startup line. If they differ, build with the SDK the
server picked, or repair with a `restore` from that SDK followed by `reload_workspace`.
