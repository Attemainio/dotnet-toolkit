# Auditing the install procedure

The mechanics behind `dotnet-consistency`'s install-procedure audit. The skill states *when*
to run this and what counts as a finding; this file carries the inventory table, the drift catalog,
and the reachability scenarios. Read it when that step fires, not before.

**Ground truth is the plugin tree** — `skills/`, `agents/`, `hooks/`, `docs/`, `standards/`, `.claude/rules/`,
`.mcp.json`. `skills/dotnet-init/SKILL.md` is a *claim* about that tree, checked against it,
never the reverse. The failure this catches is silent in both directions: an asset ships but never
reaches the consumer, or the uninstall instructions leave files behind that keep steering a repo that
no longer has the plugin.

## 1. Build the expected inventory

```bash
ls skills/ agents/ hooks/ docs/ docs/tools/ docs/design/ docs/install/ standards/ .claude/rules/ scripts/
```

Sort every entry into exactly one of four delivery mechanisms. **The sorting is the audit**: a
finding is any shipped file that lands in none of them, or that init handles as if it were in a
different one.

| # | Mechanism | What it covers | Reaches the consumer by | Uninstall |
| --- | --- | --- | --- | --- |
| 1 | **Ships active** | the MCP server (`.mcp.json` → `dotnet dist/DotnetToolkit.McpServer.dll`), `hooks/hooks.json` + the five `hook <name>` subcommands of that same binary, `skills/*/SKILL.md`, `agents/*.md` | installing the plugin — the harness discovers them from the plugin manifest | removing the plugin; **nothing repo-local** |
| 2 | **Must be copied** | `.claude/rules/dotnet-index.md` (the one always-loaded rule, copied verbatim); the MCP permission allowlist merged into `.claude/settings.json`; the `.claude/dotnet-toolkit/install.json` manifest. **`standards/` is deliberately *not* here** — it is mechanism 3 | `dotnet-init` writing them into the repo | **explicit deletion** — `docs/install/uninstall.md`'s "Delete these" list is the only thing that removes them |
| 3 | **Referenced by path** | one `docs/tools/<tool>.md` per tool plus `server.md`, `docs/design/*.md`, `docs/install/*.md`, and **all 13 `standards/*.md`** | `${CLAUDE_PLUGIN_ROOT}/...` paths named **in the skills** — never in a mechanism-2 rule or an agent definition, neither of which can expand the variable. `agents/dotnet-code-review.md` is the one consumer that can't expand it either, which is why it resolves its own `pluginRoot` from its own `workspace_status` call rather than expecting a path handed to it | removing the plugin; the references die with it, so **nothing repo-local** |
| 4 | **Created at runtime** | `.claude/dotnet-toolkit/cache/` (self-gitignored), optional `.claude/dotnet-toolkit/config.json`, `.claude/dotnet-toolkit/backups/` | the server and init, at runtime | must be **named** in the uninstall section with an explicit disposition, even if that disposition is "safe to leave" |

Two rules the sorting enforces, both of which have been got wrong before:

- **Mechanism 3 is not mechanism 2.** `docs/` is referenced, never copied. Copies would go stale on
  every plugin update and would survive uninstall as orphaned advice. If init ever grows a step that
  copies `docs/` into a consumer, that is a finding.
- **Mechanism 1 needs no repo-local step, and claiming otherwise is also a finding.** Hooks, skills,
  and the agents travel with the plugin. Init must not copy them, and must not tell the user to clean
  them up.

## 2. Audit init's coverage

Read `skills/dotnet-init/SKILL.md` **and the three path files it routes to** — the skill is a
router, so its claims about the file set now live in `docs/install/install.md`, `verify.md`, and
`uninstall.md`, and each drifts on its own. For every inventory entry, confirm it is accounted for in
the right place: `install.md`'s "What gets written" table lists exactly the mechanism-2 files and
nothing from 1, 3, or 4; its apply step writes exactly those, backing up any that already exist;
`uninstall.md`'s "Delete these" removes exactly the mechanism-2 files, dispositions the mechanism-4
paths, and states that mechanisms 1 and 3 leave with the plugin; `verify.md`'s checklists name the
same files as `install.md`, and the skill's own router table points at all three.

Two path rules:

- **No mechanism-2 file contains `${CLAUDE_PLUGIN_ROOT}` at all.** The harness expands that variable
  in skill content and in hook commands, but **not in a rule file** — a rule is delivered literally,
  so any such path lands in the consumer as dead text pointing at a directory that does not exist. A
  rule reaches plugin content by naming the skill that opens it. Hard finding; it has shipped before
  and is invisible until a consumer follows the path.
