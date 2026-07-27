# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Claude Code plugin for .NET repositories: a Roslyn-powered MCP server (`DotnetToolkit.McpServer`) exposing token-efficient code query, project navigation, and development-log tools, plus skills that teach Claude to prefer these tools over raw Read/Grep/`dotnet build`. This repo is both the plugin's implementation and (via `.mcp.json`) a live consumer of its own server.

## Working in this repo: use the plugin's own tools

This repo consumes its own MCP server (`.mcp.json`), so the tools it ships are available here and are the
**default** way to explore and change C# — not Grep, Glob, `find`, `ls`, `cat`, or bare `Read` on `.cs`
files. Dogfooding is the point: if a tool is awkward or wrong for a real task here, that's a bug report
about the tool, not a reason to fall back to shell.

| Instead of | Use |
| --- | --- |
| `grep`/Grep for a type or member name | `search_index` (FTS-ranked, many terms per call) |
| `Read` on a `.cs` file to see a type or method | `get_symbol` (declaration/body layers, no whole-file read) |
| `Read`/`sed` to reach one region of a long member | `get_symbol` with `include: "source:code@120-160"` (absolute file lines; `;`-separate ranges) |
| `grep` for callers, or guessing who implements an interface | `get_references` (Roslyn semantic model — sees interface, virtual, and delegate dispatch) |
| `find`/`ls`/Glob to map a subsystem | `get_scope` |
| Manually tracing a call chain across files | `get_call_slice` |
| Chaining `get_references` by hand to build a callers/callees tree | `get_call_hierarchy` |
| Guessing a type's base chain/interfaces from `get_symbol`'s one-hop view | `get_type_hierarchy` |
| Opening every `.csproj` to trace project references by hand | `get_project_graph` |
| Manually tracing project references looking for a cycle | `detect_circular_dependencies` |
| `git diff` to judge what a change actually altered | `get_semantic_diff` |
| Guessing why code looks the way it does | `search_log` (the intent behind past changes) |
| Wondering whether the index/workspace is warm | `workspace_status`, then `reload_workspace` if stale |

### C# edits go through `validate_patch`

**`validate_patch` is the write path for `.cs` files, not a faster `dotnet build`.** `Edit`/`Write` on a
`.cs` file is the exception, and taking it should be a deliberate, stated choice — not the default because
it is fewer keystrokes.

**This is now enforced, not just asked for.** A `PreToolUse` hook blocks `Edit`/`Write`/`NotebookEdit` on
an existing `.cs` file and returns the procedure below instead (mechanics under "Plugin packaging"). A
blocked edit is the hook working, not a bug — rebuild the change as `validate_patch` calls rather than
looking for a way around it. Creating a *new* `.cs` file with `Write` is allowed, because `baseVersions`
needs a `symbolId` that does not exist yet; change the file through `validate_patch` after that.

This is the one rule in this file that is routinely broken, so it is worth stating why it matters. Applying
through `validate_patch` with an `intent` is the **only** thing that appends to the development log —
there is no other writer. Every edit made with `Edit` instead is a change whose reasoning is gone the
moment the conversation ends: `search_log` cannot recover it, and the next session re-derives or silently
contradicts it. The compile check is the cheap half of what the tool does; the log entry is the half that
is unrecoverable later.

**"This change is too large/interleaved to decompose into `validate_patch` calls" is not a valid reason to
use `Edit` instead.** It has been used as one, twice, and both times was wrong: split the change into more
`validate_patch` calls, one per touched symbol, rather than dropping the tool because the shape is
inconvenient. A signature change spanning several methods across two files is still just several
`validate_patch` calls sharing one `intent`, not a reason to fall back to `Edit`. If a lapse happens
anyway, backfill it immediately with a follow-up `validate_patch` call (an identity edit — current text
replaced with itself — still carries a real `intent` into the log) rather than leaving the log silent about
what happened.

A worked call, start to finish:

1. `get_symbol` on the target — keep its `contentVersion` and `declarationSites` line span.
2. `validate_patch` with `baseVersions: {symbolId: contentVersion}` and line-span `edits`, first with
   `applyOnSuccess: false` to see the ladder verdict without touching disk.
3. Re-send with `applyOnSuccess: true` and an `intent` in user terms once it reports
   `isSufficient: true`. Disk is written and the log entry appended in the same step.

Read `skills/dotnet-change/SKILL.md` before the first C# edit of a session for `baseVersions`, the
sufficiency triple, and how to batch from `suggestedInspection` — and read the relevant coding standards
from `.claude/rules/` per `csharp-standards.md`'s index in the same sitting; they are on-demand reads,
not auto-loaded. The `dotnet-code-query` skill carries the read protocol (session/task ids, expansion
gating, leases) — follow it here too.

