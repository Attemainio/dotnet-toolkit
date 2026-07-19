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

- **Syntax index** (`Index/ProjectIndex.cs`, started via `StartInitialization()`) — every `.cs` file parsed with Roslyn, no MSBuild needed. Lets `search_index` and `get_symbol` answer almost immediately (marked `staleness: "index_only"`).
- **MSBuild workspace** (`Workspace/WorkspaceHost.cs`, started via `StartLoading()`) — full semantic model via `MSBuildWorkspace`. Powers `get_references` and `validate_patch`, and the live path of `get_symbol`. `Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults()` must run before any `Microsoft.CodeAnalysis.Workspaces.MSBuild` code touches assemblies — this happens at the very top of `Program.cs`, before the host builder is even constructed.

Other subsystems:

- `Workspace/SolutionLocator.cs` — auto-discovers the target solution (`*.slnx` > `*.sln` > `*.csproj`, root + one level deep) under `CLAUDE_PROJECT_DIR` (the target repo, set by Claude Code — not this repo, when installed as a plugin). `SlnxParser.cs` handles the newer `.slnx` format.
- `Workspace/ToolkitConfig.cs` — reads optional per-repo `.claude/dotnet-toolkit/config.json` (solution override, `devlogDir` — legacy, used only by the devlog import — and `excludeGlobs`; `defaultFormat` is vestigial now that responses are JSON-only).
- `Devlog/` — **legacy, retained only for migration.** The markdown devlog (`devlog/<year>-W<week>.md`) is no longer written or queried by any tool; `DevlogMigration.cs` imports existing entries into the SQLite `feature_log` once at startup, and the parser/store remain solely to read that legacy format.
- `Store/` — the SQLite knowledge store (`KnowledgeStore.cs`, WAL + migration runner in `Schema.cs`): symbol index and reference edges (`SymbolStore.cs`), append-only development log (`FeatureLogStore.cs`), and immutable raw telemetry. Always rebuildable from source.
- `Fingerprint/` + `Contracts/` — `SyntaxFingerprint.cs` computes the `decl`/`body` version layers from token text (trivia-blind, so comments and formatting move nothing); `ContentVersion.cs`/`Lease.cs` implement the layered lease protocol.
- `Identity/` — ULIDs and the content-derived `symbolId`; `Workspace/SymbolKey.cs` derives ids from Roslyn symbols.
- `Validation/` — the write path: `PatchSandbox.cs` (forked in-memory solution), `ChangeClassifier.cs` (declaration delta → change kinds), `EscalationTable.cs` (§13.2 rule table), `ValidationLadder.cs` (levels 1–4), `DiagnosticDistiller.cs` (root causes + suggested inspections).
- `Telemetry/` — per-call raw events and the read-side aggregations behind `get_retrieval_metrics`.
- `Tools/` — the MCP surface: `ContextTools.cs` (`get_symbol`, `get_references`, `search_index`), `PatchTools.cs` (`validate_patch`), `MetricsTools.cs` (`get_retrieval_metrics`), `ServerTools.cs` (`ping`, `workspace_status`, `reload_workspace`).

Change detection across both tiers is **mtime-polling**, not filesystem watchers — this is deliberate so it works on WSL `/mnt/*` drives where inotify doesn't fire.

Caches for a target repo live in `.claude/dotnet-toolkit/cache/` under that repo (self-gitignored).

## Plugin packaging

`.claude-plugin/plugin.json` is the plugin manifest; `.mcp.json` registers the MCP server, launching it via `scripts/run-server.sh`, which prefers a user-local `~/.dotnet` install (needed on systems where the system-wide `dotnet` predates net10.0) over falling back to `dotnet` on `PATH`. The published server in `dist/` is what actually runs — after editing anything under `src/`, re-run `./scripts/build-plugin.sh` for a `claude --plugin-dir` session to see the change.

`skills/` (`dotnet-code-query`, `dotnet-change`, `dotnet-review`) are the plugin's own skills, shipped to consumers. `dotnet-code-query` carries the retrieval protocol (session/task ids, resolution escalation, expansion gating, leases, refetch-after-compaction); `dotnet-change` carries the write protocol (baseVersions, required intent, the sufficiency triple, batching from `suggestedInspection`); `dotnet-review` says when to delegate to the review agents below. Keep them in sync with actual tool behavior when tool signatures or output format change.

## Code review

`agents/` (plugin root, sibling to `skills/`) ships four read-only review subagents — a validation layer
that checks code against the standards recorded in `docs/`, not a source of standards itself. Each has no
`Edit`/`Write` or `validate_patch` access, and has its own project-scoped memory:

- **`dotnet-reviewer`** — correctness/bugs, naming conventions, styling, idiomatic best practices.
- **`dotnet-performance`** — hot/cold-path performance: allocations, boxing, async correctness, caching.
- **`dotnet-refactor-cleaner`** — dead code and duplication, every dead-code claim backed by a stated
  `get_references` zero-hit check, never a guess. Reports removal candidates only.
- **`dotnet-doc-reviewer`** — XML documentation completeness/accuracy and inline-comment quality on public
  API surface. Unlike PandaAI's equivalent `doc-reviewer` agent (which has `Edit`/`Write` and fixes docs
  itself), this one only reports gaps — consistent with the report-only design shared by all four.

**Each agent's own file is deliberately thin** — frontmatter, a one-paragraph statement of its one
dimension, and a list of which `docs/*.md` files to read. All *process* (setup steps, diff/scope review
modes, output format, severity tags, boundaries, memory discipline) lives in one shared
`docs/review-workflow.md`, referenced by all four instead of restated four times. All *dimension content*
(what to actually check) lives in `docs/naming-conventions.md`, `docs/styling.md`, `docs/best-practices.md`,
`docs/performance.md`, `docs/xml-documentation.md`, and the shared `docs/common-antipatterns.md` catalog.
Agent files should only ever reference these docs, never restate their content — that's what keeps the
docs as the single source of truth the main agent reads too, rather than four forks of the same rules.

All four have the read-side MCP toolset — `search_index`, `get_symbol`, `get_references`, `search_log` — so
they trace callers and implementations semantically rather than grepping, and can check whether an apparent
violation was a deliberately recorded decision before asserting it. The log only covers changes applied
through `validate_patch`, so `docs/review-workflow.md` tells them an empty result is not proof of absence
and to mark such findings lower-confidence rather than asserting a violation.

A consuming repo can override any doc in this list by placing its own copy at
`.claude/dotnet-toolkit/<name>.md` — every agent prefers that over the plugin's bundled default. The
`dotnet-review` skill teaches the main conversation which agent(s) to launch for a given request and how to
merge their output. These docs are default guidance for **consuming repos** installing this plugin, not a
description of this repo's own style specifically — though this repo's own code happens to follow them.
