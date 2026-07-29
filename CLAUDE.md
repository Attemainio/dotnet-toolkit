# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Claude Code plugin for .NET repositories: a Roslyn-powered MCP server (`DotnetToolkit.McpServer`)
exposing token-efficient code query, project navigation, and development-log tools, plus skills that
teach Claude to prefer these tools over raw Read/Grep/`dotnet build`. This repo is both the plugin's
implementation and (via `.mcp.json`) a live consumer of its own server.

## Working in this repo: use the plugin's own tools

This repo consumes its own MCP server, so the tools it ships are available here and are the
**default** way to explore and change C# — not Grep, Glob, `find`, `ls`, `cat`, or bare `Read` on
`.cs` files. Dogfooding is the point: if a tool is awkward or wrong for a real task here, that's a
bug report about the tool, not a reason to fall back to shell.

**`docs/tools/_index.md` is the router** — which tool answers which question, and the common call
chains. Read it when you need to pick a tool, then read the one `docs/tools/<tool>.md` for how to
call it. Don't read the whole directory, and don't read `Tools/*.cs` to learn a signature.

Shell and plain file tools stay appropriate for what the MCP surface does not cover: `dotnet build`
/ `dotnet test` / `./scripts/build-plugin.sh`, `git`, and reading or editing non-C# files (Markdown,
JSON, `.sh`, `.csproj`, skill and agent definitions).

### C# edits go through `validate_patch`

**`validate_patch` is the write path for `.cs` files, not a faster `dotnet build`.** A `PreToolUse`
hook blocks `Edit`/`Write`/`NotebookEdit` on an existing `.cs` file and returns the procedure
instead. A blocked edit is the hook working, not a bug — rebuild the change as `validate_patch`
calls rather than looking for a way around it. Creating a *new* `.cs` file with `Write` is allowed,
because `baseVersions` needs a `symbolId` that does not exist yet; change the file through
`validate_patch` after that.

This is the one rule here that is routinely broken, so it is worth stating why it matters. Applying
through `validate_patch` with an `intent` is the **only** thing that appends to the development log
— there is no other writer. Every edit made with `Edit` instead is a change whose reasoning is gone
the moment the conversation ends: `search_log` cannot recover it, and the next session re-derives or
silently contradicts it. The compile check is the cheap half of what the tool does; the log entry is
the half that is unrecoverable later.

**"This change is too large/interleaved to decompose into `validate_patch` calls" is not a valid
reason to use `Edit` instead.** It has been used as one, twice, and both times was wrong: split the
change into more `validate_patch` calls, one per touched symbol. A signature change spanning several
methods across two files is still just several `validate_patch` calls sharing one `intent`. If a
lapse happens anyway, backfill it immediately with a follow-up `validate_patch` call (an identity
edit — current text replaced with itself — still carries a real `intent` into the log) rather than
leaving the log silent.

The call, start to finish: `get_symbol` for `contentVersion` + `declarationSites`, then
`validate_patch` with `baseVersions`, line-span `edits`, `applyOnSuccess: true`, and an `intent`.
On failure, amend through the returned `draftId` — don't rebuild the patch. Full procedure and
failure modes: **`docs/tools/validate_patch.md`**.

Read `skills/dotnet-change/SKILL.md` before the first C# edit of a session, and the relevant coding
standards from `.claude/rules/` per `csharp-standards.md`'s index in the same sitting; they are
on-demand reads, not auto-loaded.

## Commands

```bash
dotnet build                        # build the solution
dotnet test                         # unit + MSBuildWorkspace integration tests (tests/DotnetToolkit.McpServer.Tests)
dotnet test --filter FullyQualifiedName~ClassName   # run a single test class
./scripts/build-plugin.sh           # dotnet publish src/DotnetToolkit.McpServer -c Release -o dist; required after any server change for the plugin (dist/) to pick it up
```

`dotnet test` includes `WorkspaceIntegrationTests`, which loads
`tests/DotnetToolkit.McpServer.Tests/fixtures/SampleSolution` via `MSBuildWorkspace` — expect it to
be slower than the pure unit tests.

`TreatWarningsAsErrors` is set repo-wide (`Directory.Build.props`), so a build with warnings fails.

**Build with the same SDK the server uses.** `scripts/run-server.sh` prefers a user-local
`~/.dotnet` when present, so if the `dotnet` on `PATH` is a *different* net10 SDK, building with it
rewrites `obj/project.assets.json` for the wrong MSBuild and the server's next workspace load fails
with `The "ResolvePackageAssets" task failed` — `workspace_status` then reports DEGRADED and semantic
results go silently incomplete rather than erroring. Check `dotnet --list-sdks`; if they differ, build
with `~/.dotnet/dotnet`, or repair with `~/.dotnet/dotnet restore` + `reload_workspace`.