Shell and plain file tools stay appropriate for what the MCP surface does not cover: `dotnet build` /
`dotnet test` / `./scripts/build-plugin.sh`, `git`, and reading or editing non-C# files (Markdown, JSON,
`.sh`, `.csproj`, skill and agent definitions).

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

- **Syntax index** (`Index/ProjectIndex.cs`, started via `StartInitialization()`) — every `.cs` file parsed with Roslyn, no MSBuild needed. Lets `search_index` and `get_symbol` answer almost immediately (marked `limitedBy: "index_only"`).
- **MSBuild workspace** (`Workspace/WorkspaceHost.cs`, started via `StartLoading()`) — full semantic model via `MSBuildWorkspace`. Powers `get_references` and `validate_patch`, and the live path of `get_symbol`. `Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults()` must run before any `Microsoft.CodeAnalysis.Workspaces.MSBuild` code touches assemblies — this happens at the very top of `Program.cs`, before the host builder is even constructed.

Other subsystems:

- `Workspace/SolutionLocator.cs` — auto-discovers the target solution (`*.slnx` > `*.sln` > `*.csproj`, root + one level deep) under `CLAUDE_PROJECT_DIR` (the target repo, set by Claude Code — not this repo, when installed as a plugin). `SlnxParser.cs` handles the newer `.slnx` format.
- `Workspace/ToolkitConfig.cs` — reads optional per-repo `.claude/dotnet-toolkit/config.json` (solution override, `devlogDir` — legacy, used only by the devlog import — and `excludeGlobs`; `defaultFormat` selects the response wire format via `Output/Formats.Render` — `toon` (default), `compact` (minified JSON), or `json` (pretty-printed JSON); see `search_log(query: "contract")` for the 3.9 rationale — `Contracts/Contract.cs` itself only records the current version number, not per-version history, see its own doc comment for why).
- `Devlog/` — **legacy, retained only for migration.** The markdown devlog (`devlog/<year>-W<week>.md`) is no longer written or queried by any tool; `DevlogMigration.cs` imports existing entries into the SQLite `feature_log` once at startup, and the parser/store remain solely to read that legacy format.
- `Store/` — the SQLite knowledge store (`KnowledgeStore.cs`, WAL + migration runner in `Schema.cs`): symbol index and reference edges (`SymbolStore.cs`), append-only development log (`FeatureLogStore.cs`), and immutable raw telemetry. Always rebuildable from source.
- `Fingerprint/` + `Contracts/` — `SyntaxFingerprint.cs` computes the `decl`/`body` version layers from token text (trivia-blind, so comments and formatting move nothing); `ContentVersion.cs`/`Lease.cs` implement the layered lease protocol.
- `Identity/` — ULIDs and the content-derived `symbolId`; `Workspace/SymbolKey.cs` derives ids from Roslyn symbols.
- `Validation/` — the write path: `PatchSandbox.cs` (forked in-memory solution), `ChangeClassifier.cs` (declaration delta → change kinds), `EscalationTable.cs` (§13.2 rule table), `ValidationLadder.cs` (levels 1–4), `DiagnosticDistiller.cs` (root causes + suggested inspections).
- `Telemetry/` — per-call raw events and the read-side aggregations behind `get_retrieval_metrics`.
- `Tools/` — the MCP surface: `ContextTools.cs` (`get_symbol`, `get_references`, `search_index`), `FlowTools.cs` (`get_scope`, `get_call_slice`, `get_call_hierarchy`, `get_type_hierarchy`), `GraphTools.cs` (`get_project_graph`, `detect_circular_dependencies`), `HistoryTools.cs` (`get_semantic_diff`, `search_log`), `PatchTools.cs` (`validate_patch`), `MetricsTools.cs` (`get_retrieval_metrics`), `ServerTools.cs` (`ping`, `set_output_format`, `workspace_status`, `reload_workspace`).

Change detection across both tiers is **mtime-polling**, not filesystem watchers — this is deliberate so it works on WSL `/mnt/*` drives where inotify doesn't fire.

Caches for a target repo live in `.claude/dotnet-toolkit/cache/` under that repo (self-gitignored).

## Plugin packaging

`.claude-plugin/plugin.json` is the plugin manifest; `.mcp.json` registers the MCP server, launching it via `scripts/run-server.sh`, which prefers a user-local `~/.dotnet` install (needed on systems where the system-wide `dotnet` predates net10.0) over falling back to `dotnet` on `PATH`. The published server in `dist/` is what actually runs — after editing anything under `src/`, re-run `./scripts/build-plugin.sh` for a `claude --plugin-dir` session to see the change.

