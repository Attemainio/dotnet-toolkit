# dotnet-toolkit

A [Claude Code plugin](https://code.claude.com/docs/en/plugins) for .NET repositories
that cuts token usage with a C# MCP server: Roslyn-powered symbol retrieval with
content-version leases, and a write path that validates edits against an in-memory
compilation before they touch disk — plus skills that teach Claude to use them
instead of reading whole files.

It is an **add-in** for existing projects: it never replaces a repo's own CLAUDE.md
or conventions.

## Why

Grep and Read do not merely cost more tokens on C# — they give wrong answers. A text
search cannot see interface, virtual or delegate dispatch, counts comment and string
matches as real hits, and drops results silently when output is truncated. On a 290-file
repo, `grep -rn` for a method name found 3 of its 5 call sites and returned 58 comment
matches to hand-filter; `get_references` returned all 5 with none to discard, for a
fraction of the tokens.

## Features

| Domain | Tools | What it replaces |
|---|---|---|
| Symbol retrieval | `get_symbol`, `search_index` | Read over .cs files, ls/Glob to learn a codebase |
| Relationships | `get_references`, `get_call_slice`, `get_scope` | Grep for usages; guessing what is callable where |
| History | `get_semantic_diff`, `search_log` | Reading textual diffs; re-proposing rejected designs |
| Validated edits | `validate_patch` | Editing blind, then `dotnet build` output dumps |
| Self-observation | `get_retrieval_metrics` | Guessing where tokens went |
| Server | `ping`, `workspace_status`, `reload_workspace` | — |

Responses are JSON, deliberately terse: **absent fields carry no information**, and a
field is omitted rather than guessed. A caller count is reported only where the edge
cache actually covers that symbol's project, because a confident `0` reads as "nothing
uses this" when the truth may be "this project never loaded".

Every content response carries a layered `contentVersion` you can pass back as
`knownVersion`, so unchanged content is never re-sent. Edits go through `validate_patch`,
which runs the cheapest sufficient level of a validation ladder and reports honestly
whether that level was sufficient for the kind of change made.

`search_index` OR-es and ranks its terms, so one call answers for several names:
`query: "fee ledger TryBuy TrySell"` returns the symbols for all four. Names are indexed
split on both separators and camel-case boundaries, so `Ledger` finds `FIFOLedger`.

Four read-only review agents (`dotnet-reviewer`, `dotnet-performance`, `dotnet-refactor-cleaner`,
`dotnet-doc-reviewer`) ship alongside the tools — each starts with no prior project context, has
the read-side MCP toolset, reads bundled reference docs (`docs/*.md`, overridable per-repo under
`.claude/dotnet-toolkit/`), and reports findings without editing code. See `CLAUDE.md`'s Code
review section for details.

## Requirements

- .NET 10 SDK (the server targets `net10.0`, and analyzed projects need their SDK
  present). On WSL/Ubuntu: `sudo apt-get install -y dotnet-sdk-10.0`.
- Claude Code.

## Install

```bash
git clone <this repo> dotnet-toolkit
cd dotnet-toolkit
./scripts/build-plugin.sh          # publishes the server to dist/ (required once, and after updates)
```

For a permanent install, register the repo as a marketplace once from any Claude Code
session, then install from it:

```
/plugin marketplace add /path/to/dotnet-toolkit
/plugin install dotnet-toolkit@dotnet-toolkit-local
```

Or load it for a single session, without installing:

```bash
claude --plugin-dir /path/to/dotnet-toolkit
```

> `build-plugin.sh` republishes over `dist/`, which is what running servers execute. It
> will disconnect the MCP server in every open session using the plugin. Close them first,
> or expect to restart them.

## How it works

- The MCP server starts instantly and builds two knowledge tiers in the background:
  a **syntax index** (every .cs file parsed with Roslyn, no MSBuild needed — lets
  `search_index`/`get_symbol` answer within seconds, marked `staleness: "index_only"`)
  and an **MSBuild workspace** (full semantic model — powers `get_references` and
  `validate_patch` once loaded; check `workspace_status`).
- A SQLite knowledge store under the target repo holds the symbol index, an FTS index for
  discovery, a reference-edge cache, an append-only development log, and immutable usage
  telemetry. It is always rebuildable from source; deleting it just forces a rebuild.
- The target repo root comes from `CLAUDE_PROJECT_DIR` (set by Claude Code). The
  solution is auto-discovered (`*.slnx` > `*.sln` > `*.csproj`, root + one level deep).
  When several candidates exist the server refuses to guess and `workspace_status` tells
  you how to choose.
- Caches live in `.claude/dotnet-toolkit/cache/` in the target repo (self-gitignored).
- Change detection is mtime-polling based, so it works on WSL `/mnt/*` drives where
  inotify does not.
- Run `dotnet restore` in the target repo at least once (in the same OS you run
  Claude Code in) or semantic tools will report workspace load diagnostics.

### When a project fails to load

`workspace_status` marks the workspace `DEGRADED` and names the offending project. This
matters more than it sounds: reference edges come from the semantic model, so a project
MSBuild could not evaluate contributes none. Semantic results for that project are
incomplete, and `get_symbol` omits caller counts there rather than report a zero it
cannot justify. Anything that breaks `dotnet build` breaks this too — including a NuGet
audit escalating a vulnerability advisory to an error.

## Per-repo configuration (optional)

`.claude/dotnet-toolkit/config.json` in the target repo:

```json
{
  "solution": "src/MyApp.slnx",
  "excludeGlobs": ["**/Generated/**"]
}
```

`solution` resolves ambiguity when several solutions exist — write it, then call
`reload_workspace`. `excludeGlobs` keeps generated code out of the index.

(`devlogDir` is still read, but only by the one-time import of legacy markdown devlogs
described below. `defaultFormat` is vestigial: responses are JSON.)

## Development log

`validate_patch` records why each applied change was made, into the SQLite `feature_log`.
`search_log` queries it — use it before re-proposing a design, to find out whether an
approach was already tried and rejected.

Earlier versions wrote weekly markdown files to `devlog/<year>-W<week>.md`. Those are no
longer written or queried; a startup migration imports any existing entries into the
store once, and the markdown parser survives only to read that legacy format.

## Development

```bash
dotnet build
dotnet test        # unit + MSBuildWorkspace integration tests
./scripts/build-plugin.sh
```

`TreatWarningsAsErrors` is set repo-wide, so a build with warnings fails.

Layout: `src/DotnetToolkit.McpServer/` (`Tools/` the MCP surface, `Workspace/` MSBuild +
solution discovery, `Index/` + `Indexing/` the syntax index and edge builder, `Store/`
SQLite, `Fingerprint/` + `Contracts/` version layers and leases, `Validation/` the write
path, `Telemetry/`, `Git/`, `Identity/`, `Devlog/` legacy), `tests/` (xunit +
`fixtures/SampleSolution`), `skills/` (plugin skills), `agents/` (review subagents),
`docs/*.md` (review reference docs), `.claude-plugin/` (plugin + marketplace manifests),
`.mcp.json` (MCP registration).
