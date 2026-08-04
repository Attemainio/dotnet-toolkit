# Architecture

How `DotnetToolkit.McpServer` is put together, and the packaging that turns it into a Claude Code
plugin. **Read this when a change touches server internals** — startup order, the two knowledge tiers,
a subsystem you haven't worked in, or how the plugin is delivered. Ordinary tool *usage* needs none of
it: `docs/tools/_index.md` routes that.

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
  `limitedBy: "index_only"`.
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
  rules), `ShapeFacts.cs` (the counted facts one symbol's column is built from) and `SymbolShape.cs`
  (the `P…M…N…L…O…D…C…A…` column on a search hit or a `get_symbol` member row, plus the legend text
  stated once per envelope). The renderer is deliberately kind-blind and ungated: every non-zero count
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
- `Hooks/` — the four Claude Code hooks, as a `hook <name>` subcommand of this same binary rather than
  as shell scripts. `HookCli.cs` dispatches and owns the fail-open boundary; `CsFileMembership.cs` and
  `BashCommandScanner.cs` carry the logic the read guards share. `docs/hook-reference.md`.
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

Then run the `dotnet-toolkit-consistency` skill, which owns the authoritative list of files describing
the tool surface and checks each against `Tools/*.cs`.

## Packaging

`.claude-plugin/plugin.json` is the manifest. `.mcp.json` registers the MCP server as
`dotnet ${CLAUDE_PLUGIN_ROOT}/dist/DotnetToolkit.McpServer.dll` — a bare command plus arguments, because
an MCP stdio server is spawned directly rather than through a shell, so a `.sh` launcher is unrunnable
on Windows. The only requirement is `dotnet` on `PATH`, which the plugin needs anyway.

**The published server in `dist/` is what actually runs — after editing anything under `src/`, re-run
`dotnet publish src/DotnetToolkit.McpServer -c Release -o dist`** (or its `scripts/build-plugin.sh` /
`scripts/build-plugin.cmd` wrapper).

`hooks/hooks.json` ships four hooks — `hook guard-cs-edit`, `hook guard-cs-read`,
`hook guard-cs-bash-read`, `hook hint-reload-new-cs-file` — all subcommands of that same published
binary, documented in `docs/hook-reference.md`. They travel with the plugin, so a consuming repo gets
the enforcement from installation alone. They parse their payload with `System.Text.Json` and **fail
open** on anything unexpected: a workflow guard must never wedge editing. Nothing the plugin ships at
runtime requires a shell, a shebang, or `node`/`python3`/`jq`.

Guard deny messages point at `docs/tools/<tool>.md` rather than restating a tool's manual — the message
fires often, the manual is read once.

### How rules load

`.claude/rules/csharp-standards.md` is the **master index** for coding standards and the one
always-loaded rule (no `paths:` frontmatter, deliberately short).

A path-scoped rule fires only when the built-in `Read` tool touches a matching file, and in this repo
`.cs` contact goes through the MCP tools or is blocked by the guards — so path-scoping `**/*.cs` almost
never fires here. Every other `.claude/rules/` file is read **explicitly, on demand**: by the main agent
at write time (via the index and `dotnet-change`'s pre-edit step) and by the review agent per
invocation. Their `paths: ["**/*.cs"]` frontmatter exists only to keep them out of the launch context,
not as a load mechanism.

A consuming repo can override any standards file by placing its own copy at
`.claude/dotnet-toolkit/<name>.md`; `dotnet-toolkit-init` can instead copy the whole set into the repo's
own `.claude/rules/`. These standards are default guidance for **consuming repos** installing this
plugin, not a description of this repo's own style specifically — though this repo's own code happens to
follow them.

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

Check `dotnet --list-sdks` against the `MSBuild:` startup line. If they differ, build with the SDK the
server picked, or repair with a `restore` from that SDK followed by `reload_workspace`.
