# dotnet-toolkit

A [Claude Code plugin](https://code.claude.com/docs/en/plugins) for .NET repositories
that cuts token usage with a C# MCP server: Roslyn-powered code queries, an
always-fresh project structure index, and a structured development log — plus skills
that teach Claude to use them instead of reading whole files.

It is an **add-in** for existing projects: it never replaces a repo's own CLAUDE.md
or conventions.

## Features

| Domain | Tools | What it replaces |
|---|---|---|
| Code queries (Roslyn) | `find_symbol`, `get_symbol`, `find_references`, `find_implementations`, `diagnostics` | Read/Grep over .cs files, `dotnet build` output dumps |
| Structure index | `project_tree`, `list_folder`, `outline` | ls/Glob + reading files to learn a codebase |
| Development log | `devlog_add`, `devlog_search`, `devlog_get` | Re-discovering past decisions and rejected approaches |
| Server | `ping`, `workspace_status`, `reload_workspace` | — |

All read tools default to a compact pipe-delimited format (an `outline` is typically
~10–20% of the tokens of the file it describes); pass `format: "json"` for structured
output.

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

Then, from your .NET project:

```bash
claude --plugin-dir /path/to/dotnet-toolkit
```

or add it to a plugin marketplace for a permanent install.

## How it works

- The MCP server starts instantly and builds two knowledge tiers in the background:
  a **syntax index** (every .cs file parsed with Roslyn, no MSBuild needed — powers
  structure tools within seconds) and an **MSBuild workspace** (full semantic model —
  powers references/implementations/diagnostics once loaded; check
  `workspace_status`).
- The target repo root comes from `CLAUDE_PROJECT_DIR` (set by Claude Code). The
  solution is auto-discovered (`*.slnx` > `*.sln` > `*.csproj`, root + one level deep).
- Caches live in `.claude/dotnet-toolkit/cache/` in the target repo (self-gitignored).
- Change detection is mtime-polling based, so it works on WSL `/mnt/*` drives where
  inotify does not.
- Run `dotnet restore` in the target repo at least once (in the same OS you run
  Claude Code in) or semantic tools will report workspace load diagnostics.

## Per-repo configuration (optional)

`.claude/dotnet-toolkit/config.json` in the target repo:

```json
{
  "solution": "src/MyApp.slnx",
  "devlogDir": "docs/devlog",
  "excludeGlobs": ["**/Generated/**"],
  "defaultFormat": "compact"
}
```

`solution` resolves ambiguity when several solutions exist; `devlogDir` is where
weekly devlog markdown files are written (commit these — they're for humans too).

## Development log format

Entries are appended to `docs/devlog/<year>-W<week>.md` — human-readable markdown
with a hidden metadata comment per entry:

```markdown
## 2026-07-12 — Fixed decimal rounding in price calculation
<!-- devlog {"id":"20260712-1403-a3f2","ts":"...","status":"done","classes":["PriceCalculator"],"ensemble":"Ordering","tags":["bug"]} -->

**WHAT:** ...
**WHY:** ...
**OBSERVATIONS:** what was tried and why alternatives were rejected ...
**FIX:** ...
```

The search index is a rebuildable cache; hand-edited or merge-conflicted devlog files
are re-indexed automatically by mtime.

## Compact format legend

Tables: `label(shown/total) col1|col2` header, one pipe-separated row per line,
`…+N more (raise limit)` marker when truncated. Outlines: kind code + signature per
line, `// ` prefixes the XML doc summary. Kind codes: `C` class, `I` interface,
`S` struct, `R` record, `E` enum, `D` delegate, `M` method, `K` constructor,
`P` property, `F` field, `V` event.

## Development

```bash
dotnet build
dotnet test        # unit + MSBuildWorkspace integration tests
./scripts/build-plugin.sh
```

Layout: `src/DotnetToolkit.McpServer/` (server: `Tools/`, `Workspace/`, `Index/`,
`Devlog/`, `Output/`), `tests/` (xunit + `fixtures/SampleSolution`), `skills/`
(plugin skills), `.claude-plugin/plugin.json` + `.mcp.json` (plugin manifest and MCP
registration).
