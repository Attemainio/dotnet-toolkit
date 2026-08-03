# dotnet-toolkit

**Claude reads your C# through a compiler, not a text search.**

A [Claude Code plugin](https://code.claude.com/docs/en/plugins) for .NET repositories. It gives Claude
a Roslyn semantic model of your solution, so it answers questions about your code the way your IDE
does — and validates every edit against a real compilation *before* it touches disk.

It is an **add-in**. It never replaces your repo's CLAUDE.md, your conventions, or your build.

<!-- TODO(visual): hero diagram — "Claude + grep" vs "Claude + semantic model" on the same question -->

---

## The problem

Ask Claude to find every caller of a method, and without help it reaches for `grep`. On C#, that is not
merely slower — **it is wrong**, and confidently so.

A text search cannot see interface dispatch, virtual dispatch, or delegate invocation. It counts
comments and string literals as real hits. It returns one fragment of a `partial class` with no signal
that the rest exists. And when output is truncated, it drops results *silently* — the answer looks
complete either way.

> On a 290-file repo, `grep -rn` for a method name found **3 of its 5 call sites** and returned **58
> comment matches** to hand-filter. `get_references` returned all 5, with nothing to discard, for a
> fraction of the tokens.

The failure mode that costs you real time isn't the wasted tokens. It's Claude refactoring against an
answer that was missing two call sites, and neither of you noticing until the build breaks — or until
it doesn't, and something fails at runtime instead.

## What a semantic model changes

The plugin runs a C# MCP server that keeps a Roslyn view of your solution, and ships skills that teach
Claude to reach for it instead of `grep`/`Read`/`Edit`.

| Instead of | Claude gets |
|---|---|
| Guessing from text | The compiler's own answer — dispatch, overrides, and implementations resolved properly |
| Reading whole files | The one symbol it asked for, reassembled across partial classes, at a fraction of the tokens |
| Editing blind, then `dotnet build` | An edit compiled in memory first, applied only if it holds — with the *reason* recorded |
| "It probably has no callers" | A count, or an explicit *unknown* — never a guess dressed as a zero |

That last row is the design rule throughout: **an absent field carries no information.** A confident `0`
reads as "nothing uses this" when the truth may be "that project never loaded." The server would rather
tell you it doesn't know.

<!-- TODO(visual): flow chart — get_symbol → validate_patch → dev log, with the ladder levels -->

## What you get

**17 tools**, grouped by the question they answer:

| You want to know | Tools |
|---|---|
| Where is this symbol, and what does it look like | `get_symbol`, `search_index` |
| What touches it — callers, implementations, call chains | `get_references`, `get_call_hierarchy`, `get_call_slice`, `get_type_hierarchy`, `get_scope` |
| How the projects fit together | `get_project_graph`, `detect_circular_dependencies` |
| What actually changed, and why it was done that way | `get_semantic_diff`, `search_log` |
| Is this edit safe | `validate_patch` |
| Rename this everywhere it is used | `rename_symbol` |
| Where the tokens went | `get_retrieval_metrics` |

Plus four housekeeping tools you rarely call by hand: `workspace_status`, `reload_workspace`,
`set_output_format`, `ping`.

**A validated write path.** `validate_patch` compiles your change in a forked in-memory solution, runs
the cheapest sufficient level of a validation ladder, and reports honestly whether that level was
*enough* for the kind of change you made. Nothing reaches disk unless it holds.

**Your `.editorconfig` decides what counts as broken.** The ladder grades exactly as `dotnet build`
does: `.editorconfig` severities and `TreatWarningsAsErrors` are honored, and the analyzers your
projects already reference (`CA*`, any NuGet analyzer package) run over the changed documents — a rule
you set to `error` blocks the patch, warnings and suggestions are reported and don't. So a change that
passes here doesn't fail your build for a rule the tool didn't know about.

**Clean is reported, not implied.** Every result carries a `checks` block saying which rungs ran and
over what, what the analyzers found at each severity, and — explicitly — what went unexamined. A silent
success can't tell you whether something was checked and found fine or never checked at all; this can.

**Renames the compiler computes, not you.** `rename_symbol` rewrites a symbol and every reference to it
from Roslyn's own reference graph — across projects, through interface and virtual dispatch — validates
the result through the same ladder, and records it in the same log. `applyOnSuccess: false` rehearses the
whole thing and tells you the blast radius without writing a byte.

**A development log that answers "why".** Every applied patch records its intent. `search_log` reads it
back — so the next session finds out an approach was already tried and rejected, instead of re-proposing
it.

**A read-only review agent.** `dotnet-code-review` reviews all quality aspects at once — correctness,
performance, concurrency, security, docs, testing — over one stated scope, against coding standards that
the writing agent reads too. One source of truth for both sides.

Full tool reference: **[`docs/tools/_index.md`](docs/tools/_index.md)** — the router, one row per
question, with a detail page per tool.

---

## Requirements

- **.NET 10 SDK** — the server targets `net10.0`, and the projects it analyzes need their own SDK
  present. On WSL/Ubuntu: `sudo apt-get install -y dotnet-sdk-10.0`.
- **Claude Code.**
- Run `dotnet restore` in your repo at least once, in the same OS you run Claude Code in.

## Install

Not in a public marketplace yet — the repo itself is the install source.

### 1. Clone and publish the server

```bash
git clone https://github.com/Attemainio/dotnet-toolkit dotnet-toolkit
cd dotnet-toolkit
./scripts/build-plugin.sh     # publishes the server to dist/ — required once, and after every update
```

### 2. Load the plugin

**Just trying it out?** Point Claude Code at the clone. Nothing is written to any config; closing the
session is the entire uninstall.

```bash
claude --plugin-dir /path/to/dotnet-toolkit
```

**Keeping it?** Register the clone as a local marketplace once, and it loads in every future session.

```
/plugin marketplace add /path/to/dotnet-toolkit
/plugin install dotnet-toolkit@dotnet-toolkit-local
```

### 3. Wire it into your repo

Installing makes the tools *available*. It does not make a fresh session *prefer* them — and a plugin
cannot auto-load coding standards, so this step is not optional if you want either.

```
/dotnet-toolkit-init
```

It shows you the exact plan and writes only after you approve, backing up anything it touches. It adds
one small always-loaded rule plus the coding standards to your `.claude/rules/`, and **never modifies
your CLAUDE.md**.

Then confirm the wiring took:

```
/dotnet-toolkit-install-check
```

> Upgrading from a pre-2026-07 install? The standards moved out of `docs/` and were renamed
> (`naming-conventions.md` → `.claude/rules/naming.md`, and so on). Re-running `/dotnet-toolkit-init`
> refreshes them; per-repo overrides under `.claude/dotnet-toolkit/` must use the new names.

### 4. Run the self-evaluation — and please report what it finds

**This step is what makes the plugin better, and it only works if you do it.**

```
/dotnet-toolkit-selfeval
```

It runs a fixed probe over every tool against *your* repo and measures each call's exact token cost. It
is read-only — it never changes your code, and every finding is about **this plugin**, never about your
codebase.

Your repo is the point. This plugin has been tuned against a handful of solutions, and every codebase
has structural shapes the tools have not met yet — deep partial classes, big overload sets, generated
code, `.slnx` vs `.sln`, projects that fail to load. Those are exactly the conditions under which a tool
quietly underperforms, and the evaluation is the only thing that surfaces them.

**Please paste the report into a new issue:**

### 👉 [Open an issue with your self-eval report](https://github.com/Attemainio/dotnet-toolkit/issues/new)

Include the report as-is, plus your solution's rough size (projects / `.cs` files) and anything unusual
about its layout. The report contains tool names, token counts, and call routes — no source code. Skim
it before posting if your repo is private.

<!-- TODO(visual): screenshot of a self-eval report -->

> **After any update**, re-run `./scripts/build-plugin.sh`. It republishes over `dist/`, which is what
> running servers execute — so it disconnects the MCP server in every open session. Run
> `/plugin reload-plugins` or restart to pick the rebuilt server up.

---

## Everyday use

Once wired up, you mostly just talk to Claude normally — the skills route the work. What changes is what
happens underneath:

| You ask | What happens |
|---|---|
| "Where is the fee calculation?" | `search_index` ranks every matching symbol in one call — terms are OR-ed, so several names cost one round trip |
| "What breaks if I change this signature?" | `get_references` walks the semantic model, including interface and virtual dispatch |
| "Rename this and fix the callers" | `rename_symbol` derives every call-site edit from the compiler's reference graph, then compiles the result in memory; nothing lands unless it holds |
| "Why is this written this way?" | `search_log` returns the recorded intent from when it was written |
| "Review this subsystem" | `dotnet-code-review` runs with fresh context against the shared standards |

Guard hooks block `Read`/`Edit` on compiled `.cs` files and tell Claude which tool to use instead, so it
cannot quietly fall back to text search mid-session.

## Configuration (optional)

`.claude/dotnet-toolkit/config.json` in your repo:

```json
{
  "solution": "src/MyApp.slnx",
  "excludeGlobs": ["**/Generated/**"]
}
```

`solution` resolves ambiguity when several exist — write it, then call `reload_workspace`.
`excludeGlobs` keeps generated code out of the index. The solution is auto-discovered otherwise
(`*.slnx` > `*.sln` > `*.csproj`); when several candidates exist the server refuses to guess and
`workspace_status` tells you how to choose.

## Troubleshooting

| Symptom | What it means |
|---|---|
| Semantic tools say the workspace is loading | The MSBuild model builds in the background so startup stays instant. `search_index`/`get_symbol` answer immediately from the syntax index; check progress with `workspace_status`. |
| `workspace_status` says **DEGRADED** | A project failed to load, and its reference edges are missing — results for it are incomplete, not wrong-but-complete. Anything that breaks `dotnet build` breaks this too, including a NuGet audit escalating an advisory to an error. |
| Results look stale | Change detection is mtime-polling (so it works on WSL `/mnt/*` where inotify does not). `reload_workspace` forces it. |

## Uninstall

- **Loaded with `--plugin-dir`**: stop passing the flag. Nothing was recorded anywhere.
- **Installed from the local marketplace**:

  ```
  /plugin uninstall dotnet-toolkit@dotnet-toolkit-local
  /plugin marketplace remove dotnet-toolkit-local
  /plugin reload-plugins
  ```

The MCP server and the guard hooks travel *with* the plugin — they stop the moment it unloads, with
nothing repo-local to clean up.

If you ran `/dotnet-toolkit-init`, it also wrote files into your `.claude/`. Those are yours to keep or
delete; `/dotnet-toolkit-install-check` will list exactly what a clean removal touches, and
`skills/dotnet-toolkit-init/SKILL.md`'s "Undoing this later" section has the literal list. The SQLite
cache in `.claude/dotnet-toolkit/cache/` is self-gitignored and always rebuildable — deleting it just
forces a rebuild.

---

## Learn more

| | |
|---|---|
| [`docs/tools/_index.md`](docs/tools/_index.md) | **Start here** — which tool answers which question, plus a page per tool |
| [`docs/skill-reference.md`](docs/skill-reference.md) | What each of the seven skills does |
| [`docs/agent-reference.md`](docs/agent-reference.md) | The review agent's design and token budget |
| [`docs/hook-reference.md`](docs/hook-reference.md) | The guard hooks and what they block |
| [`docs/architecture.md`](docs/architecture.md) | How the server is built: startup, the two knowledge tiers, subsystems, packaging |
| [`CLAUDE.md`](CLAUDE.md) | The operating contract for working on the plugin itself |

## Development

```bash
dotnet build
dotnet test                    # unit + MSBuildWorkspace integration tests
./scripts/build-plugin.sh
```

`TreatWarningsAsErrors` is set repo-wide, so a build with warnings fails. If more than one .NET 10 SDK
is installed, build with the same one `scripts/run-server.sh` picks — see `docs/architecture.md`'s
Environment section for the symptoms and the repair.

Layout: `src/DotnetToolkit.McpServer/` (the server — `Tools/` is the MCP surface), `tests/`, `skills/`,
`agents/`, `.claude/rules/` (coding standards), `docs/`, `.claude-plugin/` (manifests), `.mcp.json`.
`docs/architecture.md` explains how the pieces fit.

Issues and self-eval reports: **https://github.com/Attemainio/dotnet-toolkit/issues**