## Architecture

The server (`src/DotnetToolkit.McpServer/Program.cs`) starts over stdio and registers tools via
`WithToolsFromAssembly`. **stdout is reserved for MCP JSON-RPC** — all logging goes to stderr; never
write to `Console.Out` in server code.

Two independent knowledge tiers are built in the background so the MCP handshake completes within
Claude Code's ~5s startup timeout — tool calls await readiness themselves rather than blocking
startup:

- **Syntax index** (`Index/ProjectIndex.cs`, `StartInitialization()`) — every `.cs` file parsed with
  Roslyn, no MSBuild needed. Lets `search_index` and `get_symbol` answer almost immediately (marked
  `limitedBy: "index_only"`).
- **MSBuild workspace** (`Workspace/WorkspaceHost.cs`, `StartLoading()`) — full semantic model.
  Powers `get_references` and `validate_patch`, and the live path of `get_symbol`.
  `MSBuildLocator.RegisterDefaults()` must run before any `Microsoft.CodeAnalysis.Workspaces.MSBuild`
  code touches assemblies — this happens at the very top of `Program.cs`, before the host builder is
  constructed.

Other subsystems:

- `Workspace/SolutionLocator.cs` — auto-discovers the target solution (`*.slnx` > `*.sln` >
  `*.csproj`, root + one level deep) under `CLAUDE_PROJECT_DIR` (the target repo — not this repo,
  when installed as a plugin). `SlnxParser.cs` handles `.slnx`.
- `Workspace/ToolkitConfig.cs` — optional per-repo `.claude/dotnet-toolkit/config.json`: solution
  override, `devlogDir` (legacy, devlog import only), `excludeGlobs`, and `defaultFormat` (`toon`
  default / `compact` / `json`) via `Output/Formats.Render`.
- `Devlog/` — **legacy, retained only for migration.** The markdown devlog is no longer written or
  queried; `DevlogMigration.cs` imports existing entries into the SQLite `feature_log` once at
  startup.
- `Store/` — the SQLite knowledge store (`KnowledgeStore.cs`, WAL + migrations in `Schema.cs`):
  symbol index and reference edges (`SymbolStore.cs`), append-only development log
  (`FeatureLogStore.cs`), immutable raw telemetry. Always rebuildable from source.
- `Fingerprint/` + `Contracts/` — `SyntaxFingerprint.cs` computes the `decl`/`body` version layers
  from token text (trivia-blind, so comments and formatting move nothing); `ContentVersion.cs`
  implements the layered version token every content response carries.
