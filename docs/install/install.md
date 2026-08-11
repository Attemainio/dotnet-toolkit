# Installing dotnet-toolkit into a repo

The procedure behind `dotnet-init`'s install path. The skill decides *which* path you are on
and owns the approval gate; this file is the steps once install is the answer.

**Nothing here is written until the user approves the plan.** That gate lives in the skill and is
absolute — these steps stage first, write second.

## What gets written

**Three items. That is the entire footprint.**

| File(s) | Content |
| --- | --- |
| `.claude/rules/dotnet-index.md` | **verbatim copy** of `${CLAUDE_PLUGIN_ROOT}/.claude/rules/dotnet-index.md`: a pure router — which skill to invoke for reading, writing, exploring or reviewing C#, plus the rule that agents are launched by skills. It names **no MCP tool**; the tool tables live in the skills, which the plugin serves and never copies. **Always-loaded** — no `paths:` frontmatter, and it must be the only file in `.claude/rules/` without any |
| `.claude/settings.json` | the **read-only** MCP tools merged additively into `permissions.allow` (Step 3) |
| `.claude/dotnet-toolkit/install.json` | the manifest: plugin version, timestamp, and a hash per copied file |

It is copied rather than templated, so the plugin and every consuming repo run the *same* rule text.
There is no inline template to drift out of step with the plugin's own copy — a copy step cannot
diverge the way a second authored copy did. **`dotnet-index.md` is not optional**: declining it
leaves the repo with no protocol at all, and the tools stay available but unused.

**The coding standards are not copied.** They live at `${CLAUDE_PLUGIN_ROOT}/standards/*.md` and are
read from there on demand — by the main agent through `dotnet-write`'s pre-edit step, and by the
review agent through the `pluginRoot` it resolves itself from its own `workspace_status` call, since
`${CLAUDE_PLUGIN_ROOT}` does not expand inside an agent definition either. Consequences worth stating
when presenting the plan:

- A consuming repo is **always on the plugin's current standards**; they cannot go stale, and there
  is no 13-file refresh to run or conflict to resolve on a plugin update.
- **There is no per-repo override.** One copy of each standard exists, which is what keeps the
  writer and the reviewer from judging against different text. A repo that needs different rules
  writes its own guidance into its own `.claude/rules/`, outside this plugin — say so plainly rather
  than implying the plugin's standards are editable in place.
- They are deliberately **not** in `.claude/rules/`. A `paths:`-scoped rule fires only on the
  built-in `Read` tool, which the guards block for compiled `.cs`, while still firing on `.cs` files
  no project compiles — so as rules they would load unpredictably rather than on demand. See
  `docs/design/architecture.md`, "How rules load".

### What is deliberately *not* written

Naming these matters as much as the table above: assuming one of them needs copying, or needs
cleaning up at uninstall, is the recurring bug in this procedure.

| Not written | Why | At uninstall |
| --- | --- | --- |
| the MCP server, `hooks/`, `skills/`, `agents/` | they ship *active* with the plugin — the harness discovers them from the plugin manifest, so there is nothing repo-local to install | leave with the plugin; nothing to clean up |
| `docs/tools/*.md`, `docs/design/*.md`, `standards/*.md` | referenced by `${CLAUDE_PLUGIN_ROOT}/...` path from the skills (a rule cannot expand that variable, so it names the skill instead). Copies would go stale on every plugin update and would outlive the plugin as orphaned advice | leave with the plugin; the references die with it |
| `.claude/dotnet-toolkit/cache/` | created by the server at runtime, self-gitignored, always rebuildable | safe to leave; delete the directory for a fully clean removal |
| `CLAUDE.md` | the project's own file | never touched, so nothing to undo |

## Step 1 — Read what is already there

The target repo is the current working directory (or `CLAUDE_PROJECT_DIR` if set) — **not** the
plugin repo, unless the user is deliberately testing against it. Nothing here depends on the repo
having a `CLAUDE.md`.

