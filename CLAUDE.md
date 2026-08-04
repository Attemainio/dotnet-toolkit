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

The tool protocol — MCP tools over Grep/`Read` for C#, delegating an unknown symbol sweep to the
`dotnet-explore` agent, and `validate_patch` as the only write path — lives in
**`.claude/rules/tool-protocol.md`**. It is always-loaded alongside this file, and is the same rule
`dotnet-toolkit-init` copies into consuming repos. It is deliberately not repeated here: a rule that
lives only in `CLAUDE.md` never reaches a consumer, and two copies always diverge.

What this repo adds on top, because it is the plugin's own source tree:

**Before finishing**, run `dotnet test` and, if anything under `src/` changed, re-publish to `dist/`
— `dist/` is what actually runs, so a server change is not delivered until it is republished. **`dist/`
now also carries the hooks**, so a change under `Hooks/` is live only after republishing too.

## Commands

```bash
dotnet build                        # build the solution
dotnet test                         # unit + MSBuildWorkspace integration tests
dotnet test --filter FullyQualifiedName~ClassName   # a single test class
dotnet publish src/DotnetToolkit.McpServer -c Release -o dist   # required after any src/ change
```

`scripts/build-plugin.sh` and `scripts/build-plugin.cmd` are thin wrappers over that publish line.
Nothing the plugin ships at runtime runs a shell: `.mcp.json` and `hooks/hooks.json` both invoke
`dotnet <dll>` so the plugin works on Windows as well as WSL/Linux/macOS.

`dotnet test` includes `WorkspaceIntegrationTests`, which loads a fixture solution via
`MSBuildWorkspace` — expect it to be slower than the pure unit tests.

`TreatWarningsAsErrors` is set repo-wide (`Directory.Build.props`), so a build with warnings fails.

**If more than one net10 SDK is installed, build with the same one the server registers for MSBuild**
(`~/.dotnet/dotnet` here). It picks the newest installed SDK and logs `MSBuild: ...` to stderr at
startup; `DOTNET_TOOLKIT_DOTNET_ROOT` pins a different install. Building with a different one silently
degrades the server's workspace — symptoms and repair in `docs/architecture.md`.

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
| Changing either always-loaded rule, or what init ships | `.claude/rules/tool-protocol.md` + `csharp-standards.md`; `skills/dotnet-toolkit-init/SKILL.md` copies both |
| Auditing the install procedure, or what a consumer ends up with | `docs/install-audit.md` (maintainer side, run by `dotnet-toolkit-consistency`) and `docs/install-verify.md` (consumer side, run by `dotnet-toolkit-init`) |

Standards in `.claude/rules/` are **on-demand reads, not auto-loaded** — only `tool-protocol.md` and
`csharp-standards.md` are always present, because only those two lack `paths:` frontmatter.

## Non-obvious invariants

- **stdout is reserved for MCP JSON-RPC.** All logging goes to stderr; never write to `Console.Out` in
  server code.
- **`dist/` is what runs**, not `src/` — for the MCP server *and* the four hooks. Re-publish after any
  server change.
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
- **Three files are always-loaded: this one, `.claude/rules/tool-protocol.md`, and
  `.claude/rules/csharp-standards.md`** — the two rules because they alone carry no `paths:`
  frontmatter. Everything added to any of them is paid by every session regardless of task, and the
  two rules are paid again by every *consuming* repo, since init copies both. Prefer a skill or a
  `docs/` file with a pointer. Keep this file under **~10 KB (~150 lines)**, and `tool-protocol.md`
  and `csharp-standards.md` each under ~6 KB; an architecture rundown, tool catalog, skill catalog,
  or per-tool procedure growing back into any of them is the regression to watch for.

# Compact instructions

When compacting, preserve: the concrete task in flight and its remaining steps; any `symbolId`,
`contentVersion`, or `draftId` still in play; `validate_patch` failures and what was being corrected;
and decisions already settled with the user. Drop resolved tool output, file listings, and superseded
drafts — they are re-fetchable from the MCP tools in one call.
