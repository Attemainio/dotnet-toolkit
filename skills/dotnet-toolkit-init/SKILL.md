---
name: dotnet-toolkit-init
description: Use when the user asks to set up, install, wire up, or apply dotnet-toolkit's tool-usage and coding-standards rules into a project — e.g. "set up dotnet-toolkit here", "/dotnet-toolkit-init", "make Claude use the MCP tools in this repo". Writes a path-scoped rule into .claude/rules/ that mandates the MCP tools over Grep/Read/find for C# and carries a write-time security/testing checklist, adds a short pointer to the repo's CLAUDE.md, checks for conflicts with other installed plugins, backs up anything it touches, and only writes after the user approves the exact diff.
---

# Wiring dotnet-toolkit into a project

Installing this plugin (`/plugin install`, or `--plugin-dir`) makes its MCP tools *available*. It does
not make Claude *prefer* them — nothing tells a fresh session in a consuming repo that Grep and Read give
wrong answers on C#, or that `validate_patch` is the write path. It also doesn't give the consuming repo
any proactive nudge toward the security/testing standards `dotnet-code-review`'s `security` and `testing`
dimensions check for at review time — a plugin can ship `docs/*.md` for an *agent* to read explicitly
(via `${CLAUDE_PLUGIN_ROOT}/docs/...`), but it cannot ship a `.claude/rules/` file the harness auto-loads;
only a repo's own `.claude/rules/` gets scanned that way, and a plugin has no manifest field to register
one. This skill writes both kinds of guidance into a target repo, additively, and only with explicit
approval.

**Do not skip the approval step under any circumstances**, even if the user's request sounded like a
green light to "just do it." These files change how every future session in that repo behaves; show the
exact content and wait for a yes.

## What gets written, and why it is split

Two artifacts, deliberately sized differently:

| File | Content | Why there |
| --- | --- | --- |
| `.claude/rules/dotnet-toolkit-csharp.md` | the full C# protocol (tool table, write path, worked steps) *and* a write-time security/testing checklist | path-scoped to `**/*.cs`, so it loads **when Claude touches a C# file**, not once at launch |
| `CLAUDE.md` | ~5 lines: the tools exist, the rule file governs their use | CLAUDE.md is the repo's *general* instruction file; a full tool protocol does not belong in it |

The checklist is folded into the same rule file rather than a second one deliberately — one file means one
approval step and one load event per touched `.cs` file, instead of asking the harness to inject two
separate rules at the same moment for a concern that's really one "how to touch C# here" protocol.

The split is the point. A rule in `.claude/rules/` with `paths:` frontmatter is injected at the moment
Claude reads a matching file — which counteracts the adherence decay that makes a launch-time CLAUDE.md
instruction get ignored 100k tokens into a session. Keeping CLAUDE.md's share to a pointer also leaves
that file for what it is for: the project's own architecture, commands, and conventions.

**Be honest with the user about what this does and does not buy.** Per Claude Code's documentation,
rules without `paths` load at launch *with the same priority as `.claude/CLAUDE.md`* — there is no
precedence win, and both are context rather than enforced configuration. What path-scoping buys is
**timing**, not priority. The actual enforcement is the plugin's `PreToolUse` hook (`hooks/hooks.json`),
which blocks `Edit`/`Write` on existing `.cs` files and ships with the plugin — it needs no per-repo
setup and this skill does not install it. Do not tell the user rules "override" CLAUDE.md.

## Step 1 — Locate the target repo

The target repo is the current working directory (or `CLAUDE_PROJECT_DIR` if set) — **not** this plugin
repo, unless the user is deliberately testing the skill against it.

A missing `CLAUDE.md` is no longer a blocker: the rule file is the primary artifact and stands alone. If
there is no `CLAUDE.md`, write the rule and tell the user that Step 5's pointer was skipped, suggesting
`/init` if they want one. Do not create a bare-bones CLAUDE.md just to have something to append to.

## Step 2 — Read what is already there

Read the existing `CLAUDE.md` in full if present, and list `.claude/rules/`. The project's own
conventions take priority over anything this skill adds. Concretely:

