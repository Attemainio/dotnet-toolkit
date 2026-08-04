---
name: dotnet-toolkit-init
description: Use when the user asks to set up, install, wire up, verify, refresh, or remove dotnet-toolkit in a project — "set up dotnet-toolkit here", "/dotnet-toolkit-init", "make Claude use the MCP tools in this repo", and equally "did the init work", "check the dotnet-toolkit installation", "is this repo wired up correctly", "are my copies out of date", "what does uninstalling leave behind". Copies the plugin's two always-loaded rules into .claude/rules/ (tool-protocol.md, which mandates the MCP tools over Grep/Read/find for C# and delegating exploration to the dotnet-explore agent, plus csharp-standards.md) and the coding-standards files they index, merges the read-only MCP tools into .claude/settings.json's permission allowlist so they don't prompt on every call, records what it installed in .claude/dotnet-toolkit/install.json, and on a re-run verifies that state and refreshes what the plugin has changed since. Checks for conflicts with other installed plugins, backs up anything it touches, and only writes after the user approves the exact plan. Does not modify the repo's CLAUDE.md.
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
blocked by the guards, so `paths: ["**/*.cs"]` would almost never load. (An earlier version of this
skill shipped exactly that, and its rule rarely fired.) Hence the split: the two protocol rules carry
**no `paths:`** and are always-loaded, which is why they are kept short — they cost tokens in every
session. The standards copies keep `paths: ["**/*.cs"]` for the opposite reason, to stay **out** of
the launch context until read on demand. Rules load *alongside* CLAUDE.md at the same priority —
never tell the user they "override" anything. Actual enforcement is the plugin's `PreToolUse` hooks
(`docs/hook-reference.md`), which travel with the plugin and need no per-repo setup.

**Do not skip the approval step under any circumstances**, even if the user's request sounded like a
green light to "just do it." These files change how every future session in that repo behaves; show the
exact content and wait for a yes.

## What gets written

| File(s) | Content |
| --- | --- |
| `.claude/rules/tool-protocol.md` | **verbatim copy** of `${CLAUDE_PLUGIN_ROOT}/.claude/rules/tool-protocol.md`: tool table, the `dotnet-explore` delegation rule, write path. **Always-loaded** — no `paths:` frontmatter |
| `.claude/rules/csharp-standards.md` | **verbatim copy** of the plugin's file: the standards index, its per-file trigger conditions, and the write-time checklist. **Always-loaded** — no `paths:` frontmatter |
| `.claude/rules/{naming,styling,best-practices,antipatterns,architecture,api-design,error-handling,resource-management,performance,concurrency,security,testing,xml-documentation}.md` | verbatim copies of the plugin's standards files from `${CLAUDE_PLUGIN_ROOT}/.claude/rules/` (the current list always matches `csharp-standards.md`'s index) — the repo owns editable copies; re-running this skill refreshes them (diffed, backed up) |
| `.claude/settings.json` | the **read-only** MCP tools merged additively into `permissions.allow` (Step 5). Without this every `search_index`/`get_symbol` call prompts, in a repo whose rules just told the session to always use them |
| `.claude/dotnet-toolkit/install.json` | the manifest: plugin version, timestamp, and a hash per copied file. This is what makes a later run able to tell *plugin changed* from *repo edited locally* (Step 8) |

Everything is copied rather than templated, so the plugin and every consuming repo run the *same*
rule text. There is no inline template in this skill to drift out of step with the plugin's own
`.claude/rules/` — a copy step cannot diverge the way a second authored copy did.

The standards are copied rather than referenced so the repo can edit them into its own convention set.
A repo that would rather track the plugin's versions can decline the copies in Step 6 and rely on
`${CLAUDE_PLUGIN_ROOT}/.claude/rules/` reads plus `.claude/dotnet-toolkit/<name>.md` overrides — say
this option exists when presenting the plan. **The two always-loaded rules are not optional**, though:
declining them leaves the repo with no protocol at all.

**The two always-loaded rules are the repo's entire always-loaded footprint, budgeted at ~6 KB each
(`tool-protocol.md`, `csharp-standards.md`).** They declare *when* to use the tools and
*where* the procedure lives; they are not a copy of the procedure. Per-tool arguments, the full
`validate_patch` walkthrough, and failure modes stay in `${CLAUDE_PLUGIN_ROOT}/docs/tools/<tool>.md`
and the `dotnet-change` skill, read on demand.