`hooks/hooks.json` ships four hooks — `guard-cs-edit.sh`, `guard-cs-read.sh`, `guard-cs-bash-read.sh`,
`hint-reload-new-cs-file.sh` — fully documented in `docs/hook-reference.md` (matchers, allow/deny cases,
the solution-membership heuristic, limits). They travel with the plugin, so a consuming repo gets the
enforcement from installation alone; they read their JSON payload through whichever of `node`/`python3`/`jq`
is present and fail open when none is.

`.claude/rules/csharp-standards.md` is the **master index** for coding standards, restructured 2026-07
around a verified fact: a path-scoped rule fires only when the built-in `Read` tool touches a matching
file, and in this repo `.cs` contact goes through the MCP tools or is blocked by the guards — so
path-scoping `**/*.cs` almost never fires here. It is the one **always-loaded** rule (no `paths:`
frontmatter, deliberately short) and indexes every other file in `.claude/rules/`, each read
**explicitly, on demand** by the main agent (write time, via the index and the `dotnet-change` skill's
pre-edit step) and the review agent (all of them, per invocation). Those files' `paths: ["**/*.cs"]`
frontmatter exists only to keep them out of the launch context, not as a load mechanism.

`skills/` (`dotnet-code-query`, `dotnet-change`, `dotnet-review`, `dotnet-toolkit-init`, `dotnet-toolkit-consistency`) are the plugin's own skills, shipped to consumers — cataloged in `docs/skill-reference.md`. `dotnet-code-query` carries the retrieval protocol (session/task ids, resolution escalation, expansion gating, leases, refetch-after-compaction); `dotnet-change` carries the write protocol (baseVersions, required intent, the sufficiency triple, batching from `suggestedInspection`) plus the pre-edit standards-reading step; `dotnet-review` says when to delegate to the review agent below; `dotnet-toolkit-init` writes an additive, approval-gated tool-usage *and coding-standards* rule (always-loaded protocol rule *and copies of the standards files*) into a *consuming* repo's own `.claude/rules/` (backed up first, undoable) and never touches that repo's CLAUDE.md — `.claude/rules/` loads independently of CLAUDE.md, not appended into it, so the rule file is self-sufficient. It exists because a plugin can ship `docs/*.md`/rule files for explicit reads (`${CLAUDE_PLUGIN_ROOT}/...`), but has no manifest field to make the harness auto-load a rule the way a consuming repo's own `.claude/rules/` gets scanned — installing the plugin makes the tools available, this skill is what makes a fresh session in that repo actually prefer them and follow the security/testing checklist at write time. `dotnet-toolkit-consistency` is this repo's own internal audit — it checks `Tools/*.cs` against every file listed in "Changing the tool surface" below and fixes whatever has drifted; it ships to consumers too, but its primary use is on this repo itself.

**Invoke `dotnet-toolkit-consistency` whenever you notice — or suspect — that this plugin's own docs, skills, agent, rules, hooks, or `CLAUDE.md`/`README.md` are out of sync with the actual tool surface.** Concretely: after any tool addition/removal/rename/signature change, after editing a hook or script, after adding a new `docs/*.md` or `skills/*` file, or any time you catch a stale tool name, a missing row in one of the tables below, or a doc describing behavior the code no longer has. Don't silently patch one file and move on — run the skill so the fix is checked against every file that describes the same surface, not just the one you happened to be looking at.

## Changing the tool surface: update the docs that teach it

**Whenever you add, remove, or change an MCP tool — its name, its arguments, its return shape, its defaults, or the behaviour a caller can override — update the files that describe it in the same change.** They are the only thing that tells a consuming agent the tool exists and how to call it; a tool nothing points at is a tool nobody uses.

This repo is its own consumer, so drift is self-inflicting: Claude working *in* this repo is taught by these same files, and a stale one degrades the next session here before it ever reaches a consumer.

The surface that has to move with the code:

