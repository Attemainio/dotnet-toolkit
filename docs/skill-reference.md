# Skill reference

The plugin ships seven skills under `skills/`. Each is a workflow definition the model loads when its
trigger matches; this page is the catalog of what each one is for and what it reads or writes.

## `dotnet-code-query` — the read protocol

**When**: exploring, searching, inspecting, or analyzing C# — orienting in a codebase, finding a
symbol, callers/references, implementations, type shapes.

Carries what applies across all the read-side MCP tools: the decision table routing a question to a
tool, expansion gating on `referenceCounts`, symbol addressing, version tokens, workspace readiness,
and task attribution. The reason it exists: Grep and Read give wrong answers on C# (text search cannot
see interface/virtual/delegate dispatch, counts comment matches as hits, silently under-reports on
truncation), while the MCP tools answer from a Roslyn semantic model at a fraction of the tokens.

**Per-tool mechanics deliberately live outside it**, in `docs/tools/<tool>.md`, read on demand. The
skill is kept under ~5k tokens because Claude Code re-attaches only the first 5,000 tokens of an
invoked skill after auto-compaction — a larger skill loses its later sections mid-session while still
pointing at them. `docs/tools/_index.md` is the router between the two.

## `dotnet-change` — the write protocol

**When**: changing C# — editing a method or type, changing a signature, renaming, fixing a compile
error.

Carries the `validate_patch` protocol: `baseVersions` from `get_symbol`'s `contentVersion`, applying
straight through with `applyOnSuccess: true` rather than dry-running first, the sufficiency triple, the
required `intent`, batching from `suggestedInspection`, and amending a failed patch through its
`draftId` instead of resubmitting it. Routes a **pure rename** away to `rename_symbol`, which derives the
call-site edits rather than having them authored. Its blast-radius step routes a not-yet-located change
out to the `dotnet-explore` agent (`docs/agent-reference.md`) so the wide search is paid in a context
that gets discarded. Also carries the pre-edit standards step: before the first C# edit of a session,
read the relevant `.claude/rules/` standards per `csharp-standards.md`'s index, and give any touched
symbol lacking a `<summary>` one in the same edit.

## `dotnet-review` — delegating review

**When**: the user asks for a code review of any kind — PR/diff review, naming/styling, performance,
concurrency, dead code, XML docs, test coverage, security.

Teaches the main conversation to delegate to the `dotnet-code-review` subagent: each instance reviews
**all quality aspects** of one stated scope, and large targets are partitioned into disjoint scopes
reviewed by parallel instances. Covers how to partition, what context each instance needs (scope,
`mode`, baseline, the exceptional `focus:` narrowing, hot-path hints), and how to merge per-scope
results — including the `Standards:` line each instance reports, since the agent loads a fixed core of
standards plus only those the scoped code triggers, and an untriggered aspect is not-assessed rather
than clean. The agent's own process lives in `agents/dotnet-code-review.md` (self-contained);
`docs/agent-reference.md` documents its design for maintainers.

## `dotnet-toolkit-init` — the whole consumer lifecycle

**When**: "set up dotnet-toolkit here", "make Claude use the MCP tools in this repo" — and equally
"did the init work", "is this repo wired up correctly", "are my copies out of date", "what does
uninstalling leave behind".

Installing the plugin makes the tools *available*; nothing makes a fresh session in a consuming repo
*prefer* them or follow the standards — plugins cannot ship auto-loading rules, only a repo's own
`.claude/rules/` is scanned. This skill writes that guidance into the target repo: the two
always-loaded rules — `tool-protocol.md` (tool table, `dotnet-explore` delegation, write path) and
`csharp-standards.md` (standards index, write-time checklist) — copied verbatim, plus copies of the
standards files (list in `.claude/rules/csharp-standards.md`'s index). It also merges the **read-only**
MCP tools into `.claude/settings.json`'s permission allowlist — without that, every call the rules
just mandated raises a prompt — while deliberately leaving `validate_patch`/`rename_symbol` out, since
a write to the user's source should keep asking. What it installed is recorded in
`.claude/dotnet-toolkit/install.json`.

**Re-running it is the verify-and-refresh path** (Step 8), which is why there is no separate
install-check skill. The manifest's per-file hashes let it distinguish *the plugin changed this*
(refresh silently) from *the repo edited this* (show the diff and ask). It then runs the checklists
in `docs/install-verify.md`: installed state, always-loaded footprint against its ~6 KB budget, and an
uninstall dry run. Approval-gated, backed up, additive — it never touches the repo's CLAUDE.md.

The *maintainer-side* counterpart — auditing whether this skill's procedure still matches what the
plugin ships — is `dotnet-toolkit-consistency` Step 4b, via `docs/install-audit.md`.

## `dotnet-toolkit-consistency` — the self-audit

**When**: after any tool addition/removal/rename/signature change, after editing a hook or script, after
adding a new doc/skill/rule file, or whenever something describing the tool surface looks stale.

Audits `Tools/*.cs` as ground truth against every file that describes the tool surface — docs, skills,
the agent definition, rules, hooks, CLAUDE.md, README — and fixes exact drift file by file. **It owns the
authoritative list of those files**, so a new doc, skill, or tool group is wired in by adding a row
there. Also enforces the always-loaded and per-skill size budgets, and checks that anything operational
reaches consumers through a shipped file rather than living only in this repo's CLAUDE.md or a
maintainer's memory. Ships to consumers but its primary use is on this repo itself.

## `dotnet-toolkit-selfeval` — the efficiency evaluation

**When**: "self-evaluate", "how efficient are these tools here", "audit the MCP responses for
redundancy", or before/after changing a tool's arguments or return shape.

Runs a fixed probe matrix over every shipped tool **against the repo the server is already pointed at**,
and measures each call's exact token cost from `get_retrieval_metrics` deltas isolated by a
caller-supplied `taskId`. Four analyses run over every probe — routes, redundancy, noise, and whether an
*advisory* field (`search_index`'s `shape` column) actually pays for itself when followed. Findings are
labelled `[bug]` / `[warning]` / `[message]`.

How to run each analysis lives in **`docs/selfeval-analyses.md`**, read on demand rather than carried in
the skill: all four are needed on every run, and a skill over ~5k tokens is silently truncated from the
end, which would drop the later analyses while its Step 3 table still pointed at them.

The distinction that makes it useful: the consuming repo is the **specimen**, dotnet-toolkit is the
**subject**. Findings are always improvements to this plugin, never to the specimen's code; the
specimen's structural oddities (partial classes, nested types, overload sets, `.slnx` vs `.sln`) matter
only as the conditions under which a tool underperforms, which is why the run is worth repeating in
every repo the plugin is installed into. Read-only — `validate_patch` probes run with
`applyOnSuccess: false` and nothing is ever fixed by the skill itself. Distinct from
`dotnet-toolkit-consistency`, which checks whether the *docs* match the code; this one checks whether
the code is *efficient*.
