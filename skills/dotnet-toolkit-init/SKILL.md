---
name: dotnet-toolkit-init
description: Use when the user asks to set up, install, wire up, or apply dotnet-toolkit's tool-usage and coding-standards rules into a project — e.g. "set up dotnet-toolkit here", "/dotnet-toolkit-init", "make Claude use the MCP tools in this repo". Writes a path-scoped rule into .claude/rules/ that mandates the MCP tools over Grep/Read/find for C# and carries a write-time security/testing checklist, checks for conflicts with other installed plugins, backs up anything it touches, and only writes after the user approves the exact diff. Does not modify the repo's CLAUDE.md.
---

# Wiring dotnet-toolkit into a project

Installing this plugin (`/plugin install`, or `--plugin-dir`) makes its MCP tools *available*. It does
not make Claude *prefer* them — nothing tells a fresh session in a consuming repo that Grep and Read give
wrong answers on C#, or that `validate_patch` is the write path. It also doesn't give the consuming repo
any proactive nudge toward the security/testing standards `dotnet-code-review`'s `security` and `testing`
dimensions check for at review time — a plugin can ship `docs/*.md` for an *agent* to read explicitly
(via `${CLAUDE_PLUGIN_ROOT}/docs/...`), but it cannot ship a `.claude/rules/` file the harness auto-loads;
only a repo's own `.claude/rules/` gets scanned that way, and a plugin has no manifest field to register
one. This skill writes that guidance into a target repo, additively, and only with explicit approval.

**This skill does not touch the repo's `CLAUDE.md`.** Per Claude Code's documentation
(`code.claude.com/docs/en/memory`), `.claude/rules/` is discovered and loaded independently of CLAUDE.md —
rules are not appended into that file at runtime, they are a separate context injection. A path-scoped
rule (`paths: ["**/*.cs"]`, what this skill writes) loads on its own the moment Claude reads a matching
file; it needs no pointer elsewhere to be discovered. The one artifact this skill produces is therefore
self-sufficient, and the project's own CLAUDE.md — its architecture, commands, and conventions, written
and owned by the project — is left alone entirely. This also makes install/uninstall a single-file
operation: see "Undoing this later" below.

**Do not skip the approval step under any circumstances**, even if the user's request sounded like a
green light to "just do it." This file changes how every future session in that repo behaves; show the
exact content and wait for a yes.

## What gets written

One artifact:

| File | Content |
| --- | --- |
| `.claude/rules/dotnet-toolkit-csharp.md` | the full C# protocol (tool table, write path, worked steps) *and* a write-time security/testing checklist, under `paths: ["**/*.cs"]` so it loads **when Claude touches a C# file**, not once at launch |

The checklist is folded into the same rule file rather than a second one deliberately — one file means one
approval step and one load event per touched `.cs` file, instead of asking the harness to inject two
separate rules at the same moment for a concern that's really one "how to touch C# here" protocol.

**Be honest with the user about what this does and does not buy.** Per Claude Code's documentation,
rules without `paths` load at launch *with the same priority as `.claude/CLAUDE.md`* — there is no
precedence win, and both are context rather than enforced configuration. This rule ships with `paths`, so
what it buys is **timing**: it stays out of context until a `.cs` file is actually touched. The actual
enforcement is the plugin's `PreToolUse` hook (`hooks/hooks.json`), which blocks `Edit`/`Write` on
existing `.cs` files and ships with the plugin — it needs no per-repo setup and this skill does not
install it. Do not tell the user the rule "overrides" CLAUDE.md; it loads alongside it, not above it, and
does not touch it at all.

## Step 1 — Locate the target repo

The target repo is the current working directory (or `CLAUDE_PROJECT_DIR` if set) — **not** this plugin
repo, unless the user is deliberately testing the skill against it.

The rule file is the only artifact and stands alone regardless of whether the repo has a `CLAUDE.md` at
all — nothing here depends on that file existing.

## Step 2 — Read what is already there

