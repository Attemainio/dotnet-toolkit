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
| Symbol retrieval | `get_symbol`, `search_index` | Read over .cs files, ls/Glob to learn a codebase; `sed`/line-range reads to reach one region of a long member (`include: "source:code@120-160"`) |
| Relationships | `get_references`, `get_call_slice`, `get_scope`, `get_call_hierarchy`, `get_type_hierarchy` | Grep for usages; guessing what is callable where; manually tracing callers/overrides across files |
| Project structure | `get_project_graph`, `detect_circular_dependencies` | Reading `.csproj`/`.sln` references by hand to map project dependencies |
| History | `get_semantic_diff`, `search_log` | Reading textual diffs; re-proposing rejected designs |
| Validated edits | `validate_patch` | Editing blind, then `dotnet build` output dumps |
| Self-observation | `get_retrieval_metrics` | Guessing where tokens went |
| Server | `ping`, `set_output_format`, `workspace_status`, `reload_workspace` | — |

Responses are JSON, deliberately terse: **absent fields carry no information**, and a
field is omitted rather than guessed. A caller count is reported only where the edge
cache actually covers that symbol's project, because a confident `0` reads as "nothing
uses this" when the truth may be "this project never loaded".

Every content response carries a layered `contentVersion` for your own manual drift-detection
across calls. Edits go through `validate_patch`, which runs the cheapest sufficient level of a
validation ladder and reports honestly whether that level was sufficient for the kind of change
made.

`search_index` OR-es and ranks its terms, so one call answers for several names:
`query: "fee ledger TryBuy TrySell"` returns the symbols for all four. Names are indexed
split on both separators and camel-case boundaries, so `Ledger` finds `FIFOLedger`.

One read-only review agent (`dotnet-code-review`) ships alongside the tools. Each invocation reviews
**all quality aspects at once** — correctness, performance, concurrency, cleanup, docs, testing,
security — over one precisely stated scope; large targets are partitioned into disjoint scopes reviewed
by parallel instances of the same agent. It starts with no prior project context, has the read-side MCP
toolset, reads the bundled coding standards (`.claude/rules/*.md`, overridable per-repo under
`.claude/dotnet-toolkit/`), and reports findings without editing code. The same standards files are what
the main agent reads at write time, so writer and reviewer share one source of truth.

Because a parallel run re-pays each instance's startup cost, the agent reads a fixed core of six
standards every time and loads the other seven only when the code it retrieved matches their trigger
condition in `.claude/rules/csharp-standards.md`. Every report ends with a `Standards:` line naming what
was loaded and what was not, so an untriggered aspect reads as *not assessed* rather than clean. See
`CLAUDE.md`'s Code review section and `docs/agent-reference.md` for details.

## Making Claude actually prefer these tools

