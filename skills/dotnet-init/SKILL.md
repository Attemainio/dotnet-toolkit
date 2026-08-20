---
name: dotnet-init
description: Use when the user asks to set up, install, wire up, verify, refresh, or remove dotnet-toolkit in a project — "set up dotnet-toolkit here", "/dotnet-init", "make Claude use the MCP tools in this repo", and equally "did the init work", "check the dotnet-toolkit installation", "is this repo wired up correctly", "are my copies out of date", "what does uninstalling leave behind". Copies the plugin's single always-loaded rule into .claude/rules/dotnet-index.md — a pure router naming no tools, mandating the dotnet-read, dotnet-write, dotnet-explore and dotnet-review skills over Grep/Read/Edit for C#, each of which carries its own tool set — merges the read-only MCP tools into .claude/settings.json's permission allowlist so they don't prompt on every call, records what it installed in .claude/dotnet-toolkit/install.json, and on a re-run verifies that state and refreshes what the plugin has changed since. Checks for conflicts with other installed plugins, backs up anything it touches, and only writes after the user approves the exact plan. Does not modify the repo's CLAUDE.md.
---

# Wiring dotnet-toolkit into a project

Installing this plugin (`/plugin install`, or `--plugin-dir`) makes its MCP tools *available*. It does
not make Claude *prefer* them — nothing tells a fresh session in a consuming repo that Grep and Read give
wrong answers on C#, or that `validate_patch` is the write path, or that the plugin ships coding
standards at all. A plugin cannot ship a `.claude/rules/` file the harness auto-loads; only a repo's own
`.claude/rules/` gets scanned, and a plugin has no manifest field to register one. This skill writes that
guidance into a target repo, additively, and only with explicit approval.

**This skill does not touch the repo's `CLAUDE.md`.** Per Claude Code's documentation
(`code.claude.com/docs/en/memory`), `.claude/rules/` is discovered and loaded independently of CLAUDE.md —
rules are not appended into that file at runtime, they are a separate context injection. The project's
own CLAUDE.md — its architecture, commands, and conventions, written and owned by the project — is left
alone entirely.

**Be honest with the user about the loading mechanics.** A `paths:`-scoped rule fires only when the
built-in `Read` touches a matching file — and here `.cs` contact goes through the MCP tools or is
blocked by the guards, so `paths: ["**/*.cs"]` would almost never load. Worse, it does not reliably
*suppress* either: the read guard deliberately allows `Read` on `.cs` files no project compiles, so
such a rule fires unpredictably rather than on demand. (Earlier versions of this skill shipped
standards as path-scoped rules for exactly this reason, and it was wrong both ways.)

So: **one file is copied**, `dotnet-index.md`, carrying no `paths:` and therefore always-loaded —
which is why it is kept short. It costs tokens in every session, and is **inherited by every
subagent with no opt-out**, so a parallel review pays it once per instance. **The coding standards
are not copied at all**: they live at `${CLAUDE_PLUGIN_ROOT}/standards/` and are read by explicit
path, so a consuming repo is always on the current versions and has nothing to refresh. Rules load
*alongside* CLAUDE.md at the same priority — never tell the user they "override" anything. Actual
enforcement is the plugin's `PreToolUse` hooks, which travel with the plugin and need no per-repo
setup.

**Do not skip the approval step under any circumstances**, even if the user's request sounded like a
green light to "just do it." These files change how every future session in that repo behaves; show the
exact content and wait for a yes.

## The three paths

This skill owns the whole lifecycle of dotnet-toolkit in a consuming repo. Decide which path you are
on, then read that one file — each is self-contained, and none of them is long.

| The ask | Path | Read |
| --- | --- | --- |
| "set up dotnet-toolkit here", first run | **install** | `${CLAUDE_PLUGIN_ROOT}/docs/install/install.md` |
| "did the init work", "is this wired up right", "are my copies stale", a re-run | **verify & refresh** | `${CLAUDE_PLUGIN_ROOT}/docs/install/verify.md` |
| "remove it", "what would uninstalling leave behind" | **uninstall** | `${CLAUDE_PLUGIN_ROOT}/docs/install/uninstall.md` |

A re-run is always verify-first: check what is installed and whether it is current *before* writing
anything. Never re-install blind over an existing installation.

The maintainer-side counterpart — auditing whether this skill's procedure still matches what the
plugin ships — is `dotnet-consistency`, via `docs/install/audit.md`. Not this skill's job.

## The two rules that hold on every path

**1. Do not skip the approval step under any circumstances**, even if the user's request sounded like
a green light to "just do it". These files change how every future session in that repo behaves, and
the allowlist changes what runs without asking. Show the exact content and wait for a yes. A generic
"go ahead and set it up" from earlier is not that yes if the plan has not been shown yet. Use
AskUserQuestion when there are real options to choose between (a coexistence resolution, a name
collision, copies vs. no-copies).

**2. Back up before overwriting.** Anything about to be written that already exists goes to
`.claude/dotnet-toolkit/backups/<name>.<UTC timestamp>.bak` first, and the backups stay after a
successful apply.

## The staleness decision — the one piece of logic worth keeping in context

`.claude/dotnet-toolkit/install.json` records the plugin version and a sha256 per copied file at
install time — `dotnet-index.md` and `.claude/dotnet-toolkit/.gitignore`, the two files init copies
verbatim. On any re-run, compare its `pluginVersion` against the installed plugin's
`.claude-plugin/plugin.json`, then hash each file. Three states, and they need different handling:

| Manifest hash vs. disk | Disk vs. current plugin | Meaning | Action |
| --- | --- | --- | --- |
| same | same | untouched and current | nothing |
| same | differs | the plugin changed it | refresh it; no need to ask per file |
| differs | — | **the repo edited it** — that is their convention now | show the diff and ask before replacing |
| same, but the manifest key names a path the plugin no longer ships | — | the plugin **renamed or removed** it | write the new path, then delete the old one — it is our copy, not theirs |

Without the hashes a refresh can only ask about every difference, which is how a repo's own edits get
silently reverted. This is why the manifest exists rather than a version stamp inside each file: the
copies are byte-identical to the plugin's originals, and that is what makes the comparison meaningful.

That last row is not hypothetical: `.claude/rules/index.md` became `.claude/rules/dotnet-index.md`,
so every repo installed before the rename has a manifest key pointing at a file the plugin no longer
writes. Treating it as "untouched and current" is what leaves two always-loaded rules in the repo —
the failure this row exists to prevent. `docs/install/install.md` carries the removal procedure.

## Where the target repo is

The current working directory (or `CLAUDE_PROJECT_DIR` if set) — **not** this plugin repo, unless the
user is deliberately testing the skill against it. Nothing here depends on the repo having a
`CLAUDE.md`; this skill never writes to one.
