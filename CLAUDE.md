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
| `grep` for callers, or guessing who implements an interface | `get_references` (Roslyn semantic model — sees interface, virtual, and delegate dispatch) |
| `find`/`ls`/Glob to map a subsystem | `get_scope` |
| Manually tracing a call chain across files | `get_call_slice` |
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
sufficiency triple, and how to batch from `suggestedInspection`. The `dotnet-code-query` skill carries the
read protocol (session/task ids, expansion gating, leases) — follow it here too.

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
- `Tools/` — the MCP surface: `ContextTools.cs` (`get_symbol`, `get_references`, `search_index`), `FlowTools.cs` (`get_scope`, `get_call_slice`), `HistoryTools.cs` (`get_semantic_diff`, `search_log`), `PatchTools.cs` (`validate_patch`), `MetricsTools.cs` (`get_retrieval_metrics`), `ServerTools.cs` (`ping`, `workspace_status`, `reload_workspace`).

Change detection across both tiers is **mtime-polling**, not filesystem watchers — this is deliberate so it works on WSL `/mnt/*` drives where inotify doesn't fire.

Caches for a target repo live in `.claude/dotnet-toolkit/cache/` under that repo (self-gitignored).

## Plugin packaging

`.claude-plugin/plugin.json` is the plugin manifest; `.mcp.json` registers the MCP server, launching it via `scripts/run-server.sh`, which prefers a user-local `~/.dotnet` install (needed on systems where the system-wide `dotnet` predates net10.0) over falling back to `dotnet` on `PATH`. The published server in `dist/` is what actually runs — after editing anything under `src/`, re-run `./scripts/build-plugin.sh` for a `claude --plugin-dir` session to see the change.

`hooks/hooks.json` ships the `PreToolUse` guard described above, pointing at
`${CLAUDE_PLUGIN_ROOT}/scripts/guard-cs-edit.sh`. It travels with the plugin, so a consuming repo gets the
enforcement from installation alone — `dotnet-toolkit-init` neither installs nor needs to mention it as a
setup step. The script reads its JSON payload through whichever of `node`, `python3`, or `jq` is present
(none is guaranteed: `jq` is absent on this repo's own dev box, and Claude Code's native installer means
`node` cannot be assumed either) and allows the edit if none is.

