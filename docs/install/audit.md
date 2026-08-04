# Auditing the install procedure

The mechanics behind `dotnet-toolkit-consistency`'s install-procedure audit. The skill states *when*
to run this and what counts as a finding; this file carries the inventory table, the drift catalog,
and the reachability scenarios. Read it when that step fires, not before.

**Ground truth is the plugin tree** — `skills/`, `agents/`, `hooks/`, `docs/`, `.claude/rules/`,
`.mcp.json`. `skills/dotnet-toolkit-init/SKILL.md` is a *claim* about that tree, checked against it,
never the reverse. The failure this catches is silent in both directions: an asset ships but never
reaches the consumer, or the uninstall instructions leave files behind that keep steering a repo that
no longer has the plugin.

## 1. Build the expected inventory

```bash
ls skills/ agents/ hooks/ docs/ docs/tools/ .claude/rules/ scripts/
```

Sort every entry into exactly one of four delivery mechanisms. **The sorting is the audit**: a
finding is any shipped file that lands in none of them, or that init handles as if it were in a
different one.

| # | Mechanism | What it covers | Reaches the consumer by | Uninstall |
| --- | --- | --- | --- | --- |
| 1 | **Ships active** | the MCP server (`.mcp.json` → `dotnet dist/DotnetToolkit.McpServer.dll`), `hooks/hooks.json` + the four `hook <name>` subcommands of that same binary, `skills/*/SKILL.md`, `agents/*.md` | installing the plugin — the harness discovers them from the plugin manifest | removing the plugin; **nothing repo-local** |
| 2 | **Must be copied** | `.claude/rules/tool-protocol.md` and `.claude/rules/csharp-standards.md` (both always-loaded, copied verbatim) plus the standards copies enumerated by `csharp-standards.md`'s index; the MCP permission allowlist merged into `.claude/settings.json`; the `.claude/dotnet-toolkit/install.json` manifest | `dotnet-toolkit-init` writing them into the repo | **explicit deletion** — init's "Undoing this later" list is the only thing that removes them |
| 3 | **Referenced by path** | `docs/tools/_index.md`, one `docs/tools/<tool>.md` per tool plus `server.md`, and `docs/references/*.md` | `${CLAUDE_PLUGIN_ROOT}/docs/...` paths named **in the skills** — never in a mechanism-2 rule, which cannot expand the variable | removing the plugin; the references die with it, so **nothing repo-local** |
| 4 | **Created at runtime** | `.claude/dotnet-toolkit/cache/` (self-gitignored), optional `.claude/dotnet-toolkit/config.json`, `.claude/dotnet-toolkit/backups/` | the server and init, at runtime | must be **named** in the uninstall section with an explicit disposition, even if that disposition is "safe to leave" |

Two rules the sorting enforces, both of which have been got wrong before:

- **Mechanism 3 is not mechanism 2.** `docs/` is referenced, never copied. Copies would go stale on
  every plugin update and would survive uninstall as orphaned advice. If init ever grows a step that
  copies `docs/` into a consumer, that is a finding.
- **Mechanism 1 needs no repo-local step, and claiming otherwise is also a finding.** Hooks, skills,
  and the agents travel with the plugin. Init must not copy them, and must not tell the user to clean
  them up.

## 2. Audit init's coverage

Read `skills/dotnet-toolkit-init/SKILL.md`. For every inventory entry, confirm it is accounted for in
the right place: the "What gets written" table lists exactly the mechanism-2 files and nothing from
1, 3, or 4; the apply step writes exactly those, backing up any that already exist; "Undoing this
later" deletes exactly the mechanism-2 files, names the mechanism-4 paths with a disposition, and
states that mechanisms 1 and 3 leave with the plugin.

Two path rules:

- **No mechanism-2 file contains `${CLAUDE_PLUGIN_ROOT}` at all.** The harness expands that variable
  in skill content and in hook commands, but **not in a rule file** — a rule is delivered literally,
  so any such path lands in the consumer as dead text pointing at a directory that does not exist. A
  rule reaches plugin content by naming the skill that opens it. Hard finding; it has shipped before
  and is invisible until a consumer follows the path.