| File | Carries |
| --- | --- |
| `skills/dotnet-code-query/SKILL.md` | the read protocol — every read tool, when to reach for it, escalation, leases, worked examples |
| `skills/dotnet-change/SKILL.md` | the write protocol — `validate_patch` arguments and the sufficiency rules |
| `skills/dotnet-review/SKILL.md` | which agent to delegate to, and the tools they rely on |
| `agents/dotnet-code-review.md` | **the review agent's complete operating instructions** — `tools:` frontmatter, always-loaded standards core, retrieval protocol, evidence bars, modes, output format, boundaries. A tool absent from the frontmatter is unavailable to it; an aspect absent from the evidence bars is one it never checks |
| `docs/agent-reference.md` | **human-facing only — the agent does not read it.** The review agent's design rationale and token budget; must not drift from the agent file, which is the authority |
| `docs/tool-reference.md` | the complete per-tool catalog — arguments, a real example call/response, what it replaces — for every shipped tool; what `dotnet-toolkit-init` points a consuming repo at |
| `docs/hook-reference.md` | the four hooks and their scripts — matchers, allow/deny behavior, limits |
| `docs/skill-reference.md` | the catalog of shipped skills — one entry per skill, none stale |
| `.claude/rules/csharp-standards.md` | the always-loaded standards index — its file list must match the standards actually in `.claude/rules/`, and its `validate_patch` line must match the current write path |
| the standards files in `.claude/rules/` (list in `csharp-standards.md`'s index) | any MCP tool named in their review-calibration sections must still exist with the described behavior |
| `skills/dotnet-toolkit-init/SKILL.md` | the rule-file template written into *consuming* repos, which embeds its own copies of the tool table and the standards-file list |
| `scripts/guard-cs-edit.sh` | the deny message a blocked `Edit` returns — it restates the `validate_patch` procedure, so a wrong signature here teaches the wrong call at the worst moment |
| `scripts/guard-cs-read.sh` | the deny message a blocked `Read` returns — it restates the `search_index`/`get_symbol` alternatives, so a stale tool description here teaches the wrong call at the worst moment |
| `scripts/guard-cs-bash-read.sh` | the deny message a blocked Bash read returns — mirrors `guard-cs-read.sh`'s alternatives from the shell side, so a stale tool description here teaches the wrong call at the worst moment |
| the `[Description]` attributes in `Tools/*.cs` | what the model sees before it has read any skill — the first and often only description it gets |
| `skills/dotnet-toolkit-consistency/SKILL.md` | the audit itself — its Step 4 table is a second copy of this table's row list, so a row added here needs the matching row added there too |

For each change, make sure the docs still carry the tool list, usage guidance (what question it answers,
when to prefer it), and at least one real, run-and-verified invocation — an invented example that doesn't
match the current signature is worse than none.

Tool signature changes are also breaking for in-process callers (the tests call these methods positionally), and any change to response shape or lease behaviour needs `Contracts/Contract.cs` bumped. After editing anything under `src/`, re-run `./scripts/build-plugin.sh` or `dist/` still serves the old surface.

## Code review

`agents/dotnet-code-review.md` (plugin root, sibling to `skills/`) ships one read-only review subagent —
a validation layer that checks code against the standards in `.claude/rules/`, not a source of standards
itself. The standards are shared with the main agent (which reads them at write time per
`csharp-standards.md`'s index), so writer and reviewer work from one source of truth. The agent has no
`validate_patch` access, and reviews all quality aspects of one stated scope per invocation —
parallelism is by scope partition (one instance per disjoint slice), never by aspect.

**The agent file is self-contained and the per-instance token baseline is a design constraint.** Every
parallel instance re-pays the same fixed startup cost, so it is multiplied by the partition count: seven
instances once started at ~43k each before reading any reviewed code. Three things hold it down and
should not be undone casually — selective standards loading (six core files always, the other seven only
when `csharp-standards.md`'s "When" column matches the retrieved code), no `skills:` grant (the 41.5 KB
`dotnet-code-query` protocol is written for the main agent's write path, not a reviewer), and batched
`get_symbol` retrieval over the whole scope instead of per-symbol round-trips. Selective loading trades
tokens for a coverage risk, paid for by the mandatory `Standards:` line on every report — an untriggered
aspect is **not-assessed**, never reported clean. `docs/agent-reference.md` records the rationale for
maintainers; the agent no longer reads it.

**Its read-only property is enforced by instruction, not by tool grant.** The `tools:` frontmatter
omits `Edit`/`Write`, but `memory: project` makes the harness grant them anyway so the agent can
maintain `.claude/agent-memory/dotnet-toolkit-dotnet-code-review/` — the resolved tool list does
include `Write` and `Edit`. What keeps it from touching source or standards is the agent file's own
Memory and Boundaries sections, not a capability boundary. Don't reason about this agent as if it were
sandboxed.

**Process, review modes, aspect tags/evidence bars, output format, and boundaries all live in
`agents/dotnet-code-review.md` — not restated here.** Adding a new aspect is a new `.claude/rules/*.md`
file, a row in `csharp-standards.md`'s index stating its trigger as an observable property of the code,
and one entry in the agent's evidence bars — never a new agent file. Decide explicitly whether it joins
the always-loaded core or the triggered set.

A consuming repo can override any standards file by placing its own copy at
`.claude/dotnet-toolkit/<name>.md` (`dotnet-toolkit-init` can instead copy the whole set into the repo's
own `.claude/rules/` for local ownership). The `dotnet-review` skill teaches the main conversation how to
partition a target into scopes and merge instances' output. These standards are default guidance for
**consuming repos** installing this plugin, not a description of this repo's own style specifically —
though this repo's own code happens to follow them.
