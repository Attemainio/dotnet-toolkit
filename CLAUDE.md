# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Claude Code plugin for .NET repositories: a Roslyn-powered MCP server (`DotnetToolkit.McpServer`) exposing token-efficient code query, project navigation, and development-log tools, plus skills that teach Claude to prefer these tools over raw Read/Grep/`dotnet build`. This repo is both the plugin's implementation and (via `.mcp.json`) a live consumer of its own server.

## Commands

```bash
dotnet build                        # build the solution
dotnet test                         # unit + MSBuildWorkspace integration tests (tests/DotnetToolkit.McpServer.Tests)
dotnet test --filter FullyQualifiedName~ClassName   # run a single test class
./scripts/build-plugin.sh           # dotnet publish src/DotnetToolkit.McpServer -c Release -o dist; required after any server change for the plugin (dist/) to pick it up
```

`dotnet test` includes `WorkspaceIntegrationTests`, which loads `tests/DotnetToolkit.McpServer.Tests/fixtures/SampleSolution` via `MSBuildWorkspace` — expect it to be slower than the pure unit tests.

`TreatWarningsAsErrors` is set repo-wide (`Directory.Build.props`), so a build with warnings fails.

## Architecture

The server (`src/DotnetToolkit.McpServer/Program.cs`) starts over stdio (`WithStdioServerTransport`) and registers tools via `WithToolsFromAssembly`. **stdout is reserved for MCP JSON-RPC** — all logging goes to stderr (`LogToStandardErrorThreshold = LogLevel.Trace`); never write to `Console.Out` in server code.

Two independent knowledge tiers are built in the background so the MCP handshake completes within Claude Code's ~5s startup timeout — tool calls await readiness themselves rather than blocking startup:

- **Syntax index** (`Index/ProjectIndex.cs`, started via `StartInitialization()`) — every `.cs` file parsed with Roslyn, no MSBuild needed. Powers `find_symbol`, `outline`, `project_tree`, `list_folder` almost immediately.
- **MSBuild workspace** (`Workspace/WorkspaceHost.cs`, started via `StartLoading()`) — full semantic model via `MSBuildWorkspace`. Powers `find_references`, `find_implementations`, `get_symbol`, `diagnostics`. `Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults()` must run before any `Microsoft.CodeAnalysis.Workspaces.MSBuild` code touches assemblies — this happens at the very top of `Program.cs`, before the host builder is even constructed.

Other subsystems:

- `Workspace/SolutionLocator.cs` — auto-discovers the target solution (`*.slnx` > `*.sln` > `*.csproj`, root + one level deep) under `CLAUDE_PROJECT_DIR` (the target repo, set by Claude Code — not this repo, when installed as a plugin). `SlnxParser.cs` handles the newer `.slnx` format.
- `Workspace/ToolkitConfig.cs` — reads optional per-repo `.claude/dotnet-toolkit/config.json` (solution override, `devlogDir`, `excludeGlobs`, `defaultFormat`).
- `Devlog/` — structured WHAT/WHY/OBSERVATIONS/FIX entries in `devlog/<year>-W<week>.md` (project-root-level, not under `docs/` — configurable via `devlogDir`); `DevlogStore.cs` owns persistence, `DevlogSearch.cs` the searchable index (a rebuildable cache — hand-edited or conflicted files are re-indexed by mtime), `DevlogParser.cs`/`DevlogModel.cs` the markdown format with its hidden metadata comment per entry.
- `Output/` — `CompactFormatter.cs` and `OutlineRenderer.cs` render the default pipe-delimited compact format (see legend in README); `OutputFormat.cs` is the compact/json switch every read tool honors.
- `Tools/` — the four MCP tool groups: `RoslynTools.cs` (code queries), `StructureTools.cs` (navigation/index), `DevlogTools.cs`, `ServerTools.cs` (`ping`, `workspace_status`, `reload_workspace`).

Change detection across both tiers is **mtime-polling**, not filesystem watchers — this is deliberate so it works on WSL `/mnt/*` drives where inotify doesn't fire.

Caches for a target repo live in `.claude/dotnet-toolkit/cache/` under that repo (self-gitignored).

## Plugin packaging

`.claude-plugin/plugin.json` is the plugin manifest; `.mcp.json` registers the MCP server, launching it via `scripts/run-server.sh`, which prefers a user-local `~/.dotnet` install (needed on systems where the system-wide `dotnet` predates net10.0) over falling back to `dotnet` on `PATH`. The published server in `dist/` is what actually runs — after editing anything under `src/`, re-run `./scripts/build-plugin.sh` for a `claude --plugin-dir` session to see the change.

`skills/` (`dotnet-code-query`, `dotnet-navigation`, `dotnet-devlog`, `dotnet-review`) are the plugin's own skills, shipped to consumers to teach them to prefer these MCP tools over Read/Grep/`dotnet build`, and (via `dotnet-review`) when to delegate to the review agents below. Keep them in sync with actual tool behavior when tool signatures or output format change.

## Code review

`agents/` (plugin root, sibling to `skills/`) ships four read-only review subagents — a validation layer
that checks code against the standards recorded in `docs/`, not a source of standards itself. Each has no
`Edit`/`Write` tool access, never calls `devlog_add`, and has its own project-scoped memory:

- **`dotnet-reviewer`** — correctness/bugs, naming conventions, styling, idiomatic best practices.
- **`dotnet-performance`** — hot/cold-path performance: allocations, boxing, async correctness, caching.
- **`dotnet-refactor-cleaner`** — dead code and duplication, every dead-code claim backed by a stated
  `find_references` zero-hit check, never a guess. Reports removal candidates only.
- **`dotnet-doc-reviewer`** — XML documentation completeness/accuracy and inline-comment quality on public
  API surface. Unlike PandaAI's equivalent `doc-reviewer` agent (which has `Edit`/`Write` and fixes docs
  itself), this one only reports gaps — consistent with the report-only design shared by all four.

**Each agent's own file is deliberately thin** — frontmatter, a one-paragraph statement of its one
dimension, and a list of which `docs/*.md` files to read. All *process* (setup steps, diff/scope review
modes, devlog usage, output format, severity tags, boundaries, memory discipline) lives in one shared
`docs/review-workflow.md`, referenced by all four instead of restated four times. All *dimension content*
(what to actually check) lives in `docs/naming-conventions.md`, `docs/styling.md`, `docs/best-practices.md`,
`docs/performance.md`, `docs/xml-documentation.md`, and the shared `docs/common-antipatterns.md` catalog.
Agent files should only ever reference these docs, never restate their content — that's what keeps the
docs as the single source of truth the main agent reads too, rather than four forks of the same rules.

All four have the plugin's full read-side MCP toolset — Roslyn queries, structure queries, and read-only
devlog (`devlog_search`/`devlog_get`, never `devlog_add`) — so they can check whether an apparent violation
is actually a deliberately recorded decision before flagging it (see `docs/review-workflow.md`'s setup
step 2).

A consuming repo can override any doc in this list by placing its own copy at
`.claude/dotnet-toolkit/<name>.md` — every agent prefers that over the plugin's bundled default. The
`dotnet-review` skill teaches the main conversation which agent(s) to launch for a given request and how to
merge their output. These docs are default guidance for **consuming repos** installing this plugin, not a
description of this repo's own style specifically — though this repo's own code happens to follow them.
