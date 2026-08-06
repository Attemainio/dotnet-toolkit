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
`dotnet-explore` agent, `validate_patch` as the only write path, which standards apply when, and
which skill to invoke — lives in **`.claude/rules/index.md`**. It is always-loaded alongside this
file, and is the same rule `dotnet-toolkit-init` copies into consuming repos. It is deliberately not
repeated here: a rule that lives only in `CLAUDE.md` never reaches a consumer, and two copies always
diverge.

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

`scripts/build-plugin.sh|.cmd` are thin wrappers over that publish line. Nothing the plugin ships at
runtime runs a shell: `.mcp.json` and `hooks/hooks.json` both invoke `dotnet <dll>`, so it works on
Windows as well as WSL/Linux/macOS. `dotnet test` includes `WorkspaceIntegrationTests`, which loads a
fixture solution via `MSBuildWorkspace` — slower than the pure unit tests. `TreatWarningsAsErrors` is
set repo-wide (`Directory.Build.props`), so a build with warnings fails.

**If more than one net10 SDK is installed, build with the same one the server registers for MSBuild**
(`~/.dotnet/dotnet` here). It picks the newest installed SDK and logs `MSBuild: ...` to stderr at
startup; `DOTNET_TOOLKIT_DOTNET_ROOT` pins a different install. Building with a different one silently
degrades the server's workspace — symptoms and repair in `docs/design/architecture.md`.

## Where to read what

Everything a *consumer* needs is routed from `.claude/rules/index.md`, not from here. These rows are
the maintainer's routes — files a consuming repo does not have.

| When | Read |
|---|---|
| Changing server internals, startup order, a subsystem, or packaging | `docs/design/architecture.md` |
| Changing the always-loaded rule, or what init ships | `.claude/rules/index.md`; `skills/dotnet-toolkit-init/SKILL.md` copies it |
| Changing a coding standard | `standards/<name>.md`, plus its row in `.claude/rules/index.md`'s table |
| Reviewing code, or changing the review agent | `agents/dotnet-code-review.md`; design rationale in `docs/design/agents.md` |
| Why a hook is built the way it is | `docs/design/hooks.md` (design notes — hooks fire from `hooks/hooks.json`; nothing routes to this file) |
| Auditing the install procedure, or what a consumer ends up with | `docs/install/audit.md` (maintainer side, run by `dotnet-toolkit-consistency`) and `docs/install/verify.md` (consumer side, run by `dotnet-toolkit-init`) |

**`.claude/rules/index.md` is the only always-loaded rule**, because it is the only file in
`.claude/rules/` with no frontmatter. The standards under `standards/` are read by explicit path
only — `paths:` frontmatter would not reliably load them here (see `docs/design/architecture.md`),
which is why they live outside `.claude/rules/` and carry none.

## Non-obvious invariants

- **stdout is reserved for MCP JSON-RPC.** All logging goes to stderr; never write to `Console.Out` in
  server code.
- **`dist/` is what runs**, not `src/` — for the MCP server *and* the five hooks. Re-publish after any
  server change.
- **Tool signature changes break in-process callers** — the tests call these methods positionally — and
  **any response-shape change needs `Contracts/Contract.cs` bumped.**
- **Change detection is mtime-polling, not filesystem watchers**, so it works on WSL `/mnt/*` where
  inotify doesn't fire. Don't "fix" it into a watcher.
- **`.claude/rules/` holds exactly one file**, and the standards live in `standards/` with no
  frontmatter. A second unfrontmattered rule, or a `paths:` key reappearing on a standard, is a
  silent always-loaded regression paid by every session *and* every subagent.
- **Any new code that shells out to a child process with redirected stdout+stderr must drain both
  streams concurrently** (`Task.WhenAll`, never one fully then the other) — an unread stream's OS
  pipe buffer fills and the child hangs forever. Shelling out to `dotnet` specifically needs two more
  environment variables on top of that, `MSBUILDDISABLENODEREUSE=1` and
  `DOTNET_CLI_USE_MSBUILD_SERVER=0`, because `dotnet restore`/`build` spawn persistent MSBuild worker
  nodes that inherit the redirected pipes and outlive the direct child. `WorkspaceHost.RestoreAsync`
  shipped this bug once (commit `71fe3fa`) and a second call site hit the MSBuild-node variant of it
  in the same session.
- **`.gitattributes` forces LF on `*.sh`/`*.cs`/`*.csproj`.** Windows-side tooling once CRLF'd
  `.cs` files, breaking raw string literals in two tests — if a new file type needs the same
  protection, add it there rather than fixing line endings reactively.

## Instruction consistency

The implemented tool surface (`Tools/*.cs`) is the ground truth for **facts** — names, signatures,
return shapes — and every doc, skill, rule, and hook message describing it is downstream. Official
Claude documentation is the ground truth for **design** — how a tool gets found, what is always
loaded, what a rule or skill may contain — and there the code is what moves.

- After changing a tool name, signature, or response contract — or a hook, script, skill, or documented
  workflow — **invoke `dotnet-toolkit-consistency`**. It owns the authoritative list of files that
  describe the surface, the checklist against official Claude guidance, and the drift sweep. Don't
  silently patch one file and move on; two copies always diverge.
- Update every affected file in the same task as the change.
- Update **this file** only when the change affects an always-applicable workflow, invariant, or route.
  Detailed mechanics, transient findings, and per-tool behavior belong in the shipped file that owns
  them, with at most a pointer here.
- Anything operational that lives *only* in this file never reaches a consuming repo — which is a
  finding, not a convenience.

## Context budget

**This file and `.claude/rules/index.md` are the only always-loaded files**, and the only two under a
size budget. Keep them declarations of *when* and *where*, not procedure.

The policy — the targets, what counts as scatter, why an overage is fixed by moving guidance behind a
pointer rather than deleting it, and the harness facts behind all of it — is owned and enforced by
`dotnet-toolkit-consistency` (`harness-compliance.md` §D–§F). Don't re-derive it here.

# Compact instructions

When compacting, preserve: the concrete task in flight and its remaining steps; any `symbolId`,
`contentVersion`, or `draftId` still in play; `validate_patch` failures and what was being corrected;
and decisions already settled with the user. Drop resolved tool output, file listings, and superseded
drafts — they are re-fetchable from the MCP tools in one call.