The same file also ships a `PostToolUse` hook on `Write`, `scripts/hint-reload-new-cs-file.sh`. A `Write`
that creates a brand-new `.cs` file (the one case the `PreToolUse` guard above lets through) leaves the
syntax index and MSBuild workspace unaware of it — both are mtime-polling, not filesystem watchers, and a
new file is invisible until a full sweep runs and a subsequent workspace reload completes (see
`Index/ProjectIndex.cs`'s `EnsureFreshAsync`/`SweepAsync` and `Workspace/WorkspaceHost.cs`'s
`TriggerReload`). A `validate_patch`/`get_symbol` call against the new file before that finishes fails
deterministically with `invalid_edit: file is not part of the loaded solution` — there is no on-demand
fallback that opens an arbitrary path from disk. The hook cannot fix this itself (a hook is a separate
process with no access to the MCP stdio pipe, so it cannot call `reload_workspace` directly); it injects an
`additionalContext` reminder via `hookSpecificOutput` telling Claude to call `reload_workspace(scope: "all")`
itself before the next call touches the new file, so the reminder lands at file-creation time rather than
after a confusing failure. Same JSON-payload-reading fallback chain and fail-open behavior as the
`PreToolUse` guard, plus the JSON *reply* is built by that same interpreter rather than hand-interpolated,
since `file_path` is caller-controlled text (a Windows path's backslashes) that has no business near manual
JSON string escaping.

`.claude/rules/csharp-tools.md` is the reinforcement half, path-scoped to `**/*.cs` so it loads when a C#
file is touched rather than only at launch. Keep it short and keep it non-overlapping with the tool table
above: rules carry the *same* priority as CLAUDE.md, not higher, so a second copy of that table would buy
nothing and cost its tokens on every session that touches C#. It also cannot replace the table — a
path-scoped rule fires when Claude *reads* a matching file, and a session that reaches for Grep first may
never trigger it. Always-loaded steering belongs here; at-contact reminders belong there. The full
standalone protocol lives in `skills/dotnet-toolkit-init/SKILL.md`'s rule template, which is correct —
a *consuming* repo's CLAUDE.md gets only a pointer, so that copy has nothing above it to lean on.

`.claude/rules/csharp-standards.md` is a second, equally short reinforcement rule in this repo (also
path-scoped to `**/*.cs`) for the highest-cost-if-caught-late security/testing items — a pointer to
`docs/security.md`/`docs/testing.md`, not a copy of them, same rationale as `csharp-tools.md` above. In
`dotnet-toolkit-init`'s template for *consuming* repos the two are folded into one rule file instead of
kept separate, since that template is the full standalone protocol rather than a reinforcement pointer —
see that skill for why one file was chosen there.

`skills/` (`dotnet-code-query`, `dotnet-change`, `dotnet-review`, `dotnet-toolkit-init`) are the plugin's own skills, shipped to consumers. `dotnet-code-query` carries the retrieval protocol (session/task ids, resolution escalation, expansion gating, leases, refetch-after-compaction); `dotnet-change` carries the write protocol (baseVersions, required intent, the sufficiency triple, batching from `suggestedInspection`); `dotnet-review` says when to delegate to the review agents below; `dotnet-toolkit-init` writes an additive, approval-gated tool-usage *and coding-standards* rule into a *consuming* repo's own `.claude/rules/` plus a short CLAUDE.md pointer (backed up first, undoable) — installing the plugin makes the tools available, this skill is what makes a fresh session in that repo actually prefer them and follow the security/testing checklist at write time. It exists because a plugin can ship `docs/*.md` for its agent to read explicitly (`${CLAUDE_PLUGIN_ROOT}/docs/...`), but has no manifest field to make the harness auto-load a rule the way a consuming repo's own `.claude/rules/` gets scanned.

## Changing the tool surface: update the docs that teach it

**Whenever you add, remove, or change an MCP tool — its name, its arguments, its return shape, its defaults, or the behaviour a caller can override — update the files that describe it in the same change.** They are the only thing that tells a consuming agent the tool exists and how to call it; a tool nothing points at is a tool nobody uses.

This repo is its own consumer, so drift is self-inflicting: Claude working *in* this repo is taught by these same files, and a stale one degrades the next session here before it ever reaches a consumer.

The surface that has to move with the code:

| File | Carries |
| --- | --- |
| `skills/dotnet-code-query/SKILL.md` | the read protocol — every read tool, when to reach for it, escalation, leases, worked examples |
| `skills/dotnet-change/SKILL.md` | the write protocol — `validate_patch` arguments and the sufficiency rules |
| `skills/dotnet-review/SKILL.md` | which agent to delegate to, and the tools they rely on |
| `agents/dotnet-code-review.md` | the agent's `tools:` frontmatter list, and its dimension → doc(s) table — a tool absent from the former is unavailable to it; a dimension absent from the latter can't be requested |
| `docs/review-workflow.md` | how the review agents are told to use the read tools |
| `docs/tool-reference.md` | the complete per-tool catalog — arguments, a real example call/response, what it replaces — for every shipped tool; what `dotnet-toolkit-init` points a consuming repo at |
| `.claude/rules/csharp-tools.md` | this repo's own path-scoped C# rule — the tool table a session sees the moment it touches a `.cs` file |
| `skills/dotnet-toolkit-init/SKILL.md` | the rule-file template written into *consuming* repos, which embeds its own copy of the tool table |
| `scripts/guard-cs-edit.sh` | the deny message a blocked `Edit` returns — it restates the `validate_patch` procedure, so a wrong signature here teaches the wrong call at the worst moment |
| the `[Description]` attributes in `Tools/*.cs` | what the model sees before it has read any skill — the first and often only description it gets |

For each change, make sure the docs still carry:

- **the tool list** — every shipped tool appears somewhere a caller will look, and nothing appears that no longer exists;
- **usage** — what question the tool answers and when to prefer it over the alternative;
- **examples** — at least one real invocation with realistic arguments. Run it and use what it actually returned; an invented example that does not match the current signature is worse than none.

A concrete instance of this going wrong: `get_scope`, `get_call_slice`, and `get_semantic_diff` shipped in `FlowTools.cs`/`HistoryTools.cs` and are named in **none** of the files above, so no skill or agent has ever told anyone they exist.

Tool signature changes are also breaking for in-process callers (the tests call these methods positionally), and any change to response shape or lease behaviour needs `Contracts/Contract.cs` bumped. After editing anything under `src/`, re-run `./scripts/build-plugin.sh` or `dist/` still serves the old surface.

## Code review

`agents/dotnet-code-review.md` (plugin root, sibling to `skills/`) ships one read-only review subagent —
a validation layer that checks code against the standards recorded in `docs/`, not a source of standards
itself. It has no `Edit`/`Write` or `validate_patch` access, and it reviews exactly one **dimension** per
invocation, stated as `dimension: <name>` in its prompt — launch it once per dimension needed, in
parallel when more than one applies to a request:

- **`correctness`** (default) — bugs, naming conventions, styling, idiomatic best practices.
- **`performance`** — hot/cold-path performance: allocations, boxing, async correctness, caching.
- **`cleanup`** — dead code and duplication, every dead-code claim backed by a stated `get_references`
  zero-hit check, never a guess. Reports removal candidates only.
- **`docs`** — XML documentation completeness/accuracy and inline-comment quality on public API surface.
  Unlike PandaAI's equivalent `doc-reviewer` agent (which has `Edit`/`Write` and fixes docs itself), this
  only reports gaps — consistent with the report-only design shared by every dimension. Uses `get_symbol`'s
  `xmlDoc` component (`null` when a `<summary>` is absent) as the missing-doc signal, not a raw file read.
- **`testing`** — test coverage signal and test quality. Every coverage-gap claim is backed by a stated
  `get_references` check for a test-project caller, the same zero-hit discipline `cleanup` applies to dead
  code — never a guess from "this looks untested."
- **`security`** — secrets, injection, auth explicitness, CORS/transport, PII logging, data protection. No
  dedicated static scanner backs this dimension (no CVE/dependency check, no taint tracking) — findings
  come from reading source via `get_symbol` and tracing usage via `get_references`, same as every other
  dimension, and `docs/security.md` says so explicitly rather than implying broader coverage than it has.

**The agent's own file is deliberately thin** — frontmatter, and a table mapping each dimension to which
`docs/*.md` file(s) to read and its default review mode. All *process* (setup steps, diff/scope review
modes, output format, severity tags, boundaries, memory discipline) lives in one shared
`docs/review-workflow.md`, referenced regardless of dimension instead of restated per dimension. All
*dimension content* (what to actually check) lives in `docs/naming-conventions.md`, `docs/styling.md`,
`docs/best-practices.md`, `docs/performance.md`, `docs/xml-documentation.md`, `docs/testing.md`,
`docs/security.md`, and the shared `docs/common-antipatterns.md` catalog. The agent file should only ever
reference these docs, never restate their content — that's what keeps the docs as the single source of
truth the main agent reads too, rather than a per-dimension fork of the same rules. Adding a new dimension
is a new row in that table plus a new `docs/*.md` file — never a new agent file.

A short, path-scoped `.claude/rules/csharp-standards.md` (scoped to `**/*.cs`, same mechanism as
`.claude/rules/csharp-tools.md`) reinforces the handful of security/testing items where catching the
mistake at write-time is much cheaper than catching it at review time — a pointer with a few bullets, not
a restatement of `docs/security.md`/`docs/testing.md`. The review dimensions still check exhaustively
regardless of whether the rule fired; the rule exists to reduce how often they need to.

It has the read-side MCP toolset — `search_index`, `get_symbol`, `get_references`, `search_log`,
`get_scope`, `get_call_slice`, `get_semantic_diff` — uniform across every dimension, so the shared
`docs/review-workflow.md` can reference any of them without per-dimension conditionals. The point is
that it traces callers and implementations semantically rather than grepping, establishes reachability
instead of assuming it, and can check whether an apparent violation was a deliberately recorded decision
before asserting it. The log only covers changes applied
through `validate_patch`, so `docs/review-workflow.md` tells it an empty result is not proof of absence
and to mark such findings lower-confidence rather than asserting a violation.

A consuming repo can override any doc in this list by placing its own copy at
`.claude/dotnet-toolkit/<name>.md` — the agent prefers that over the plugin's bundled default for that
doc. The `dotnet-review` skill teaches the main conversation which dimension(s) to launch for a given
request and how to merge their output. These docs are default guidance for **consuming repos** installing
this plugin, not a description of this repo's own style specifically — though this repo's own code
happens to follow them.