Installing the plugin makes the tools *available*; it doesn't make a fresh session in your repo
*prefer* them over Grep/Read/`dotnet build`, and it can't auto-load the coding standards (plugins have
no mechanism for that). Run `/dotnet-toolkit-init` once per repo to fix both: it drafts an additive,
always-loaded `.claude/rules/dotnet-toolkit-csharp.md` protocol rule (backing off if another plugin
already governs code search there) plus copies of the standards files (list in
`.claude/rules/csharp-standards.md`'s index), shows you the exact plan,
and writes only after you approve — backing up anything it replaces so it's fully reversible. It never
modifies your `CLAUDE.md`: `.claude/rules/` loads independently, so the rule files stand on their own.
See `skills/dotnet-toolkit-init/SKILL.md` for the process and `docs/tool-reference.md` for the complete
per-tool reference the rule file points at.

> Upgrading from an earlier version: the standards used to live in `docs/` under different names
> (`naming-conventions.md`, `common-antipatterns.md`, `review-workflow.md`, …). They are now
> `.claude/rules/{naming,antipatterns,…}.md` plus `docs/agent-reference.md`, and per-repo overrides
> under `.claude/dotnet-toolkit/` must use the new file names.

## Requirements

- .NET 10 SDK (the server targets `net10.0`, and analyzed projects need their SDK
  present). On WSL/Ubuntu: `sudo apt-get install -y dotnet-sdk-10.0`.
- Claude Code.

## Install

This plugin isn't in a public Claude Code marketplace yet — the repo itself doubles as the
install source. Clone it and publish the server once before first use:

```bash
git clone https://github.com/Attemainio/dotnet-toolkit dotnet-toolkit
cd dotnet-toolkit
./scripts/build-plugin.sh          # publishes the server to dist/ (required once, and after updates)
```

Then pick one of two ways to load it, depending on whether you want it for one session or
every session:

**Single session, nothing installed** — pass the path directly:

```bash
claude --plugin-dir /path/to/dotnet-toolkit
```

Run this from anywhere; it doesn't matter which repo you `cd` into first, since the target
repo is whatever `CLAUDE_PROJECT_DIR`/cwd resolves to when Claude Code starts. Nothing is
written to your global or project Claude Code config — the plugin is only active for that one
process, and closing it is the entire "uninstall."

**Persistent, every session** — register the clone as a local marketplace once, then install
from it like any other plugin:

```
/plugin marketplace add /path/to/dotnet-toolkit
/plugin install dotnet-toolkit@dotnet-toolkit-local
```

This is a genuine install: it's recorded in your Claude Code settings (see "Uninstall" below)
and the plugin loads automatically in every future session without the `--plugin-dir` flag.

> `build-plugin.sh` republishes over `dist/`, which is what running servers execute. It
> will disconnect the MCP server in every open session using the plugin — either way you
> loaded it. Close other sessions first, or expect to restart them, and run
> `/plugin reload-plugins` (or restart) to pick the rebuilt server up without a full restart.

## Uninstall

What to remove depends on which install path you used:

- **Loaded via `--plugin-dir`**: nothing was recorded anywhere — just stop passing the flag. There is no
  further step.
- **Installed from the local marketplace**: reverse the two `/plugin` commands from Install, in order:

  ```
  /plugin uninstall dotnet-toolkit@dotnet-toolkit-local
  /plugin marketplace remove dotnet-toolkit-local
  /plugin reload-plugins
  ```

  The interactive `/plugin` menu's **Installed** tab does the same thing if you'd rather click through it.
  Uninstalling from a project-scoped install offers a choice — disable for yourself only (writes an
  override to that repo's `.claude/settings.local.json`) or uninstall for everyone (removes it from
  `.claude/settings.json`) — pick whichever matches how it was installed. `/plugin marketplace remove`
  also uninstalls every plugin that came from that marketplace, which for a repo that only ever added
  this one marketplace is just this plugin, but is worth knowing if you registered others from the same
  path. Removing the marketplace without the `uninstall` step first still works — Claude Code uninstalls
  the plugin along with it — but doing both explicitly is clearer about what changed.

Either way, the MCP server registration and the four `PreToolUse`/`PostToolUse` hooks travel *with* the
plugin — there is nothing repo-local to hand-remove for those; they stop firing the moment the plugin is
no longer loaded.

**Per-repo artifacts this plugin creates while in use**, safe to delete independently of the steps above:

- `.claude/dotnet-toolkit/cache/` in any repo you pointed it at — the SQLite knowledge store
  (symbol index, dev log, telemetry). Self-gitignored and always rebuildable from source; deleting it
  just forces a rebuild on the next session.
- If you ran `/dotnet-toolkit-init` in a repo: `.claude/rules/dotnet-toolkit-csharp.md`, the
  standards-file copies it wrote alongside it (`naming.md`, `styling.md`, `best-practices.md`,
  `antipatterns.md`, `architecture.md`, `api-design.md`, `error-handling.md`, `resource-management.md`,
  `performance.md`, `concurrency.md`, `security.md`, `testing.md`, `xml-documentation.md` — only the
  ones it actually wrote; it skips any that collided with a file the repo already had), and
  `.claude/dotnet-toolkit/backups/` (the pre-write backups it made). See
  `skills/dotnet-toolkit-init/SKILL.md`'s "Undoing this later" section for the exact list — that skill
  never touches the repo's own `CLAUDE.md`, so there is nothing to restore there.
- `.claude/dotnet-toolkit/config.json`, if you added one for solution overrides/`excludeGlobs`.

None of the above affects whether the plugin itself is installed — they're just what a repo accumulates
from using it, and deleting them is independent of (and doesn't require) uninstalling the plugin.

## How it works

- The MCP server starts instantly and builds two knowledge tiers in the background:
  a **syntax index** (every .cs file parsed with Roslyn, no MSBuild needed — lets
  `search_index`/`get_symbol` answer within seconds, marked `limitedBy: "index_only"`)
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
described below. `defaultFormat` sets the wire format a fresh server starts with —
`toon` (default), `compact` for minified JSON, or `json` for pretty-printed JSON — which
`set_output_format` can then change for the rest of the session.)

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
`.claude/rules/` (the coding standards + the always-loaded index rule),
`docs/` (`tool-reference.md`, `agent-reference.md`, `hook-reference.md`, `skill-reference.md`),
`.claude-plugin/` (plugin + marketplace manifests),
`.mcp.json` (MCP registration).