Read `CLAUDE.md` if present (read-only, for Step 2's conflict check — it is never written to) and
list `.claude/rules/`. The project's own conventions take priority over anything this adds:

- Never reorder, reword, or remove anything already in `.claude/rules/`.
- **Pre-rename install**: a repo installed before the rule was renamed has `.claude/rules/index.md`
  — this same router under its old, generic name. When its hash matches a version this plugin
  shipped it is **our** previous copy, not the repo's file and not a collision, and it must be
  removed as part of the refresh rather than left beside the new one. Leaving it is the costliest
  mistake on this path: two unfrontmattered files in `.claude/rules/` are two always-loaded rules
  saying the same thing, paid by every session *and* every subagent, and the older of them routes to
  skill names that no longer exist. Handle it in the migration step below.
- **Name collision**: if the repo has a `.claude/rules/dotnet-index.md` whose hash matches nothing
  this plugin ever shipped, that file is the repo's own — do not overwrite it. Surface the collision
  and propose writing ours as `.claude/rules/dotnet-toolkit.md` instead, the user's call. Only one
  file is copied, so this is the only collision possible.
- **Legacy layout**: a repo installed before the standards moved has `.claude/rules/tool-protocol.md`,
  `.claude/rules/csharp-standards.md`, and up to 13 standards files there. Handle them per the
  migration step below — left in place they auto-load and contradict `dotnet-index.md`.
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

**The rule.** Read `${CLAUDE_PLUGIN_ROOT}/.claude/rules/dotnet-index.md` now, so the approval step
can show the exact text that will land. It carries no `paths:` frontmatter — that is what makes it
always-loaded. Do not add one, and do not edit the text on the way through: a repo that wants
different wording edits its copy afterwards, or overrides a standard via the plugin's `standards/`
directory.

`dotnet-index.md` names the `dotnet-read`, `dotnet-write`, `dotnet-explore` and `dotnet-review`
skills for every procedure detail rather than a `${CLAUDE_PLUGIN_ROOT}/...` path — the harness does
**not** expand that variable inside a rule file, so a path there would land in the consumer as
literal, dead text. Never add one while copying. That is also why the skills it names are bare skill
names and not their directory: the skills resolve the location.

If Step 2 found a scoped-but-resolvable overlap with another plugin, append one sentence to the
copied `dotnet-index.md` noting the boundary — e.g. "For non-.NET code, `<other plugin>` remains the
tool of record; this rule only governs `.cs`." One sentence; don't restate the other plugin's docs,
and don't otherwise diverge the copy.

**Legacy cleanup (hash-verified auto-clean).** If Step 1 found any of `index.md` (the pre-rename
router), `tool-protocol.md`, `csharp-standards.md`, or the 13 standards filenames in the repo's
`.claude/rules/`, stage their removal — left there they auto-load and contradict `dotnet-index.md`,
and the standards keep a `paths:` trigger that can fire on any `.cs` file no project compiles. For
each one:

- **Hash matches a version this plugin shipped** → remove it silently as part of the refresh. It was
  never edited, so there is nothing to lose and nothing to ask about.
- **Hash does not match** → the repo edited it. Show the diff and ask. Offer to carry the edits into
  a file of the repo's own choosing outside `.claude/rules/`, or to discard them. The plugin no
  longer reads a repo-local standard from anywhere, so an edited copy left in place would be dead
  text that still auto-loads — say that when asking. Never delete an edited file without an answer.

This is the same consent model the refresh path already uses for edited copies; it introduces no new
one. Count the removals in the approval summary.

**The allowlist.** A repo whose `dotnet-index.md` says "always use `search_index` instead of grep",
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

- The full content of `.claude/rules/dotnet-index.md` and where it lands.
- The exact `permissions.allow` entries to be added, and what `.claude/settings.json` looks like after
  the merge. This is a settings change, so it gets shown, not summarized.
- That the coding standards are **not** copied — they are read from the plugin, so they stay current
  — and that there is no per-repo override: the plugin's copy is the only copy.
- Any legacy files staged for removal, split into "unmodified, removing" and "edited, needs your
  call" with the diff for each of the latter.
- One line on what Step 2 found, and how it was handled (collisions included).
- One line stating plainly that `dotnet-index.md` is always-loaded (rules load independently of
  CLAUDE.md, alongside it, same priority — not above it) and is inherited by every subagent, that
  the standards load only when a skill reads them, and that the `PreToolUse` hooks are the actual
  enforcement. CLAUDE.md itself is untouched.

## Step 5 — Back up, then apply

1. For every file about to be written that already exists, copy it to
   `.claude/dotnet-toolkit/backups/<name>.<UTC timestamp>.bak` first. Keep backups after a successful
   apply. `.claude/settings.json` is included in this.
2. Copy `dotnet-index.md` into `.claude/rules/`, and delete the approved legacy files. It's
   markdown, so `Write`/`Edit` is correct — `validate_patch` is for `.cs`, and the hooks don't touch
   these files.
3. Merge the allowlist into `.claude/settings.json`.
4. Write `.claude/dotnet-toolkit/install.json`:

   ```json
   {
     "pluginVersion": "<version from the plugin's .claude-plugin/plugin.json>",
     "installedAt": "<UTC ISO-8601>",
     "files": { ".claude/rules/dotnet-index.md": "<sha256>" }
   }
   ```

   One entry per file copied — currently exactly one, since the standards are no longer copied — and
   not the settings file, whose content is the repo's. The hash is what lets a later run distinguish a
   plugin change from a local edit; without it a refresh can only ask about every difference. Compute
   with `sha256sum`. A manifest still listing standards paths is a pre-migration install; Step 3's
   legacy cleanup handles it, and the rewritten manifest must not carry those keys forward.
5. Confirm back: what was written, what was backed up, and how to undo (`docs/install/uninstall.md`).