Read `CLAUDE.md` if present (read-only, for Step 3's conflict check — it is never written to) and list
`.claude/rules/`. The project's own conventions take priority over anything this skill adds. Concretely:

- Never reorder, reword, or remove anything already in `.claude/rules/`.
- If an existing rule already covers tool usage, code search, or "how to explore this codebase," read it
  carefully — Step 3 decides whether it complements or conflicts.

## Step 3 — Detect other plugins and existing tool/standards guidance

- `.mcp.json` at the repo root — other MCP servers registered, and what they cover.
- `.claude/settings.json` / `.claude/settings.local.json` — enabled plugins, existing permissions.
- `.claude/rules/*.md` — especially any with `paths:` matching `**/*.cs`, which would land in context
  at exactly the same moment as ours; also any existing security/testing coding-standards rule, since our
  template's checklist section would overlap it.
- The CLAUDE.md text — any instruction of the shape "use X instead of grep/Read" for *any* language, or
  any existing secrets/auth/testing standards guidance.

This is a quick scan, not a deep audit. These are config/markdown files, so plain `Read`/`ls` is correct
here — the plugin's own tools are for `.cs`.

Then decide:

- **No other code-intelligence plugin** → draft as-is.
- **Another plugin governs other languages, no overlap** → draft as-is; note the scoping in Step 4's draft.
- **Genuine overlap** — another plugin's instructions already govern `.cs` search or edits → do not
  draft silently. Surface the conflict and ask how to resolve it (defer to the existing plugin, scope
  dotnet-toolkit to a subfolder, replace the older guidance) before going further.

## Step 4 — Draft the rule file

Write `.claude/rules/dotnet-toolkit-csharp.md` with this content. Keep the `paths:` frontmatter exactly
as shown — without it the rule loads unconditionally at launch and the timing benefit is lost.

```markdown
---
paths:
  - "**/*.cs"
---

# C# in this repo: dotnet-toolkit MCP tools and coding standards

You are touching a `.cs` file. This repo has the dotnet-toolkit plugin installed — a Roslyn-powered
MCP server. Its tools are the default path for C#, not Grep, Glob, `find`, `ls`, `cat`, bare `Read`,
or `Edit`/`Write`.

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
| `Edit`/`Write` then `dotnet build` | `validate_patch` |

## Reading

A `PreToolUse` hook also enforces the read side: `Read` on a `.cs` file a project actually compiles
is blocked in favor of `search_index`/`get_symbol`, so reaching for it costs a round trip and returns
nothing. It travels with the plugin like the write-side guard below — no separate install step.

## Writing

`validate_patch` is the write path, and a `PreToolUse` hook enforces it — `Edit`/`Write` on an
existing `.cs` file is blocked, so reaching for it costs a round trip and changes nothing.

It is the **only** writer to the development log. An edit that bypasses it is a change whose
reasoning is gone when the conversation ends; `search_log` cannot recover it and the next session
re-derives or silently contradicts it. The compile check is the cheap half; the log entry is the
half that is unrecoverable.

1. `get_symbol` — keep `contentVersion` and the `declarationSites` line span.
2. `validate_patch` with `baseVersions: {symbolId: contentVersion}` and line-span `edits`,
   `applyOnSuccess: false` — read the ladder verdict without touching disk.
3. Re-send with `applyOnSuccess: true` and an `intent` in user terms once `isSufficient: true`.

"Too large or too interleaved to decompose" is not a reason to fall back to `Edit`. Split it into
more `validate_patch` calls, one per touched symbol, sharing one `intent`.

Only new-file creation is legitimately outside this: `baseVersions` needs a `symbolId` that does not
exist yet. `Write` the file, then change it through `validate_patch`.

Full per-tool reference: `${CLAUDE_PLUGIN_ROOT}/docs/tool-reference.md`.

## Before writing or editing: the highest-cost-if-caught-late checks

A handful of items where catching the mistake now is much cheaper than catching it in review. Full
standards: `${CLAUDE_PLUGIN_ROOT}/docs/security.md`, `${CLAUDE_PLUGIN_ROOT}/docs/testing.md`,
`${CLAUDE_PLUGIN_ROOT}/docs/best-practices.md` — this is a reinforcement pointer, not the whole standard.

- **No credential-shaped literal in source** — connection strings, API keys, tokens. Configuration comes
  from `IConfiguration`/environment/a secret store, never a string literal, even a placeholder-looking one.
- **No string-concatenated/interpolated SQL** in a raw-SQL API call — parameterize. EF Core's LINQ surface
  and `FromSqlInterpolated` already do this safely.
- **Every controller/endpoint gets an explicit `[Authorize]` or `[AllowAnonymous]`** — never an unmarked
  endpoint relying on whatever the global default happens to be.
- **New tests exercise real dependencies, not an in-memory database substitute**, for anything asserting
  constraint/transaction/query-translation behavior that provider doesn't share with the real one.

This plugin's `dotnet-code-review` agent checks these exhaustively at review time regardless of whether
this rule fired (`dimension: security` / `dimension: testing`) — this list exists to reduce how often it
needs to, not to replace it.
```

If Step 3 found a scoped-but-resolvable overlap, add one sentence noting the boundary — e.g. "For
non-.NET code, `<other plugin>` remains the tool of record; this rule only governs `.cs`." One sentence;
don't restate the other plugin's docs.

## Step 5 — Present the plan, then wait

Show the user, in chat (not applied yet):
- The full rule file content and its path.
- One line on what Step 3 found, and how it was handled.
- One line stating plainly that this rule loads independently of CLAUDE.md (per Claude Code's docs, not
  appended into it), that `paths` scoping buys load timing rather than precedence over CLAUDE.md, and
  that the `PreToolUse` hook is the actual enforcement. CLAUDE.md itself is untouched.

Then ask directly whether to proceed. Use AskUserQuestion if there are real options to choose between
(a Step 3 coexistence resolution). **Do not write until the user has said yes.** A generic "go ahead and
set it up" from earlier is not that yes if the plan has not been shown yet.

## Step 6 — Back up, then apply

1. If `.claude/rules/dotnet-toolkit-csharp.md` already exists (a re-run), copy it to
   `.claude/dotnet-toolkit/backups/dotnet-toolkit-csharp.md.<UTC timestamp>.bak` first. Keep backups
   after a successful apply.
2. Write the rule file. It's markdown, so `Write`/`Edit` is correct — `validate_patch` is for `.cs`, and
   the hook does not touch this file.
3. Confirm back: what was written, what was backed up (if a re-run), and how to undo.

## Step 7 — Re-running (update, not duplicate)

If `.claude/rules/dotnet-toolkit-csharp.md` already exists, this is a refresh: diff its content against
the current template (it may have drifted if the tool surface or the standards checklist changed), show
*that* diff in Step 5 instead of the full text, back up as in Step 6, and replace the file's content —
same path, no new file.

If the existing file instead has a `<!-- dotnet-toolkit:start -->`/`<!-- dotnet-toolkit:end -->` marker
block inside a `CLAUDE.md` (a `(v1)`/`(v2)` artifact from before this skill stopped writing to CLAUDE.md),
propose two things together: writing the current rule-file template to
`.claude/rules/dotnet-toolkit-csharp.md`, and removing the old marker block from `CLAUDE.md` (see
"Undoing this later" for the exact removal). Say why: CLAUDE.md is no longer touched by this skill, and
the rule file alone is sufficient. Show the user both changes before applying either.

## Undoing this later

- **Remove the rule**: delete `.claude/rules/dotnet-toolkit-csharp.md`. That's the only file this skill
  writes, so this is the whole uninstall.
- **If an old CLAUDE.md marker block exists from a prior version of this skill**: delete everything from
  `<!-- dotnet-toolkit:start -->` to `<!-- dotnet-toolkit:end -->` inclusive, or restore the newest file
  from `.claude/dotnet-toolkit/backups/` over `CLAUDE.md`.

The hook comes and goes with the plugin itself — uninstalling the plugin removes it; there is nothing
repo-local to clean up.

Mention all of these when reporting Step 6's result.
