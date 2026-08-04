# Installing dotnet-toolkit into a repo

The procedure behind `dotnet-toolkit-init`'s install path. The skill decides *which* path you are on
and owns the approval gate; this file is the steps once install is the answer.

**Nothing here is written until the user approves the plan.** That gate lives in the skill and is
absolute — these steps stage first, write second.

## What gets written

| File(s) | Content |
| --- | --- |
| `.claude/rules/tool-protocol.md` | **verbatim copy** of `${CLAUDE_PLUGIN_ROOT}/.claude/rules/tool-protocol.md`: tool table, the `dotnet-explore` delegation rule, write path. **Always-loaded** — no `paths:` frontmatter |
| `.claude/rules/csharp-standards.md` | **verbatim copy** of the plugin's file: the standards index, its per-file trigger conditions, and the write-time checklist. **Always-loaded** — no `paths:` frontmatter |
| `.claude/rules/{naming,styling,best-practices,antipatterns,architecture,api-design,error-handling,resource-management,performance,concurrency,security,testing,xml-documentation}.md` | verbatim copies of the plugin's standards files from `${CLAUDE_PLUGIN_ROOT}/.claude/rules/` (the list always matches `csharp-standards.md`'s index) — the repo owns editable copies |
| `.claude/settings.json` | the **read-only** MCP tools merged additively into `permissions.allow` (Step 3) |
| `.claude/dotnet-toolkit/install.json` | the manifest: plugin version, timestamp, and a hash per copied file |

Everything is copied rather than templated, so the plugin and every consuming repo run the *same*
rule text. There is no inline template to drift out of step with the plugin's own `.claude/rules/` —
a copy step cannot diverge the way a second authored copy did.

The standards are copied rather than referenced so the repo can edit them into its own convention
set. A repo that would rather track the plugin's versions can decline the copies and rely on
`${CLAUDE_PLUGIN_ROOT}/.claude/rules/` reads plus `.claude/dotnet-toolkit/<name>.md` overrides — say
this option exists when presenting the plan. **The two always-loaded rules are not optional**:
declining them leaves the repo with no protocol at all.

### What is deliberately *not* written

Naming these matters as much as the table above: assuming one of them needs copying, or needs
cleaning up at uninstall, is the recurring bug in this procedure.

| Not written | Why | At uninstall |
| --- | --- | --- |
| the MCP server, `hooks/`, `skills/`, `agents/` | they ship *active* with the plugin — the harness discovers them from the plugin manifest, so there is nothing repo-local to install | leave with the plugin; nothing to clean up |
| `docs/tools/*.md` and `docs/references/*.md` | referenced by `${CLAUDE_PLUGIN_ROOT}/docs/...` path from the skills (a rule cannot expand that variable, so it names the skill instead). Copies would go stale on every plugin update and would outlive the plugin as orphaned advice | leave with the plugin; the references die with it |
| `.claude/dotnet-toolkit/cache/` | created by the server at runtime, self-gitignored, always rebuildable | safe to leave; delete the directory for a fully clean removal |
| `CLAUDE.md` | the project's own file | never touched, so nothing to undo |

## Step 1 — Read what is already there

The target repo is the current working directory (or `CLAUDE_PROJECT_DIR` if set) — **not** the
plugin repo, unless the user is deliberately testing against it. Nothing here depends on the repo
having a `CLAUDE.md`.

