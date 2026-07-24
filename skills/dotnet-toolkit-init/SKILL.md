---
name: dotnet-toolkit-init
description: Use when the user asks to set up, install, wire up, or apply dotnet-toolkit's tool-usage and coding-standards rules into a project — e.g. "set up dotnet-toolkit here", "/dotnet-toolkit-init", "make Claude use the MCP tools in this repo". Writes an always-loaded protocol rule into .claude/rules/ that mandates the MCP tools over Grep/Read/find for C#, and copies the plugin's nine coding-standards files alongside it, checks for conflicts with other installed plugins, backs up anything it touches, and only writes after the user approves the exact plan. Does not modify the repo's CLAUDE.md.
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

**Be honest with the user about the loading mechanics.** Path-scoped rules (`paths:` frontmatter) only
fire when the built-in `Read` tool touches a matching file — and with this plugin installed, `.cs`
contact goes through the MCP tools or is blocked by the read/edit guards, so a `paths: ["**/*.cs"]` rule
would almost never load (verified against the official docs, 2026-07; an earlier version of this skill
wrote exactly that and its rule rarely fired). The protocol rule below therefore ships **always-loaded**
(no `paths:`) and is kept short because it costs its tokens in every session. It loads *alongside*
CLAUDE.md with the same priority — never tell the user it "overrides" anything. The standards copies keep
`paths: ["**/*.cs"]` for the opposite reason: it keeps them **out** of the launch context; they are read
on demand via the protocol rule's index and the `dotnet-change` skill's pre-edit step. The actual
enforcement is the plugin's `PreToolUse` hooks (see `docs/hook-reference.md`), which travel with the
plugin and need no per-repo setup.

**Do not skip the approval step under any circumstances**, even if the user's request sounded like a
green light to "just do it." These files change how every future session in that repo behaves; show the
exact content and wait for a yes.

## What gets written

| File(s) | Content |
| --- | --- |
| `.claude/rules/dotnet-toolkit-csharp.md` | the protocol rule (template below): tool table, write path, standards index, write-time checklist. **Always-loaded** — no `paths:` frontmatter. |
| `.claude/rules/{naming,styling,best-practices,antipatterns,performance,concurrency,security,testing,xml-documentation}.md` | verbatim copies of the plugin's nine standards files from `${CLAUDE_PLUGIN_ROOT}/.claude/rules/` — the repo owns editable copies; re-running this skill refreshes them (diffed, backed up) |

The standards are copied rather than referenced so the repo can edit them into its own convention set.
A repo that would rather track the plugin's versions can decline the copies in Step 5 and rely on
`${CLAUDE_PLUGIN_ROOT}/.claude/rules/` reads plus `.claude/dotnet-toolkit/<name>.md` overrides — say
this option exists when presenting the plan.

## Step 1 — Locate the target repo

The target repo is the current working directory (or `CLAUDE_PROJECT_DIR` if set) — **not** this plugin
repo, unless the user is deliberately testing the skill against it. Nothing here depends on the repo
having a `CLAUDE.md` at all.

## Step 2 — Read what is already there

