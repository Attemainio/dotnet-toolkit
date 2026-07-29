---
name: dotnet-toolkit-install-check
description: Use when verifying that dotnet-toolkit is correctly and completely installed into a repo, that dotnet-toolkit-init's procedure actually covers everything the plugin ships, or that uninstalling leaves nothing behind — "did the init work", "check the dotnet-toolkit installation", "is this repo wired up correctly", "what does uninstalling leave behind", "audit the init skill". Builds the expected asset inventory from the plugin tree itself, then checks dotnet-toolkit-init's install and uninstall procedures against it and (in a consuming repo) the installed state on disk. Also enforces the always-loaded footprint budget: the consumer's CLAUDE.md is never touched, and the protocol rule stays a short declaration of when and how, not a copy of the workflow. Read-only — it reports and offers to re-run init, it never installs on its own.
---

# Auditing the installation

`dotnet-toolkit-init` is the only thing that makes an installed plugin actually *used* by a consuming
repo. It is prose, hand-maintained, and describes a file set that grows every time this plugin gains a
tool, a doc, a skill, or a standards file. When it falls behind, the failure is silent in both
directions: an asset ships but never reaches the consumer, or the uninstall instructions leave files
behind that keep steering a repo that no longer has the plugin.

This skill is the audit that catches both. **Ground truth is the plugin tree** — `skills/`, `agents/`,
`hooks/`, `docs/`, `.claude/rules/`, `.mcp.json`. `dotnet-toolkit-init/SKILL.md` is a *claim* about that
tree, checked against it, never the reverse.

**Read-only.** It reports findings and may offer to re-run `dotnet-toolkit-init` (which has its own
approval gate). It never writes into a consuming repo itself, and it never edits `.cs`.

## The two modes

| Mode | When | Ground truth | Checks |
| --- | --- | --- | --- |
| **procedure** | run inside this plugin repo, or asked to "audit the init skill" | the plugin tree | Steps 1, 2, 4, 5, 6 |
| **installed** | run inside a consuming repo that has been init'd | the plugin tree + that repo's `.claude/` | all steps |

If the current repo *is* this plugin repo, run **procedure** mode and say so — there is no installed
state to check, since this repo consumes the plugin through `.mcp.json` directly and deliberately keeps
its guidance in its own `CLAUDE.md`/`.claude/rules/` rather than through init.

## Step 1 — Build the expected inventory from the plugin

Enumerate what actually ships, from the plugin root (`${CLAUDE_PLUGIN_ROOT}`, or this repo's root in
procedure mode). These are markdown/JSON/shell files, so `ls`/`Read` are correct here — the MCP tools
are for `.cs`.

```bash
ls skills/ agents/ hooks/ docs/ docs/tools/ .claude/rules/ scripts/
```

Sort every entry into exactly one of four delivery mechanisms. **The sorting is the audit**: a finding
is any shipped file that lands in none of them, or that init handles as if it were in a different one.