- `Identity/` — ULIDs and the content-derived `symbolId`; `Workspace/SymbolKey.cs` derives ids from
  Roslyn symbols. Four disjoint id namespaces, deliberately never sharing a hash space: `sym_`
  (live, doc-comment-derived), `symfb_` (`Ids.FallbackSymbolId`, when `GetDocumentationCommentId()`
  returns null), `symidx_` (`Ids.IndexOnlySymbolId`, `get_symbol`'s syntax-only fallback), `draft_`
  (`Ids.Draft()`, a validated-but-unapplied patch). `validate_patch` rejects any non-`sym_` id in
  `baseVersions` with `stale_index_only_id` rather than a confusing `stale_base` cascade.
- `Validation/` — the write path: `PatchSandbox.cs` (forked in-memory solution, optionally seeded
  from a draft's proposed text), `ChangeClassifier.cs` (declaration delta → change kinds),
  `EscalationTable.cs` (§13.2 rule table), `ValidationLadder.cs` (levels 1–4),
  `DiagnosticDistiller.cs` (root causes, suggested inspections, and the `locations` where each error
  landed in the proposed text's coordinates), `PatchDraftStore.cs` (bounded, 15-minute in-memory
  store of validated-but-unapplied patches — deliberately not in SQLite, since a draft describes a
  fork of the currently loaded workspace and is meaningless once that is gone).
- `Telemetry/` — per-call raw events and the read-side aggregations behind `get_retrieval_metrics`.
- `Control/ControlServer.cs` — a loopback TCP listener (127.0.0.1, OS-assigned port published at
  `CacheDir/control.port`) letting a hook trigger an index rescan (`rescan`, synchronous) or a
  background workspace reload (`reload`, fire-and-forget) without MCP stdio access; consumed by
  `scripts/hint-reload-new-cs-file.sh`. Not a security boundary — loopback-only, same trust level as
  the MCP session.
- `Tools/` — the MCP surface: `ContextTools.cs` (`get_symbol`, `get_references`, `search_index`),
  `FlowTools.cs` (`get_scope`, `get_call_slice`, `get_call_hierarchy`, `get_type_hierarchy`),
  `GraphTools.cs` (`get_project_graph`, `detect_circular_dependencies`), `HistoryTools.cs`
  (`get_semantic_diff`, `search_log`), `PatchTools.cs` (`validate_patch`), `MetricsTools.cs`
  (`get_retrieval_metrics`), `ServerTools.cs` (`ping`, `set_output_format`, `workspace_status`,
  `reload_workspace`). `ToolTelemetry.cs` is not a tool group — it is the single place a response
  becomes a `RetrievalEvent`, plus the shared `[Description]` text for the optional `taskId`; the
  five tools taking no `TelemetryRecorder` (`ServerTools`' four and `get_retrieval_metrics`) record
  nothing by design.

Change detection across both tiers is **mtime-polling**, not filesystem watchers — deliberate, so it
works on WSL `/mnt/*` drives where inotify doesn't fire.

Caches for a target repo live in `.claude/dotnet-toolkit/cache/` under that repo (self-gitignored).

## Plugin packaging

`.claude-plugin/plugin.json` is the manifest; `.mcp.json` registers the MCP server via
`scripts/run-server.sh`, which prefers a user-local `~/.dotnet` install (needed where the system
`dotnet` predates net10.0). **The published server in `dist/` is what actually runs — after editing
anything under `src/`, re-run `./scripts/build-plugin.sh`.**

`hooks/hooks.json` ships four hooks — `guard-cs-edit.sh`, `guard-cs-read.sh`,
`guard-cs-bash-read.sh`, `hint-reload-new-cs-file.sh` — documented in `docs/hook-reference.md`. They
travel with the plugin, so a consuming repo gets the enforcement from installation alone; they read
their JSON payload through whichever of `node`/`python3`/`jq` is present and fail open when none is.

`.claude/rules/csharp-standards.md` is the **master index** for coding standards and the one
always-loaded rule (no `paths:` frontmatter, deliberately short). A path-scoped rule fires only when
the built-in `Read` tool touches a matching file, and in this repo `.cs` contact goes through the MCP
tools or is blocked by the guards — so path-scoping `**/*.cs` almost never fires here. Every other
`.claude/rules/` file is read **explicitly, on demand**: by the main agent at write time (via the
index and `dotnet-change`'s pre-edit step) and by the review agent per invocation. Their
`paths: ["**/*.cs"]` frontmatter exists only to keep them out of the launch context, not as a load
mechanism.

`skills/` ships seven skills, cataloged in `docs/skill-reference.md`:

- **`dotnet-code-query`** — the read protocol: when to reach for which tool, expansion gating,
  symbol addressing, workspace readiness. Deliberately small; per-tool mechanics live in
  `docs/tools/`.
- **`dotnet-change`** — the write protocol: `baseVersions`, required `intent`, the sufficiency
  triple, batching from `suggestedInspection`, plus the pre-edit standards-reading step.
- **`dotnet-review`** — when to delegate to the review agent, and how to partition scopes.
- **`dotnet-toolkit-init`** — writes an additive, approval-gated tool-usage *and coding-standards*
  rule into a *consuming* repo's own `.claude/rules/` (backed up first, undoable). It never touches
  that repo's CLAUDE.md, because `.claude/rules/` loads independently. It exists because a plugin can
  ship files for explicit reads (`${CLAUDE_PLUGIN_ROOT}/...`) but has no manifest field to make the
  harness auto-load a rule the way a consuming repo's own `.claude/rules/` gets scanned.
- **`dotnet-toolkit-install-check`** — audits `dotnet-toolkit-init` against the plugin tree: every
  shipped file must fall in exactly one delivery mechanism (ships active / must be copied /
  referenced by `${CLAUDE_PLUGIN_ROOT}` path / created at runtime), init's write and uninstall lists
  must name the same files, the consumer's CLAUDE.md must be untouched, and the protocol rule it
  writes must stay a ~6 KB declaration rather than a copy of the workflow. Read-only.
- **`dotnet-toolkit-consistency`** — this repo's internal audit: checks `Tools/*.cs` as ground truth
  against every file that describes the tool surface, and fixes what has drifted. **It owns the
  authoritative list of those files.** Its consumer-reachability step is the counterpart to
  `install-check`: anything operational that lives only in this CLAUDE.md or in a maintainer's memory
  never reaches a consuming repo, and is a finding.
- **`dotnet-toolkit-selfeval`** — the complementary *efficiency* audit: a fixed probe matrix over
  every tool, measuring each call's exact token cost from `get_retrieval_metrics` deltas isolated by
  a caller-supplied `taskId`. Read-only; never fixes what it finds, and every finding is about this
  plugin, never about the consuming repo's code.

**Invoke `dotnet-toolkit-consistency` whenever you notice — or suspect — that this plugin's own
docs, skills, agent, rules, hooks, or `CLAUDE.md`/`README.md` are out of sync with the actual tool
surface.** Concretely: after any tool addition/removal/rename/signature change, after editing a hook
or script, after adding a new `docs/*.md` or `skills/*` file, or any time you catch a stale tool
name or a doc describing behavior the code no longer has. Don't silently patch one file and move on
— run the skill so the fix is checked against every file that describes the same surface.

Tool signature changes are also breaking for in-process callers (the tests call these methods
positionally), and any change to response shape needs `Contracts/Contract.cs` bumped.

## Context budget

This plugin's own instruction files are its largest fixed cost, and drift toward verbosity is the
failure mode. Two hard limits, enforced by `dotnet-toolkit-consistency`:

- **No `SKILL.md` over ~5k tokens (~19 KB).** After auto-compaction Claude Code re-attaches only the
  first 5,000 tokens of each invoked skill (25k shared across all of them), so a larger skill is
  silently truncated mid-session — its later sections stop existing while its decision table still
  points at them. Push per-tool mechanics into `docs/tools/<tool>.md`, which is read on demand and
  has no such cliff.
- **CLAUDE.md and `.claude/rules/csharp-standards.md` are the only always-loaded files.** Everything
  added to them is paid by every session in this repo regardless of task. Prefer a skill or a
  `docs/` file with a pointer from here.

Guard deny messages point at `docs/tools/<tool>.md` rather than restating a tool's manual — the
message fires often, the manual is read once.

# Compact instructions

When compacting, preserve: the concrete task in flight and its remaining steps; any `symbolId`,
`contentVersion`, or `draftId` still in play; `validate_patch` failures and what was being corrected;
and decisions already settled with the user. Drop resolved tool output, file listings, and superseded
drafts — they are re-fetchable from the MCP tools in one call.

## Code review

`agents/dotnet-code-review.md` (plugin root, sibling to `skills/`) ships one read-only review
subagent — a validation layer that checks code against the standards in `.claude/rules/`, not a
source of standards itself. Writer and reviewer share those standards, so there is one source of
truth. The agent has no `validate_patch` access and reviews all quality aspects of one stated scope
per invocation — parallelism is by scope partition (one instance per disjoint slice), never by
aspect.

**The agent file is self-contained and the per-instance token baseline is a design constraint.**
Every parallel instance re-pays the same fixed startup cost, multiplied by the partition count: seven
instances once started at ~43k each before reading any reviewed code. Three things hold it down and
should not be undone casually — selective standards loading (six core files always, the other seven
only when `csharp-standards.md`'s "When" column matches the retrieved code), no `skills:` grant, and
batched `get_symbol` retrieval over the whole scope instead of per-symbol round-trips. Selective
loading trades tokens for a coverage risk, paid for by the mandatory `Standards:` line on every
report — an untriggered aspect is **not-assessed**, never reported clean. `docs/agent-reference.md`
records the rationale for maintainers; the agent no longer reads it.

**Its read-only property is enforced by instruction, not by tool grant.** The `tools:` frontmatter
omits `Edit`/`Write`, but `memory: project` makes the harness grant them anyway so the agent can
maintain `.claude/agent-memory/dotnet-toolkit-dotnet-code-review/` — the resolved tool list does
include `Write` and `Edit`. What keeps it from touching source or standards is the agent file's own
Memory and Boundaries sections, not a capability boundary. Don't reason about this agent as if it
were sandboxed.

**Process, review modes, aspect tags/evidence bars, output format, and boundaries all live in
`agents/dotnet-code-review.md` — not restated here.** Adding a new aspect is a new
`.claude/rules/*.md` file, a row in `csharp-standards.md`'s index stating its trigger as an
observable property of the code, and one entry in the agent's evidence bars — never a new agent
file. Decide explicitly whether it joins the always-loaded core or the triggered set.

A consuming repo can override any standards file by placing its own copy at
`.claude/dotnet-toolkit/<name>.md` (`dotnet-toolkit-init` can instead copy the whole set into the
repo's own `.claude/rules/`). These standards are default guidance for **consuming repos** installing
this plugin, not a description of this repo's own style specifically — though this repo's own code
happens to follow them.