- Never reorder, reword, or remove anything already in either place.
- Additions are inserted at one point, not scattered.
- If an existing rule or CLAUDE.md section already covers tool usage, code search, or "how to explore
  this codebase," read it carefully — Step 3 decides whether it complements or conflicts.

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
- **Another plugin governs other languages, no overlap** → draft as-is; note the scoping in Step 5.
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
| `git diff` to judge a change | `get_semantic_diff` |
| Guessing why code looks the way it does | `search_log` |
| `Edit`/`Write` then `dotnet build` | `validate_patch` |

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

## Step 5 — Draft the CLAUDE.md pointer

Only if `CLAUDE.md` exists. Keep the markers verbatim — they make the block idempotently findable on a
re-run (Step 8) and removable on uninstall.

```markdown
<!-- dotnet-toolkit:start (v3) -->
## C# tooling

This repo has the dotnet-toolkit plugin installed: a Roslyn-powered MCP server for token-efficient C#
query (`search_index`, `get_symbol`, `get_references`, `get_scope`) and compiler-validated edits
(`validate_patch`). For `.cs` files these replace Grep/Read/Edit, and a write-time security/testing
checklist applies too — see `.claude/rules/dotnet-toolkit-csharp.md`, which loads automatically whenever
a C# file is in play.
<!-- dotnet-toolkit:end -->
```

Insert it **immediately after the file's title and any short intro paragraph**, before the first `##`
section. If the file has no clear title/intro, insert as the first `##` section. State the chosen
position in the plan so the user can redirect it.

## Step 6 — Present the plan, then wait

Show the user, in chat (not applied yet):
- The full rule file content and its path.
- The CLAUDE.md pointer block and the heading it will follow — or that CLAUDE.md was absent and skipped.
- One line on what Step 3 found, and how it was handled.
- One line stating plainly that rules carry the *same* priority as CLAUDE.md, that path-scoping buys
  load timing rather than precedence, and that the `PreToolUse` hook is the actual enforcement.

Then ask directly whether to proceed. Use AskUserQuestion if there are real options to choose between
(two candidate insertion points, a Step 3 coexistence resolution). **Do not write until the user has
said yes.** A generic "go ahead and set it up" from earlier is not that yes if the plan has not been
shown yet.

## Step 7 — Back up, then apply

1. If `CLAUDE.md` will be modified, copy it to
   `.claude/dotnet-toolkit/backups/CLAUDE.md.<UTC timestamp>.bak` first. Same for an existing
   `.claude/rules/dotnet-toolkit-csharp.md` on a re-run. Keep backups after a successful apply.
2. Write the rule file, then the CLAUDE.md block. Both are markdown, so `Write`/`Edit` is correct —
   `validate_patch` is for `.cs`, and the hook does not touch these.
3. Confirm back: what was written, what was backed up and where, and how to undo.

## Step 8 — Re-running (update, not duplicate)

If `.claude/rules/dotnet-toolkit-csharp.md` or the CLAUDE.md markers already exist, this is a refresh:
diff existing content against the current template (it may have drifted if the tool surface or the
standards checklist changed), show *that* diff in Step 6 instead of the full text, back up as in Step 7,
and replace only the rule file and the text between the existing markers — same position, no reordering.

A `(v1)` marker block from an older run holds the full protocol inline in CLAUDE.md. Migrating it is the
right move: propose replacing it with the pointer and moving the substance into the rule file, and say why
(it loads at the moment C# is touched instead of once at launch). A `(v2)` block predates the
security/testing checklist section — migrating to `(v3)` adds that section to the existing rule file
without otherwise disturbing it. Show both diffs together either way.

## Undoing this later

- **Remove the rule**: delete `.claude/rules/dotnet-toolkit-csharp.md`.
- **Remove the pointer**: delete everything from `<!-- dotnet-toolkit:start -->` to
  `<!-- dotnet-toolkit:end -->` inclusive.
- **Full restore**: copy the newest file from `.claude/dotnet-toolkit/backups/` back over `CLAUDE.md`.

The hook comes and goes with the plugin itself — uninstalling the plugin removes it; there is nothing
repo-local to clean up.

Mention all of these when reporting Step 7's result.
