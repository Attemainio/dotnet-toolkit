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

## Subsystems

- `Workspace/SolutionLocator.cs` — auto-discovers the target solution (`*.slnx` > `*.sln` > `*.csproj`,
  root + one level deep) under `CLAUDE_PROJECT_DIR` (the *target* repo — not this repo, when installed
  as a plugin). `SlnxParser.cs` handles `.slnx`.
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
  `EscalationTable.cs` (§13.2 rule table), `ValidationLadder.cs` (levels 1–4),
  `DiagnosticDistiller.cs` (root causes, suggested inspections, and the `locations` where each error
  landed in the proposed text's coordinates), `PatchDraftStore.cs` (bounded, 15-minute in-memory store
  of validated-but-unapplied patches — deliberately *not* in SQLite, since a draft describes a fork of
  the currently loaded workspace and is meaningless once that is gone).
- `Telemetry/` — per-call raw events and the read-side aggregations behind `get_retrieval_metrics`.
- `Git/` — `GitAnalyzer.cs` (git commands, run in a repository it discovers: the solution root when that
  is inside a work tree, otherwise the repos checked out beneath it) + `SemanticDiff.cs`, behind
  `get_semantic_diff`.
- `Control/ControlServer.cs` — a loopback TCP listener (127.0.0.1, OS-assigned port published at
  `CacheDir/control.port`) letting a hook trigger an index rescan (`rescan`, synchronous) or a
  background workspace reload (`reload`, fire-and-forget) without MCP stdio access; consumed by
  `scripts/hint-reload-new-cs-file.sh`. **Not a security boundary** — loopback-only, same trust level as
  the MCP session.
- `Tools/` — the MCP surface:

  | File | Tools |
  |---|---|
  | `ContextTools.cs` | `get_symbol`, `get_references`, `search_index` |
  | `FlowTools.cs` | `get_scope`, `get_call_slice`, `get_call_hierarchy`, `get_type_hierarchy` |
  | `GraphTools.cs` | `get_project_graph`, `detect_circular_dependencies` |
  | `HistoryTools.cs` | `get_semantic_diff`, `search_log` |
  | `PatchTools.cs` | `validate_patch` |
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

`.claude-plugin/plugin.json` is the manifest. `.mcp.json` registers the MCP server via
`scripts/run-server.sh`, which prefers a user-local `~/.dotnet` install (needed where the system
`dotnet` predates net10.0).

**The published server in `dist/` is what actually runs — after editing anything under `src/`, re-run
`./scripts/build-plugin.sh`.**

`hooks/hooks.json` ships four hooks — `guard-cs-edit.sh`, `guard-cs-read.sh`, `guard-cs-bash-read.sh`,
`hint-reload-new-cs-file.sh` — documented in `docs/hook-reference.md`. They travel with the plugin, so a
consuming repo gets the enforcement from installation alone. They read their JSON payload through
whichever of `node`/`python3`/`jq` is present and **fail open** when none is: a workflow guard must never
wedge editing.

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

`scripts/run-server.sh` prefers a user-local `~/.dotnet` when present. If the `dotnet` on `PATH` is a
*different* net10 SDK, building with it rewrites `obj/project.assets.json` for the wrong MSBuild and the
server's next workspace load fails with `The "ResolvePackageAssets" task failed`. `workspace_status` then
reports DEGRADED and semantic results go silently **incomplete rather than erroring** — the dangerous
part, since answers still come back.

Check `dotnet --list-sdks`. If they differ, build with `~/.dotnet/dotnet`, or repair with
`~/.dotnet/dotnet restore` followed by `reload_workspace`.