### What is deliberately *not* written into the repo

Naming these matters as much as the table above: assuming one of them needs copying, or needs
cleaning up at uninstall, is the recurring bug in this procedure.

| Not written | Why | At uninstall |
| --- | --- | --- |
| the MCP server, `hooks/`, `skills/`, `agents/` | they ship *active* with the plugin — the harness discovers them from the plugin manifest, so there is nothing repo-local to install | leave with the plugin; nothing to clean up |
| `docs/tools/*.md` and `docs/{agent,hook,skill,tool}-reference.md` | referenced by `${CLAUDE_PLUGIN_ROOT}/docs/...` path from the skills (a rule cannot expand that variable, so it names the skill instead). Copies would go stale on every plugin update and would outlive the plugin as orphaned advice | leave with the plugin; the references die with it |
| `.claude/dotnet-toolkit/cache/` | created by the server at runtime, self-gitignored, always rebuildable from source | safe to leave; delete the directory for a fully clean removal |
| `CLAUDE.md` | the project's own file (see above) | never touched, so nothing to undo |

`dotnet-toolkit-consistency` audits this whole inventory — install and uninstall — against the actual
plugin tree; run it in the plugin repo if you suspect this table has fallen behind what the plugin
ships. Step 8 below is the consumer-side counterpart: it checks the repo's own installed state.

## Step 1 — Locate the target repo

The target repo is the current working directory (or `CLAUDE_PROJECT_DIR` if set) — **not** this plugin
repo, unless the user is deliberately testing the skill against it. Nothing here depends on the repo
having a `CLAUDE.md` at all.

## Step 2 — Read what is already there