- **Every `${CLAUDE_PLUGIN_ROOT}/...` path in a *skill*** resolves to a file in the inventory. A
  pointer to a renamed doc is the most common single defect here.

Specific drift to look for, in the order it usually appears:

1. **A standards file added to `.claude/rules/` but not to init's copy list.** Init's list must match
   `csharp-standards.md`'s index exactly — that index is the enumerator, init's table is a copy of
   it, and copies diverge. Check both directions.
2. **A tool added but not in `tool-protocol.md`'s tool table.** That rule embeds its own table for
   consumers, and it drifts independently of `docs/tools/_index.md`. The table is *"instead of X, use
   Y"* — so it carries every tool that replaces something a session would otherwise reach for, and
   legitimately omits the meta tools that replace nothing: `get_retrieval_metrics`,
   `set_output_format`, `ping`. Any **other** tool present in `_index.md`'s router but absent from
   the rule is a finding.
3. **A tool added but not in init's permission allowlist.** The allowlist covers the read-only tools
   only. `validate_patch` and `rename_symbol` are deliberately excluded — a write to the user's
   source should keep prompting — so their absence is correct, and their *presence* is the finding.
4. **A new `docs/` file nothing points at.** Most docs are reached through `_index.md`; a
   `docs/tools/<tool>.md` that its router doesn't list *is* a finding. The four `*-reference.md`
   files are **not** — they are maintainer-facing by design, reached from the plugin's own
   `README.md` and `CLAUDE.md`, and `references/agents.md` is deliberately read by neither agent.
   "Unreferenced from a consuming repo" is their intended state.
5. **An uninstall list shorter than the write list.** Diff them literally, file by file.

## 3. Out-of-the-box sufficiency

The question: **can a fresh session in a consuming repo do the work without reading anything that
only exists in this plugin's own repo?** Everything a consumer needs must be reachable from mechanism
1, 2, or 3 — never from this repo's `CLAUDE.md`, and never from a maintainer's memory.

Walk the scenarios and name, for each, the file the consumer would actually reach:

| Scenario | Must be reachable via |
| --- | --- |
| Find a symbol and its callers | `tool-protocol.md`'s tool table → the `dotnet-code-query` skill → `docs/tools/<tool>.md` |
| Change a method | `tool-protocol.md`'s write-path section → `dotnet-change` skill → `docs/tools/validate_patch.md` |
| Rename a symbol | `tool-protocol.md`'s write-path section → `dotnet-change` skill → `docs/tools/rename_symbol.md` |
| Review a change | `dotnet-review` skill → `dotnet-code-review` agent → the copied `.claude/rules/` standards |
| Map an unfamiliar change onto the code before editing | `tool-protocol.md`'s exploring section → the `dotnet-explore` agent |
| Know which standards to read before editing | the copied `csharp-standards.md` — the same index the plugin itself uses, not a consumer-only variant |
| Call a tool without a permission prompt on every use | the allowlist init merges into `.claude/settings.json` |

A scenario completable only by reading this repo's `CLAUDE.md` is a finding, and the fix is to move
that knowledge into a shipped file — one of the two copied rules, a skill, or a `docs/tools/<tool>.md`.

## Output format

Findings, concrete and file-anchored:

- **File:line** of the claim or the missing entry.
- **Mechanism** (1–4) it belongs to, and where it is currently handled.
- **Exact fix** — the row to add to init's table, the path to correct, the paragraph to replace with
  a pointer.

Close with a one-line verdict per dimension: *inventory covered / init procedure complete / uninstall
complete / out-of-the-box sufficient*. State a clean dimension as checked-and-clean rather than
omitting it — a silent dimension reads as a skipped one. If findings are fixable by re-running
`dotnet-toolkit-init` in a consuming repo (its refresh path), say so.
