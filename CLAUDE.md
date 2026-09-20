# CLAUDE.md

Guidance for Claude Code when working in this repository. This file is the **operating contract**: the
rules that apply in every session, plus where to read the detail. It is not the repo's manual — anything
needed only for a particular kind of task lives behind a pointer below.

## What this is

A Claude Code plugin for .NET repositories: a Roslyn-powered MCP server (`DotnetToolkit.McpServer`)
exposing token-efficient symbol location, navigation and relationship tools, plus skills that teach
Claude to locate with these tools, read only the located range, and edit with `Edit`/`Write` while a
hook keeps the index current. This repo is both the plugin's implementation and (via `.mcp.json`) a
live consumer of its own server. Dogfooding is the point: if a tool is awkward or wrong for a real
task here, that's a bug report about the tool, not a reason to fall back to shell.

## Non-negotiable workflow

**`.claude/rules/dotnet-index.md` is a router and nothing else**: it names no tools, and maps an
intent (read C#, write C#, explore, review) to the skill that owns it. It is always-loaded alongside
this file, and is the same rule `dotnet-init` copies into consuming repos.

The protocol itself lives one level down, loaded only when the work needs it:

| Owns | File |
|---|---|
| Every retrieval tool, what each answers, the cheap-route table, `limitedBy`, TOON | `skills/dotnet-read/SKILL.md` |
| The write path (`Edit`/`Write` on located text, `rename_symbol`), the standards step, how the index follows edits, failures | `skills/dotnet-write/SKILL.md` |
| Briefing and launching the `dotnet-explore` agent | `skills/dotnet-explore/SKILL.md` |
| Partitioning and launching `dotnet-code-review` | `skills/dotnet-review/SKILL.md` |
| Which coding standard applies when — shared by the writer and the reviewer | `standards/index.md` |

None of it is repeated in `CLAUDE.md`: a rule that lives only here never reaches a consumer, and two
copies always diverge.

What this repo adds on top, because it is the plugin's own source tree:

**`dist/` is what runs, and it is committed.** The MCP server *and* the seven hooks execute from
`dist/`, and a consuming machine gets a working plugin from `git pull` alone. So after any change
under `src/` or `Hooks/`: run `dotnet test`, run `scripts/build-plugin.sh` (never the bare publish —
it leaves the source stamp stale), and **commit the republished `dist/` in the same commit as the
change that required it.** `dotnet dist/DotnetToolkit.McpServer.dll doctor --no-probe` fails when
`src/` has moved past the stamped build; run it before every commit that touches `src/`. A stale
committed `dist/` ships a stale binary to every consumer who pulls — a blocking error, the same as a
failing test. Commits go straight to `main`.

## Commands

```bash
dotnet build                        # build the solution
dotnet test                         # unit + MSBuildWorkspace integration tests (slower: loads a fixture solution)
dotnet test --filter FullyQualifiedName~ClassName   # a single test class
scripts/build-plugin.sh                 # required after any src/ or Hooks/ change
scripts/package-plugin.sh               # clean package/ for a LOCAL marketplace source — never point one at this tree
dotnet dist/DotnetToolkit.McpServer.dll doctor --no-probe   # pre-commit: is dist/ current?
```

`TreatWarningsAsErrors` is set repo-wide (`Directory.Build.props`), so a build with warnings fails.
The root `.editorconfig` is deliberately near-empty and states why in its own header; silencing a
rule there needs its reasoning in the same edit. **If more than one net10 SDK is installed, build with
the one the server registers for MSBuild** (`~/.dotnet/dotnet` here) — a different one silently
degrades the server's workspace; symptoms and repair in `docs/design/architecture.md`.

## Where to read what

Everything a *consumer* needs is routed from `.claude/rules/dotnet-index.md`, not from here. These
rows are the maintainer's routes — files a consuming repo does not have.

| When | Read |
|---|---|
| Changing server internals, startup order, a subsystem, packaging, or anything that shells out | `docs/design/architecture.md` |
| Changing the always-loaded rule, or what init ships | `.claude/rules/dotnet-index.md`; `skills/dotnet-init/SKILL.md` copies it |
| Changing which tool answers what, or a route finding | `skills/dotnet-read/SKILL.md` (reads) or `skills/dotnet-write/SKILL.md` (writes) — **not** the always-loaded rule |
| Changing a coding standard | `standards/<name>.md`, plus its row in `standards/index.md` |
| Reviewing code, or changing the review agent | `agents/dotnet-code-review.md`; design rationale in `docs/design/agents.md` |
| Why a hook is built the way it is, or why its off-switch expires | `docs/design/hooks.md` (design notes — hooks fire from `hooks/hooks.json`) |
| Measuring the plugin against not having it | `skills/dotnet-performance/SKILL.md` — runs at the default guard state; the only sanctioned caller of `set_hook_guards` (strict variant only) |
| Auditing the install procedure, or what a consumer ends up with | `docs/install/audit.md` (maintainer side, run by `dotnet-consistency`) and `docs/install/verify.md` (consumer side, run by `dotnet-init`) |
| Why the 1.x write path was removed | `docs/design/navigation-only.md` |

**`.claude/rules/dotnet-index.md` is the only always-loaded rule**, because it is the only file in
`.claude/rules/` with no frontmatter. The standards under `standards/` are read by explicit path
only — `paths:` frontmatter would not reliably load them here (see `docs/design/architecture.md`),
which is why they live outside `.claude/rules/` and carry none.

## Non-obvious invariants

- **stdout is reserved for MCP JSON-RPC.** All logging goes to stderr; never write to `Console.Out` in
  server code.
- **Tool signature changes break in-process callers** — the tests call these methods positionally — and
  **any response-shape change needs `Contracts/Contract.cs` bumped.**
- **Change detection is mtime-polling, not filesystem watchers**, so it works on WSL `/mnt/*` where
  inotify doesn't fire. Don't "fix" it into a watcher.
- **`.claude/rules/` holds exactly one file**, and the standards live in `standards/` with no
  frontmatter. A second unfrontmattered rule, or a `paths:` key reappearing on a standard, is a
  silent always-loaded regression paid by every session *and* every subagent.
- **Shelling out has three rules** — drain both streams concurrently, scrub the inherited MSBuild
  registration before launching `dotnet`, and treat process exit (never EOF) as completion. Each was
  learned from a real hang or a silent failure; the full statement is "Shelling out to a child
  process" in `docs/design/architecture.md`. Read it before writing any `Process.Start`.
- **`.gitattributes` forces LF on `*.sh`/`*.cs`/`*.csproj`.** Windows-side tooling once CRLF'd
  `.cs` files, breaking raw string literals in two tests — if a new file type needs the same
  protection, add it there rather than fixing line endings reactively.

## Instruction consistency

The implemented tool surface (`Tools/*.cs`) is the ground truth for **facts** — names, signatures,
return shapes — and every doc, skill, rule, and hook message describing it is downstream. Official
Claude documentation is the ground truth for **design** — how a tool gets found, what is always
loaded, what a rule or skill may contain — and there the code is what moves.

- After changing a tool name, signature, or response contract — or a hook, script, skill, or documented
  workflow — **invoke `dotnet-consistency`**. It owns the authoritative list of files that describe
  the surface, the checklist against official Claude guidance, and the drift sweep. Don't silently
  patch one file and move on; two copies always diverge.
- Update every affected file in the same task as the change, and `CHANGELOG.md` for anything a
  consumer sees.
- Update **this file** only when the change affects an always-applicable workflow, invariant, or route.
  Anything operational that lives *only* here never reaches a consuming repo — a finding, not a
  convenience.

## Context budget

**This file and `.claude/rules/dotnet-index.md` are the only always-loaded files**, and the only two
under a size budget. Keep them declarations of *when* and *where*, not procedure. The policy is owned
and enforced by `dotnet-consistency` (`harness-compliance.md` §D–§F); don't re-derive it here.

# Compact instructions

When compacting, preserve: the concrete task in flight and its remaining steps; any `symbolId` or
`declarationSites` range still in play; a `dotnet build` failure and what was being corrected; and
decisions already settled with the user. Drop resolved tool output, file listings, and superseded
drafts — they are re-fetchable from the MCP tools in one call.