Read `CLAUDE.md` if present (read-only, for Step 3's conflict check — it is never written to) and list
`.claude/rules/`. The project's own conventions take priority over anything this skill adds. Concretely:

- Never reorder, reword, or remove anything already in `.claude/rules/`.
- **Name collisions**: if the repo already has a `.claude/rules/naming.md` (or any of the standards names, per `csharp-standards.md`'s index),
  that file is the repo's own — do not overwrite it. Surface the collision in Step 3 and propose either
  skipping that copy or writing ours under a `dotnet-toolkit-` prefix, the user's call.
- If an existing rule already covers tool usage, code search, or "how to explore this codebase," read it
  carefully — Step 3 decides whether it complements or conflicts.

## Step 3 — Detect other plugins and existing tool/standards guidance

- `.mcp.json` at the repo root — other MCP servers registered, and what they cover.
- `.claude/settings.json` / `.claude/settings.local.json` — enabled plugins, existing permissions.
- `.claude/rules/*.md` — always-loaded rules that would sit in context alongside ours, any of the
  standards names already taken, and any existing security/testing coding-standards rule our checklist
  would overlap.
- The CLAUDE.md text — any instruction of the shape "use X instead of grep/Read" for *any* language, or
  any existing secrets/auth/testing standards guidance.

This is a quick scan, not a deep audit. These are config/markdown files, so plain `Read`/`ls` is correct
here — the plugin's own tools are for `.cs`.

Then decide:

- **No other code-intelligence plugin** → draft as-is.
- **Another plugin governs other languages, no overlap** → draft as-is; note the scoping in Step 4's draft.
- **Genuine overlap** — another plugin's instructions already govern `.cs` search or edits, or a repo
  rule already carries C# standards → do not draft silently. Surface the conflict and ask how to resolve
  it (defer to the existing guidance, skip the overlapping copies, replace the older guidance) before
  going further.

## Step 4 — Stage the rule copies

The two always-loaded rules are **copied verbatim**, not authored here:

| Source (plugin) | Destination (target repo) |
| --- | --- |
| `${CLAUDE_PLUGIN_ROOT}/.claude/rules/tool-protocol.md` | `.claude/rules/tool-protocol.md` |
| `${CLAUDE_PLUGIN_ROOT}/.claude/rules/csharp-standards.md` | `.claude/rules/csharp-standards.md` |

Read both from the plugin now, so Step 6 can show the user the exact text that will land. Neither
carries `paths:` frontmatter — that is what makes them always-loaded; see the loading-mechanics
paragraph above. Do not add one, and do not edit the text on the way through: a repo that wants
different wording edits its copy afterwards, or overrides per file via
`.claude/dotnet-toolkit/<name>.md`.

`tool-protocol.md` names the `dotnet-change` and `dotnet-code-query` skills for every procedure
detail rather than a `${CLAUDE_PLUGIN_ROOT}/docs/...` path — the harness does **not** expand that
variable inside a rule file, so a path there would land in the consumer as literal, dead text. Never
add one while copying. Those skills exist only while the plugin is installed, which is deliberate —
see "What is deliberately *not* written".

If Step 3 found a scoped-but-resolvable overlap with another plugin, append one sentence to the
copied `tool-protocol.md` noting the boundary — e.g. "For non-.NET code, `<other plugin>` remains the
tool of record; this rule only governs `.cs`." One sentence; don't restate the other plugin's docs,
and don't otherwise diverge the copy.

## Step 5 — Stage the permission allowlist

A repo whose `tool-protocol.md` says "always use `search_index` instead of grep", but where every
such call raises a permission prompt, is worse than one with no rule: the session is told to take a
path the harness then interrupts. So the install includes the allowlist.

Merge into `.claude/settings.json` (**not** `settings.local.json` — this is a team-shared convention,
same as the rules) under `permissions.allow`, one entry per **read-only** tool:

```
mcp__plugin_dotnet-toolkit_dotnet__<tool>
```

for `search_index`, `get_symbol`, `get_references`, `get_scope`, `get_call_slice`,
`get_call_hierarchy`, `get_type_hierarchy`, `get_project_graph`, `detect_circular_dependencies`,
`get_semantic_diff`, `search_log`, `workspace_status`, `reload_workspace`, `get_retrieval_metrics`,
`ping`, `set_output_format`.

**`validate_patch` and `rename_symbol` are deliberately absent.** They write to the user's source;
they must keep prompting. Adding them is a finding, not an improvement — say so if the user asks for
"all of them".

Merging rules:

- **Additive only.** Read the existing file, append the missing entries, preserve every existing key,
  entry, and the file's formatting. Never replace the file, and never touch `permissions.deny`.
- If a `deny` entry would block one of these, do not silently override it — surface it in Step 6 and
  let the user decide.
- If `.claude/settings.json` does not exist, create it with just the `permissions.allow` block.
- Entries already present are left alone; report "already allowlisted" rather than duplicating.

## Step 6 — Present the plan, then wait

Show the user, in chat (not applied yet):
- The full content of both always-loaded rules (`tool-protocol.md`, `csharp-standards.md`) and their paths.
- The exact `permissions.allow` entries to be added, and what `.claude/settings.json` will look like
  after the merge. This is a settings change, so it gets shown, not summarized.
- The list of standards files to be copied, per `csharp-standards.md`'s index (titles + one line each, not full contents — offer
  to show any in full), and the skip-copies alternative from "What gets written".
- One line on what Step 3 found, and how it was handled (collisions included).
- One line stating plainly that both rules are always-loaded (rules load independently of
  CLAUDE.md, alongside it, same priority — not above it), that the standards copies load only when
  read, and that the `PreToolUse` hooks are the actual enforcement. CLAUDE.md itself is untouched.

Then ask directly whether to proceed. Use AskUserQuestion if there are real options to choose between
(a Step 3 coexistence resolution, a name collision, copies vs. no-copies). **Do not write until the
user has said yes.** A generic "go ahead and set it up" from earlier is not that yes if the plan has
not been shown yet.

## Step 7 — Back up, then apply

1. For every file about to be written that already exists (a re-run, or a collision the user resolved
   as "replace"), copy it to `.claude/dotnet-toolkit/backups/<name>.<UTC timestamp>.bak` first.
   Keep backups after a successful apply. `.claude/settings.json` is included in this.
2. Copy the two always-loaded rules and write the approved standards copies. They're markdown, so
   `Write`/`Edit` is correct — `validate_patch` is for `.cs`, and the hooks don't touch these files.
3. Merge the Step 5 allowlist into `.claude/settings.json`.
4. Write `.claude/dotnet-toolkit/install.json`:

   ```json
   {
     "pluginVersion": "<version from the plugin's .claude-plugin/plugin.json>",
     "installedAt": "<UTC ISO-8601>",
     "files": { ".claude/rules/tool-protocol.md": "<sha256>", "...": "..." }
   }
   ```

   One entry per file this skill copied — not the settings file, whose content is the repo's. The
   hashes are what let Step 8 distinguish a plugin change from a local edit; without them a refresh
   can only ask about every difference. Compute with `sha256sum`.
5. Confirm back: what was written, what was backed up, and how to undo.

## Step 8 — Verify and refresh (a re-run, or "is this repo wired up correctly?")

This is also the whole answer to *did the init work* and *are my copies stale* — run it on its own
when asked, without re-installing. Report each check as checked-and-clean rather than omitting it; a
silent check reads as a skipped one.

**Is it current?** Compare `install.json`'s `pluginVersion` against the installed plugin's
`.claude-plugin/plugin.json`. Then hash each copied file and sort it into one of three states, which
is the whole point of the manifest:

| Manifest hash vs. disk | Disk vs. current plugin | Meaning | Action |
| --- | --- | --- | --- |
| same | same | untouched and current | nothing |
| same | differs | the plugin changed it | refresh it; no need to ask per file |
| differs | — | **the repo edited it** — that is their convention now | show the diff and ask before replacing |

A refresh replaces content in place: same paths, no new files, backed up per Step 7, and
`install.json` rewritten afterwards. Show the diffs in Step 6 in place of full text.

Then run the three checklists in **`${CLAUDE_PLUGIN_ROOT}/docs/install-verify.md`** — read it now,
it is short and the checks have exact wording that matters:

1. **Installed correctly** — every promised file present, frontmatter right on both classes of rule,
   the allowlist covering the read-only tools and no writer, and `workspace_status` actually
   answering. A rule installed against a plugin that is not loaded is worse than no rule.
2. **Footprint within budget** — ≤ 6 KB for each always-loaded rule, and no dotnet-toolkit content in
   the consumer's `CLAUDE.md`. An overage is a finding against the *plugin*, since the copies are
   verbatim; `dotnet-toolkit-consistency` owns that fix.
3. **Uninstall completeness** — a dry run of "Undoing this later", naming what would remain.

## Undoing this later

- **Remove everything**: delete `.claude/rules/tool-protocol.md`, `.claude/rules/csharp-standards.md`,
  and the standards copies (or restore from `.claude/dotnet-toolkit/backups/`); remove the
  `mcp__plugin_dotnet-toolkit_dotnet__*` entries from `.claude/settings.json`'s `permissions.allow`,
  leaving the rest of that file alone; delete `.claude/dotnet-toolkit/install.json`. That is the
  complete list of what this skill ever writes outside `.claude/dotnet-toolkit/`.
- **Runtime leftovers**: `.claude/dotnet-toolkit/cache/` is the server's rebuildable SQLite store and
  is self-gitignored — safe to leave, delete the directory for a fully clean removal.
  `.claude/dotnet-toolkit/config.json`, if the repo wrote one, is the repo's own; leave it.
  `.claude/dotnet-toolkit/backups/` is kept deliberately — say where it is when reporting.
- **If an old CLAUDE.md marker block exists from a prior version of this skill**: delete everything from
  `<!-- dotnet-toolkit:start -->` to `<!-- dotnet-toolkit:end -->` inclusive, or restore the newest
  backup over `CLAUDE.md`. Current versions never write there, so on a fresh install there is nothing
  to check.
- **Everything else leaves with the plugin.** The hooks, skills, agents, MCP server, and every
  `docs/` file those skills open are gone the moment the plugin is uninstalled —
  no repo-local cleanup, and nothing left instructing a session to call tools that no longer exist.

Confirm the last point rather than asserting it: after the deletions, nothing remaining in the repo
should name `search_index`/`get_symbol`/`validate_patch` — except whatever the repo wrote itself,
which is theirs to keep. Step 8 does exactly this as a dry run.

Mention all of these when reporting Step 7's result.