Read `CLAUDE.md` if present (read-only, for Step 3's conflict check — it is never written to) and list
`.claude/rules/`. The project's own conventions take priority over anything this skill adds. Concretely:

- Never reorder, reword, or remove anything already in `.claude/rules/`.
- **Name collisions**: if the repo already has a `.claude/rules/naming.md` (or any of the nine names),
  that file is the repo's own — do not overwrite it. Surface the collision in Step 3 and propose either
  skipping that copy or writing ours under a `dotnet-toolkit-` prefix, the user's call.
- If an existing rule already covers tool usage, code search, or "how to explore this codebase," read it
  carefully — Step 3 decides whether it complements or conflicts.

## Step 3 — Detect other plugins and existing tool/standards guidance

- `.mcp.json` at the repo root — other MCP servers registered, and what they cover.
- `.claude/settings.json` / `.claude/settings.local.json` — enabled plugins, existing permissions.
- `.claude/rules/*.md` — always-loaded rules that would sit in context alongside ours, any of the nine
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

## Step 4 — Draft the protocol rule

Write `.claude/rules/dotnet-toolkit-csharp.md` with this content. Note there is deliberately **no
`paths:` frontmatter** — see the loading-mechanics paragraph above; adding one would make the rule
almost never load.

```markdown
# C# in this repo: dotnet-toolkit MCP tools and coding standards

This repo has the dotnet-toolkit plugin installed — a Roslyn-powered MCP server. Its tools are the
default path for C#, not Grep, Glob, `find`, `ls`, `cat`, bare `Read`, or `Edit`/`Write`.

Grep and Read give **wrong answers** on C#, not merely slower ones: text search cannot see interface,
virtual, or delegate dispatch, counts comment and string matches as real hits, silently under-reports
when output is truncated, and returns one fragment of a partial class with no signal the rest exists.

| Instead of | Use |
| --- | --- |
| `grep`/Grep for a type or member name | `search_index` (all terms in ONE call — they are OR-ed and ranked) |
| `Read` on a `.cs` file | `get_symbol` (whole symbol across partials; `include` picks the fields) |
| `grep` for callers or implementors | `get_references` (Roslyn semantic model) |
| `find`/`ls`/Glob to map a subsystem | `get_scope` |
| Tracing a call chain by hand | `get_call_slice` |
| Who eventually calls/is called by a symbol, several levels deep | `get_call_hierarchy` |
| A type's full base chain, interfaces, and derived/implementing types | `get_type_hierarchy` |
| Opening every `.csproj` to trace project references by hand | `get_project_graph` |
| Manually tracing project references looking for a cycle | `detect_circular_dependencies` |
| `git diff` to judge a change | `get_semantic_diff` |
| Guessing why code looks the way it does | `search_log` |
| Wondering whether the index/workspace is warm | `workspace_status`, then `reload_workspace` if stale |
| `Edit`/`Write` then `dotnet build` | `validate_patch` |

`PreToolUse` hooks enforce all three sides: `Read` on a compiled `.cs` file, a `Bash` command reading
the same bytes (`cat`/`grep`/`sed`/etc.), and `Edit`/`Write` on an existing one are all blocked —
reaching for any of them costs a round trip and returns nothing. The hooks travel with the plugin;
nothing repo-local to maintain.

## Writing

`validate_patch` is the write path and the **only** writer to the development log. An edit that
bypasses it is a change whose reasoning is gone when the conversation ends; `search_log` cannot
recover it and the next session re-derives or silently contradicts it.

1. `get_symbol` — keep `contentVersion` and the `declarationSites` line span.
2. `validate_patch` with `baseVersions: {symbolId: contentVersion}` and line-span `edits`,
   `applyOnSuccess: false` — read the ladder verdict without touching disk.
3. Re-send with `applyOnSuccess: true` and an `intent` in user terms once `isSufficient: true`.

"Too large or too interleaved to decompose" is not a reason to fall back to `Edit`. Split it into
more `validate_patch` calls, one per touched symbol, sharing one `intent`. Only new-file creation is
legitimately outside this: `Write` the file, then change it through `validate_patch`.

## Coding standards — read before writing C#

The standards live beside this rule in `.claude/rules/` and are **read on demand, not auto-loaded**
(their `paths:` frontmatter only keeps them out of the launch context). Before the first C# edit of a
session, read the relevant ones (the `dotnet-change` skill makes this a required step):

- **always**: `naming.md`, `styling.md`, `best-practices.md`, `xml-documentation.md`
- endpoints/auth/SQL/config/logging/crypto: `security.md` · hot paths/buffers/SIMD/`unsafe`:
  `performance.md` · awaits/locks/tasks/shared state: `concurrency.md` · tests: `testing.md` ·
  shared catalog: `antipatterns.md`

The plugin's `dotnet-code-review` agent validates against the same files at review time (every
aspect in one pass) — this list exists to reduce how often it finds something, not to replace it.

## Write-time checklist — the highest-cost-if-caught-late items

- **No credential-shaped literal in source** — configuration comes from `IConfiguration`/environment/
  a secret store, never a string literal, even a placeholder-looking one.
- **No string-concatenated/interpolated SQL** in a raw-SQL API call — parameterize.
- **Every controller/endpoint gets an explicit `[Authorize]` or `[AllowAnonymous]`.**
- **New tests exercise real dependencies, not an in-memory database substitute**, for anything
  asserting constraint/transaction/query-translation behavior the substitute doesn't share.

Full per-tool reference: `${CLAUDE_PLUGIN_ROOT}/docs/tool-reference.md`.
```

If Step 3 found a scoped-but-resolvable overlap, add one sentence noting the boundary — e.g. "For
non-.NET code, `<other plugin>` remains the tool of record; this rule only governs `.cs`." One sentence;
don't restate the other plugin's docs.

## Step 5 — Present the plan, then wait

Show the user, in chat (not applied yet):
- The full protocol-rule content and its path.
- The list of the nine standards files to be copied (titles + one line each, not full contents — offer
  to show any in full), and the skip-copies alternative from "What gets written".
- One line on what Step 3 found, and how it was handled (collisions included).
- One line stating plainly that the protocol rule is always-loaded (rules load independently of
  CLAUDE.md, alongside it, same priority — not above it), that the standards copies load only when
  read, and that the `PreToolUse` hooks are the actual enforcement. CLAUDE.md itself is untouched.

Then ask directly whether to proceed. Use AskUserQuestion if there are real options to choose between
(a Step 3 coexistence resolution, a name collision, copies vs. no-copies). **Do not write until the
user has said yes.** A generic "go ahead and set it up" from earlier is not that yes if the plan has
not been shown yet.

## Step 6 — Back up, then apply

1. For every file about to be written that already exists (a re-run, or a collision the user resolved
   as "replace"), copy it to `.claude/dotnet-toolkit/backups/<name>.md.<UTC timestamp>.bak` first.
   Keep backups after a successful apply.
2. Write the protocol rule and the approved standards copies. They're markdown, so `Write`/`Edit` is
   correct — `validate_patch` is for `.cs`, and the hooks don't touch these files.
3. Confirm back: what was written, what was backed up, and how to undo.

## Step 7 — Re-running (update, not duplicate)

A re-run is a refresh: diff each existing file against the current template/plugin copy (the tool
surface or the standards may have changed since), show *those diffs* in Step 5 instead of full text,
back up per Step 6, and replace content in place — same paths, no new files. Preserve a consuming
repo's local edits to its standards copies by showing the diff rather than silently overwriting; if
the repo edited a copy, that's their convention now — ask before replacing it.

If a `<!-- dotnet-toolkit:start -->`/`<!-- dotnet-toolkit:end -->` marker block exists inside
`CLAUDE.md` (an artifact from before this skill stopped writing to CLAUDE.md), propose removing it
alongside the refresh (see "Undoing this later"), since the rule files alone are sufficient.

## Undoing this later

- **Remove everything**: delete `.claude/rules/dotnet-toolkit-csharp.md` and the nine standards copies
  (or restore from `.claude/dotnet-toolkit/backups/`). That's the whole uninstall.
- **If an old CLAUDE.md marker block exists from a prior version of this skill**: delete everything from
  `<!-- dotnet-toolkit:start -->` to `<!-- dotnet-toolkit:end -->` inclusive, or restore the newest
  backup over `CLAUDE.md`.

The hooks come and go with the plugin itself — uninstalling the plugin removes them; there is nothing
repo-local to clean up.

Mention all of these when reporting Step 6's result.