Read `CLAUDE.md` if present (read-only, for Step 2's conflict check — it is never written to) and
list `.claude/rules/`. The project's own conventions take priority over anything this adds:

- Never reorder, reword, or remove anything already in `.claude/rules/`.
- **Name collisions**: if the repo already has a `.claude/rules/naming.md` (or any standards name from
  `csharp-standards.md`'s index), that file is the repo's own — do not overwrite it. Surface the
  collision and propose either skipping that copy or writing ours under a `dotnet-toolkit-` prefix,
  the user's call.
- If an existing rule already covers tool usage, code search, or "how to explore this codebase", read
  it carefully — Step 2 decides whether it complements or conflicts.

## Step 2 — Detect other plugins and existing guidance

- `.mcp.json` at the repo root — other MCP servers registered, and what they cover.
- `.claude/settings.json` / `.claude/settings.local.json` — enabled plugins, existing permissions.
- `.claude/rules/*.md` — always-loaded rules that would sit in context alongside ours, standards names
  already taken, and any existing security/testing standards our checklist would overlap.
- The CLAUDE.md text — any instruction of the shape "use X instead of grep/Read" for *any* language,
  or existing secrets/auth/testing guidance.

A quick scan, not a deep audit. These are config/markdown files, so plain `Read`/`ls` is correct —
the plugin's own tools are for `.cs`. Then decide:

- **No other code-intelligence plugin** → draft as-is.
- **Another plugin governs other languages, no overlap** → draft as-is; note the scoping in Step 3.
- **Genuine overlap** — another plugin's instructions already govern `.cs` search or edits, or a repo
  rule already carries C# standards → do not draft silently. Surface the conflict and ask how to
  resolve it (defer to the existing guidance, skip the overlapping copies, replace the older
  guidance) before going further.

## Step 3 — Stage the copies and the allowlist

**The rules.** Read both always-loaded rules from `${CLAUDE_PLUGIN_ROOT}/.claude/rules/` now, so the
approval step can show the exact text that will land. Neither carries `paths:` frontmatter — that is
what makes them always-loaded. Do not add one, and do not edit the text on the way through: a repo
that wants different wording edits its copy afterwards, or overrides per file via
`.claude/dotnet-toolkit/<name>.md`.

`tool-protocol.md` names the `dotnet-change` and `dotnet-code-query` skills for every procedure detail
rather than a `${CLAUDE_PLUGIN_ROOT}/docs/...` path — the harness does **not** expand that variable
inside a rule file, so a path there would land in the consumer as literal, dead text. Never add one
while copying.

If Step 2 found a scoped-but-resolvable overlap with another plugin, append one sentence to the
copied `tool-protocol.md` noting the boundary — e.g. "For non-.NET code, `<other plugin>` remains the
tool of record; this rule only governs `.cs`." One sentence; don't restate the other plugin's docs,
and don't otherwise diverge the copy.

**The allowlist.** A repo whose `tool-protocol.md` says "always use `search_index` instead of grep",
but where every such call raises a permission prompt, is worse than one with no rule: the session is
told to take a path the harness then interrupts.

Merge into `.claude/settings.json` (**not** `settings.local.json` — this is a team-shared convention,
same as the rules) under `permissions.allow`, one `mcp__plugin_dotnet-toolkit_dotnet__<tool>` entry
per **read-only** tool: `search_index`, `get_symbol`, `get_references`, `get_scope`, `get_call_slice`,
`get_call_hierarchy`, `get_type_hierarchy`, `get_project_graph`, `detect_circular_dependencies`,
`get_semantic_diff`, `search_log`, `workspace_status`, `reload_workspace`, `get_retrieval_metrics`,
`ping`, `set_output_format`.

**`validate_patch` and `rename_symbol` are deliberately absent.** They write to the user's source;
they must keep prompting. Adding them is a finding, not an improvement — say so if the user asks for
"all of them".

- **Additive only.** Read the existing file, append the missing entries, preserve every existing key,
  entry, and the file's formatting. Never replace the file, and never touch `permissions.deny`.
- If a `deny` entry would block one of these, do not silently override it — surface it for the user
  to decide.
- If `.claude/settings.json` does not exist, create it with just the `permissions.allow` block.
- Entries already present are left alone; report "already allowlisted" rather than duplicating.

## Step 4 — What the approval step must show

- The full content of both always-loaded rules and their paths.
- The exact `permissions.allow` entries to be added, and what `.claude/settings.json` looks like after
  the merge. This is a settings change, so it gets shown, not summarized.
- The standards files to be copied, per `csharp-standards.md`'s index (titles + one line each, not
  full contents — offer to show any in full), and the skip-copies alternative.
- One line on what Step 2 found, and how it was handled (collisions included).
- One line stating plainly that both rules are always-loaded (rules load independently of CLAUDE.md,
  alongside it, same priority — not above it), that the standards copies load only when read, and
  that the `PreToolUse` hooks are the actual enforcement. CLAUDE.md itself is untouched.

## Step 5 — Back up, then apply

1. For every file about to be written that already exists, copy it to
   `.claude/dotnet-toolkit/backups/<name>.<UTC timestamp>.bak` first. Keep backups after a successful
   apply. `.claude/settings.json` is included in this.
2. Copy the two always-loaded rules and write the approved standards copies. They're markdown, so
   `Write`/`Edit` is correct — `validate_patch` is for `.cs`, and the hooks don't touch these files.
3. Merge the allowlist into `.claude/settings.json`.
4. Write `.claude/dotnet-toolkit/install.json`:

   ```json
   {
     "pluginVersion": "<version from the plugin's .claude-plugin/plugin.json>",
     "installedAt": "<UTC ISO-8601>",
     "files": { ".claude/rules/tool-protocol.md": "<sha256>", "...": "..." }
   }
   ```

   One entry per file copied — not the settings file, whose content is the repo's. The hashes are what
   let a later run distinguish a plugin change from a local edit; without them a refresh can only ask
   about every difference. Compute with `sha256sum`.
5. Confirm back: what was written, what was backed up, and how to undo (`docs/install/uninstall.md`).