| # | Mechanism | What it covers | Reaches the consumer by | Uninstall |
| --- | --- | --- | --- | --- |
| 1 | **Ships active** | the MCP server (`.mcp.json` → `scripts/run-server.sh` → `dist/`), `hooks/hooks.json` + every script it names, `skills/*/SKILL.md`, `agents/*.md` | installing the plugin — the harness discovers them from the plugin manifest | removing the plugin; **nothing repo-local** |
| 2 | **Must be copied** | `.claude/rules/dotnet-toolkit-csharp.md` (the protocol rule, written from init's template) and the standards copies enumerated by `.claude/rules/csharp-standards.md`'s index | `dotnet-toolkit-init` writing them into the repo's own `.claude/rules/` | **explicit deletion** — init's "Undoing this later" list is the only thing that removes them |
| 3 | **Referenced by path** | `docs/tools/_index.md`, one `docs/tools/<tool>.md` per tool plus `server.md`, and `docs/{agent,hook,skill,tool}-reference.md` | `${CLAUDE_PLUGIN_ROOT}/docs/...` paths named in mechanism-2 files and in the skills | removing the plugin; the references die with it, so **nothing repo-local** |
| 4 | **Created at runtime** | `.claude/dotnet-toolkit/cache/` (self-gitignored), optional `.claude/dotnet-toolkit/config.json`, `.claude/dotnet-toolkit/backups/` | the server and init, at runtime | must be **named** in the uninstall section with an explicit disposition, even if that disposition is "safe to leave" |

Two rules the sorting enforces, both of which have been got wrong before:

- **Mechanism 3 is not mechanism 2.** `docs/` is referenced, never copied. Copies would go stale on
  every plugin update and would survive uninstall as orphaned advice. If init ever grows a step that
  copies `docs/` into a consumer, that is a finding.
- **Mechanism 1 needs no repo-local step, and claiming otherwise is also a finding.** Hooks, skills,
  and the agent travel with the plugin. Init must not copy them, and must not tell the user to clean
  them up.

## Step 2 — Audit init's coverage of the inventory

Read `skills/dotnet-toolkit-init/SKILL.md`. For every entry in Step 1, confirm it is accounted for in
the right place:

- **"What gets written" table** — lists exactly the mechanism-2 files, and nothing from 1, 3, or 4.
- **Step 6 (apply)** — writes exactly those, backing up any that already exist.
- **"Undoing this later"** — deletes exactly the mechanism-2 files, names the mechanism-4 paths with a
  disposition, and states that mechanisms 1 and 3 leave with the plugin.
- **Every `${CLAUDE_PLUGIN_ROOT}/...` path** init writes into the consumer resolves to a file that
  exists in Step 1's listing. A pointer to a doc that was renamed is the most common single defect
  here, and it is invisible until a consumer follows it.

Specific drift to look for, in the order it usually appears:

1. **A standards file added to `.claude/rules/` but not to init's copy list.** Init's list must match
   `csharp-standards.md`'s index exactly — that index is the enumerator, init's table is a copy of it,
   and copies diverge. Check both directions.
2. **A tool added but not in the protocol rule's tool table.** The template embeds its own table for
   consumers, and it drifts independently of `docs/tools/_index.md`. The template's table is
   *"instead of X, use Y"* — so it carries every tool that replaces something a session would
   otherwise reach for, and legitimately omits the meta tools that replace nothing:
   `get_retrieval_metrics`, `set_output_format`, `ping`. Any **other** tool present in
   `_index.md`'s router but absent from the template is a finding.
3. **A new `docs/` file nothing in the template points at.** Not automatically a finding — most docs
   are reached through `_index.md` — but a *reference* doc (`agent-`, `hook-`, `skill-`,
   `tool-reference.md`) that no consumer-reachable file names is unreachable from a consuming repo.
4. **An uninstall list shorter than the write list.** Diff them literally, file by file.

## Step 3 — Audit the installed state (installed mode only)

In the consuming repo:

```bash
ls .claude/rules/ .claude/dotnet-toolkit/ 2>/dev/null
```

- Every mechanism-2 file init promised is present.
- The protocol rule has **no `paths:` frontmatter** — with one it would almost never load, since
  path-scoped rules fire only on built-in `Read` and `.cs` contact here goes through the MCP tools or
  is blocked by the guards.
- The standards copies **do** carry `paths: ["**/*.cs"]`, which is what keeps them out of the launch
  context; they are read on demand.
- The plugin is actually connected: `workspace_status` answers, and the solution it resolved is the
  one the repo expects. A rule installed against a plugin that is not loaded is worse than no rule —
  it tells the session to call tools that will not answer.
- If `.claude/dotnet-toolkit/config.json` exists, its `solution`/`excludeGlobs`/`defaultFormat` still
  describe this repo.

## Step 4 — The always-loaded footprint

This is the check with a hard number attached, because it is the cost every session in the consuming
repo pays regardless of task.

- **The consumer's `CLAUDE.md` must be untouched by us.** No dotnet-toolkit content, and specifically
  no `<!-- dotnet-toolkit:start -->`/`<!-- dotnet-toolkit:end -->` marker block — an artifact from a
  prior version of init. A consumer's CLAUDE.md is for that project's own architecture, commands, and
  conventions. If a marker block is found, the finding is "remove it and rely on the rule file",
  which init's Step 7 already knows how to do.
- **`.claude/rules/dotnet-toolkit-csharp.md` must stay a declaration, not a workflow.** Budget: **≤ 6
  KB (~1.6k tokens)**. It answers *when* to use the tools and *how to reach* the procedure — a tool
  table, a short write-path statement, the standards index, and the write-time checklist. It must not
  grow a full `validate_patch` walkthrough, per-tool argument documentation, or failure-mode
  narration: those live in `${CLAUDE_PLUGIN_ROOT}/docs/tools/<tool>.md` and the `dotnet-change` skill,
  both read on demand.

```bash
for f in .claude/rules/dotnet-toolkit-csharp.md CLAUDE.md; do
    [ -f "$f" ] && printf "%-46s %6d B  ~%.1fk tok\n" "$f" $(wc -c < "$f") $(echo "$(wc -c < "$f")/3800" | bc -l)
done
```

Report an overage with its size and which paragraphs to replace with a pointer. **Never fix an overage
by deleting guidance** — move it behind a pointer, or the consumer simply loses the rule.

## Step 5 — Uninstall completeness

Do this as a dry run: take init's "Undoing this later" list literally, and ask what would remain.

- Every mechanism-2 file deleted or restored from `.claude/dotnet-toolkit/backups/`.
- Mechanism 4 explicitly dispositioned. `cache/` is rebuildable and self-gitignored, so "safe to
  leave, delete the directory for a clean removal" is a fine disposition — an *unmentioned* directory
  is not.
- Nothing in the repo still instructs a session to call MCP tools that are gone: after the dry run, no
  remaining file names `search_index`, `get_symbol`, `validate_patch`, or `${CLAUDE_PLUGIN_ROOT}`.
  Check the repo's own CLAUDE.md here too — if it names the tools, the repo added that itself, which
  is fine, but the uninstall report must say so rather than leave a dangling instruction.
- The backups directory survives, and the report says where it is.

## Step 6 — Out-of-the-box sufficiency

The question this step answers: **can a fresh session in a consuming repo do the work without reading
anything that only exists in this plugin's own repo?** Everything a consumer needs must be reachable
from mechanism 1, 2, or 3 — never from this repo's `CLAUDE.md`, and never from a maintainer's memory.

Walk four scenarios and name, for each, the file the consumer would actually reach:

| Scenario | Must be reachable via |
| --- | --- |
| Find a symbol and its callers | protocol rule's tool table → `docs/tools/_index.md` → `docs/tools/<tool>.md` |
| Change a method | protocol rule's write-path section → `dotnet-change` skill → `docs/tools/validate_patch.md` |
| Review a change | `dotnet-review` skill → `dotnet-code-review` agent → the copied `.claude/rules/` standards |
| Know which standards to read before editing | the protocol rule's standards index (which is the consumer's replacement for `csharp-standards.md`) |

A scenario that can only be completed by reading this repo's `CLAUDE.md` is a finding, and the fix is
to move that knowledge into a shipped file — the protocol-rule template, a skill, or a
`docs/tools/<tool>.md`. `dotnet-toolkit-consistency` owns the converse check (that this repo's own
always-loaded instructions and the maintainer's accumulated knowledge are embedded somewhere
consumer-reachable); if this step finds a gap, say which of the two skills should fix it.

## Output format

Findings, concrete and file-anchored, grouped by step:

- **File:line** of the claim or the missing entry.
- **Mechanism** (1–4) it belongs to, and where it is currently handled.
- **Exact fix** — the row to add to init's table, the path to correct, the paragraph to replace with a
  pointer.

Close with a one-line verdict per mode dimension: *inventory covered / init procedure complete /
installed state correct / footprint within budget / uninstall complete / out-of-the-box sufficient*.
State a clean step as checked-and-clean rather than omitting it — a silent step reads as a skipped
one. If findings are fixable by re-running `dotnet-toolkit-init` (a refresh, per its Step 7), say so
and offer it; that skill will show its own plan and wait for approval.
