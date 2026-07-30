# CLAUDE.md

Guidance for Claude Code when working in this repository. This file is the **operating contract**: the
rules that apply in every session, plus where to read the detail. It is not the repo's manual — anything
needed only for a particular kind of task lives behind a pointer below.

## What this is

A Claude Code plugin for .NET repositories: a Roslyn-powered MCP server (`DotnetToolkit.McpServer`)
exposing token-efficient code query, project navigation, and development-log tools, plus skills that
teach Claude to prefer these tools over raw Read/Grep/`dotnet build`. This repo is both the plugin's
implementation and (via `.mcp.json`) a live consumer of its own server. Dogfooding is the point: if a
tool is awkward or wrong for a real task here, that's a bug report about the tool, not a reason to fall
back to shell.

## Non-negotiable workflow

**Explore C# through the MCP tools**, not Grep, Glob, `find`, `ls`, `cat`, or bare `Read` on `.cs`
files. Pick the tool via `docs/tools/_index.md`, then read that one `docs/tools/<tool>.md` for how to
call it — don't read the whole directory, and don't read `Tools/*.cs` to learn a signature.

**Change existing `.cs` files through `validate_patch`.** A `PreToolUse` hook blocks
`Edit`/`Write`/`NotebookEdit` on an existing `.cs` file; a blocked edit is the hook working, not a bug.
Creating a *new* `.cs` file with `Write` is allowed, because `baseVersions` needs a `symbolId` that
does not exist yet — change it through `validate_patch` after that.

- The call: `get_symbol` for `contentVersion` + `declarationSites` (with `include: "all"` when the edit
  rewrites a body — the default token carries no `body` layer and is rejected as `unleased_body`), then
  `validate_patch` with `baseVersions`, line-span `edits`, `applyOnSuccess: true`, and an `intent`. On
  failure, amend the returned `draftId` rather than rebuilding the patch.
- **Always give a real `intent`.** Applying with one is the only thing that appends to the development
  log; an `Edit` that slips past the guard is reasoning `search_log` can never recover. The compile
  check is the cheap half of the tool — the log entry is the half that is unrecoverable later.
- **"Too large or interleaved to decompose" is not a reason to use `Edit`.** It has been used as one
  twice, and was wrong both times: split it into more `validate_patch` calls, one per touched symbol,
  sharing one `intent`. If a lapse happens anyway, backfill it immediately with a follow-up call — an
  identity edit still carries a real `intent` into the log.
- Full arguments and every failure mode: **`docs/tools/validate_patch.md`**.

**Use shell and plain file tools for what the MCP surface doesn't cover**: `dotnet build` / `dotnet
test` / `./scripts/build-plugin.sh`, `git`, and reading or editing non-C# files (Markdown, JSON, `.sh`,
`.csproj`, skill and agent definitions).

**Before finishing**, run `dotnet test` and, if anything under `src/` changed, `./scripts/build-plugin.sh`
— `dist/` is what actually runs, so a server change is not delivered until it is republished.

## Commands

```bash
dotnet build                        # build the solution
dotnet test                         # unit + MSBuildWorkspace integration tests
dotnet test --filter FullyQualifiedName~ClassName   # a single test class
./scripts/build-plugin.sh           # publish to dist/; required after any src/ change
```

`dotnet test` includes `WorkspaceIntegrationTests`, which loads a fixture solution via
`MSBuildWorkspace` — expect it to be slower than the pure unit tests.

`TreatWarningsAsErrors` is set repo-wide (`Directory.Build.props`), so a build with warnings fails.

**If more than one net10 SDK is installed, build with the same one `scripts/run-server.sh` picks**
(`~/.dotnet/dotnet` here). Building with a different one silently degrades the server's workspace —
symptoms and repair in `docs/architecture.md`.

## Where to read what

| When | Read |
|---|---|
| Picking a tool for a C# question | `docs/tools/_index.md`, then the one `docs/tools/<tool>.md` |
| Before the first C# edit of a session | `skills/dotnet-change/SKILL.md` + the standards `csharp-standards.md`'s index names for the change |
| Which coding standard applies | `.claude/rules/csharp-standards.md` (the master index) |
| Changing server internals, startup order, a subsystem, or packaging | `docs/architecture.md` |
| What a hook blocks and why | `docs/hook-reference.md` |
| What a skill is for | `docs/skill-reference.md` |
| Reviewing code, or changing the review agent | `agents/dotnet-code-review.md`; design rationale in `docs/agent-reference.md` |

Standards in `.claude/rules/` are **on-demand reads, not auto-loaded** — only
`csharp-standards.md` is always present.

## Non-obvious invariants

- **stdout is reserved for MCP JSON-RPC.** All logging goes to stderr; never write to `Console.Out` in
  server code.
- **`dist/` is what runs**, not `src/`. Re-run `./scripts/build-plugin.sh` after any server change.
- **Tool signature changes break in-process callers** — the tests call these methods positionally — and
  **any response-shape change needs `Contracts/Contract.cs` bumped.**
- **Change detection is mtime-polling, not filesystem watchers**, so it works on WSL `/mnt/*` where
  inotify doesn't fire. Don't "fix" it into a watcher.
- Review parallelism is by **scope partition**, one instance per disjoint slice — never by aspect.

## Instruction consistency

The implemented tool surface (`Tools/*.cs`) is the ground truth; every doc, skill, rule, and hook
message describing it is downstream.

- After changing a tool name, signature, or response contract — or a hook, script, skill, or documented
  workflow — **invoke `dotnet-toolkit-consistency`**. It owns the authoritative list of files that
  describe the surface. Don't silently patch one file and move on; two copies always diverge.
- Update every affected file in the same task as the change.
- Update **this file** only when the change affects an always-applicable workflow, invariant, or route.
  Detailed mechanics, transient findings, and per-tool behavior belong in the shipped file that owns
  them, with at most a pointer here.
- Anything operational that lives *only* in this file never reaches a consuming repo — which is a
  finding, not a convenience.

## Task clarification

- Resolve uncertainties from repository evidence — code, tests, docs, tool output — before asking.
- Ask before implementing when ambiguity materially changes scope, behavior, a public API, data
  compatibility, or what counts as done.
- Don't ask what the code can answer safely, and don't hold up work that doesn't depend on the answer.

## Context budget

This plugin's own instruction files are its largest fixed cost, and drift toward verbosity is the
failure mode. Both limits are enforced by `dotnet-toolkit-consistency`:

- **No `SKILL.md` over ~5k tokens (~19 KB).** After auto-compaction Claude Code re-attaches only the
  first 5,000 tokens of each invoked skill (25k shared across all of them), so a larger skill is
  silently truncated mid-session — its later sections stop existing while its decision table still
  points at them. Push per-tool mechanics into `docs/tools/<tool>.md`, read on demand with no such cliff.
- **This file and `.claude/rules/csharp-standards.md` are the only always-loaded files.** Everything
  added to them is paid by every session regardless of task. Prefer a skill or a `docs/` file with a
  pointer from here. Keep this file under **~10 KB (~150 lines)** and
  `csharp-standards.md` under ~6 KB; an architecture rundown, tool catalog, skill catalog, or per-tool
  procedure growing back here is the regression to watch for.

# Compact instructions

When compacting, preserve: the concrete task in flight and its remaining steps; any `symbolId`,
`contentVersion`, or `draftId` still in play; `validate_patch` failures and what was being corrected;
and decisions already settled with the user. Drop resolved tool output, file listings, and superseded
drafts — they are re-fetchable from the MCP tools in one call.
