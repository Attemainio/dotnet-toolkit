---
name: dotnet-init
description: Use when the user asks to set up, install, wire up, verify, refresh, or remove dotnet-toolkit for Claude Code or Codex — "set up dotnet-toolkit here", "/dotnet-init", "make my agent use the MCP tools in this repo", and equally "did the init work", "check the dotnet-toolkit installation", "is this repo wired up correctly", "are my copies out of date", "what does uninstalling leave behind". Routes Codex to its local marketplace plus user MCP registration, and Claude Code to the repo-local rule and permission wiring. Checks for conflicts, backs up repo files before changing them, and only writes after the user approves the exact plan. Does not modify the repo's CLAUDE.md or AGENTS.md.
---

# Wiring dotnet-toolkit into a project

## Choose the host first

**Codex:** use the Codex section of `<pluginRoot>/docs/installation.md`, then stop; the Claude-specific
procedure below does not apply. Register the existing local marketplace and plugin, and register the
published server separately with `codex mcp add dotnet -- dotnet <absolute-clone-path>/dist/DotnetToolkit.McpServer.dll`.
Obtain the persistent clone path from the local marketplace entry (`codex plugin list --json`), not
from the versioned plugin cache. Leave MCP `cwd` unset, so the server starts in the active workspace.
Do not copy `.claude/rules/`, edit `.claude/settings.json`, or create a Codex-only copy of an agent
brief. If a `dotnet` MCP entry already exists, show it and ask before replacing it.

**Establish the executor before constructing any path.** The agent host's OS and the OS its tool
processes run in are different questions, and only the second one decides how a path is spelled. Run
the doctor below first and read its `environment:` section — it names the executor, the topology, and
the effective `CODEX_HOME`. The case that breaks silently is a Windows desktop app executing in WSL2:
`CODEX_HOME` is the Windows-owned profile reached through `/mnt/c/...`, any `~/.codex` on the Linux
side is *not* in effect, and a registered `C:\...` path is unopenable by the Linux process that has
to launch it — while every listing command still reports the server enabled. Register from a shell
running where the tools will run, and give the marketplace `source` the same spelling as the DLL path.

**Verify by launching, never by listing.** `codex plugin list --json` and `codex mcp list --json`
report what configuration says; they start no process, so they cannot detect a broken registration —
which is exactly how an install reaches a user looking healthy with zero callable tools. The check
that can fail is:

```
dotnet <absolute-clone-path>/dist/DotnetToolkit.McpServer.dll doctor --repo <target-repo>
```

It reports executor and topology, which config file is in effect, whether each stored path is
openable *from this process* (naming the corrected spelling when it is not), the marketplace source,
the SDK the workspace will load with and why, whether `bubblewrap` is present for the WSL sandbox,
and then launches the configured command for a real handshake — process started, protocol
initialized, tools discovered, ping, workspace root/index/projects. It exits nonzero on any failure.
Report its output rather than a claim that the install "looks right"; a live `/mcp` check in the UI
is a useful confirmation but is a manual observation, not the verification.

Codex has two independent controls: `/plugins` enables or disables the skills/hooks bundle;
`[mcp_servers.dotnet] enabled = false` disables the server. Removing one does not remove the other.

**Claude Code:** continue with the procedure below. It owns the repo-local `.claude/` wiring that
Claude Code needs because it does not discover Codex's `AGENTS.md` or user MCP registration.

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