- **Every `${CLAUDE_PLUGIN_ROOT}/...` path in a *skill*** resolves to a file in the inventory. A
  pointer to a renamed doc is the most common single defect here.

Specific drift to look for, in the order it usually appears:

1. **A standards file added to `standards/` but not to `standards/index.md`'s table.** That table is the
   enumerator for both readers; a file absent from it is never loaded by anyone. Init copies no
   standards at all now, so there is no copy list to keep in step — but the old failure returns the
   moment anyone re-adds one. The historical shape of the bug was init's list being a copy of
   it, and copies diverge. Check both directions.
2. **A tool added but named in no skill at all.** The skills carry the tool tables now; the copied
   `.claude/rules/dotnet-index.md` names no tool, so a tool no skill mentions is unreachable for a
   consumer. All **19** must be covered — retrieval and the four server/meta tools in `dotnet-read`,
   the two writers in `dotnet-write`, with `workspace_status` and `reload_workspace` legitimately in
   both, and `set_hook_guards` deliberately in `dotnet-performance` alone (it suspends the guards, so
   naming it in the skill every read task loads would advertise the off-switch to every session).
   The skills are served from the plugin and never copied, so unlike the old always-loaded table this
   one cannot go stale in a consumer's tree — but it can still fall behind `Tools/*.cs`.
3. **A tool added but not in init's permission allowlist.** The allowlist covers the read-only tools
   only. `validate_patch` and `rename_symbol` are deliberately excluded — a write to the user's
   source should keep prompting — so their absence is correct, and their *presence* is the finding.
4. **A new `docs/` file nothing points at.** Tool docs are reached through `dotnet-index.md`'s
   router; a `docs/tools/<tool>.md` that its router doesn't list *is* a finding. The
   `docs/design/*.md` files are **not** — they are maintainer-facing by design, reached from the
   plugin's own `README.md` and `CLAUDE.md`, and `design/agents.md` is deliberately read by neither
   agent. "Unreferenced from a consuming repo" is their intended state. `docs/install/*.md` are
   reached from `dotnet-init`'s router (and `audit.md` from `dotnet-consistency`).
5. **An uninstall list shorter than the write list.** Diff `uninstall.md`'s "Delete these" against
   `install.md`'s "What gets written", literally, file by file.

## 3. Out-of-the-box sufficiency

The question: **can a fresh session in a consuming repo do the work without reading anything that
only exists in this plugin's own repo?** Everything a consumer needs must be reachable from mechanism
1, 2, or 3 — never from this repo's `CLAUDE.md`, and never from a maintainer's memory.

Walk the scenarios and name, for each, the file the consumer would actually reach:

| Scenario | Must be reachable via |
| --- | --- |
| Find a symbol and its callers | `dotnet-index.md` routes to the `dotnet-read` skill → its tool table → the tool's own MCP schema → `docs/tools/<tool>.md` when the schema isn't enough |
| Change a method | `dotnet-index.md` routes to the `dotnet-write` skill → its loop and cheap-route table → `docs/tools/validate_patch.md` |
| Rename a symbol | `dotnet-index.md` routes to the `dotnet-write` skill → `docs/tools/rename_symbol.md` |
| Review a change | `dotnet-review` skill (resolves and injects `Standards root:`) → `dotnet-code-review` agent → `${CLAUDE_PLUGIN_ROOT}/standards/*.md` |
| Map an unfamiliar change onto the code before editing | `dotnet-index.md` routes to the `dotnet-explore` **skill**, which briefs and launches the `dotnet-explore` agent |
| Know which standards to read before editing | `dotnet-write` step 2 → `<pluginRoot>/standards/index.md` — served by the plugin, never copied, so it is the same table the plugin itself uses and cannot go stale in a consumer |
| Call a tool without a permission prompt on every use | the allowlist init merges into `.claude/settings.json` |

A scenario completable only by reading this repo's `CLAUDE.md` is a finding, and the fix is to move
that knowledge into a shipped file — the copied `dotnet-index.md`, a skill, a standard, or a
`docs/tools/<tool>.md`.

## Output format

Findings, concrete and file-anchored:

- **File:line** of the claim or the missing entry.
- **Mechanism** (1–4) it belongs to, and where it is currently handled.
- **Exact fix** — the row to add to init's table, the path to correct, the paragraph to replace with
  a pointer.

Close with a one-line verdict per dimension: *inventory covered / init procedure complete / uninstall
complete / out-of-the-box sufficient*. State a clean dimension as checked-and-clean rather than
omitting it — a silent dimension reads as a skipped one. If findings are fixable by re-running
`dotnet-init` in a consuming repo (its refresh path), say so.
