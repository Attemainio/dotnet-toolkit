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
`draftId` instead of resubmitting it. Also carries the pre-edit standards step: before the first C# edit of a session,
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

## `dotnet-toolkit-init` — wiring a consuming repo

**When**: "set up dotnet-toolkit here", "make Claude use the MCP tools in this repo".

Installing the plugin makes the tools *available*; nothing makes a fresh session in a consuming repo
*prefer* them or follow the standards — plugins cannot ship auto-loading rules, only a repo's own
`.claude/rules/` is scanned. This skill writes that guidance into the target repo: an always-loaded
protocol rule (tool table, write path, standards index, write-time checklist) plus copies of the
standards files (list in `.claude/rules/csharp-standards.md`'s index) into the repo's `.claude/rules/`.
Approval-gated, backed up, additive — it never touches
the repo's CLAUDE.md, and uninstall is deleting the listed files.

## `dotnet-toolkit-install-check` — auditing the installation

**When**: "did the init work", "check the dotnet-toolkit installation", "is this repo wired up
correctly", "what does uninstalling leave behind", "audit the init skill".

Builds the expected asset inventory from the plugin tree itself and sorts every shipped file into one
of four delivery mechanisms — *ships active* (MCP server, hooks, skills, agent), *must be copied*
(the protocol rule and standards copies), *referenced by `${CLAUDE_PLUGIN_ROOT}` path* (`docs/`), and
*created at runtime* (`cache/`, `backups/`, `config.json`). It then checks `dotnet-toolkit-init`'s
"what gets written", "what is deliberately not written", and "undoing this later" sections against
that inventory, and in a consuming repo checks the state actually on disk.

Also enforces the two boundaries the installation is defined by: the consumer's CLAUDE.md is never
touched (no marker block, no dotnet-toolkit content), and the protocol rule stays a **declaration**
of when and how — budget ~6 KB — with the full workflow behind on-demand pointers. Read-only: it
reports and may offer to re-run `dotnet-toolkit-init`, never installs on its own. Complements
`dotnet-toolkit-consistency`, which checks the same files for *accuracy* rather than *delivery*.

## `dotnet-toolkit-consistency` — the self-audit

**When**: after any tool addition/removal/rename/signature change, after editing a hook or script, after
adding a new doc/skill/rule file, or whenever something describing the tool surface looks stale.

Audits `Tools/*.cs` as ground truth against every file that describes the tool surface — docs, skills,
the agent definition, rules, hooks, CLAUDE.md, README — and fixes exact drift file by file. Ships to
consumers but its primary use is on this repo itself.

## `dotnet-toolkit-selfeval` — the efficiency evaluation

**When**: "self-evaluate", "how efficient are these tools here", "audit the MCP responses for
redundancy", or before/after changing a tool's arguments or return shape.

Runs a fixed probe matrix over every shipped tool **against the repo the server is already pointed at**,
and measures each call's exact token cost from `get_retrieval_metrics` deltas isolated by a
caller-supplied `taskId`. Reports where the same outcome was reachable with fewer calls or fewer tokens
(route efficiency), which response fields restate what the caller already knew, and which outputs carry
noise — labelled `[bug]` / `[warning]` / `[message]`.

The distinction that makes it useful: the consuming repo is the **specimen**, dotnet-toolkit is the
**subject**. Findings are always improvements to this plugin, never to the specimen's code; the
specimen's structural oddities (partial classes, nested types, overload sets, `.slnx` vs `.sln`) matter
only as the conditions under which a tool underperforms, which is why the run is worth repeating in
every repo the plugin is installed into. Read-only — `validate_patch` probes run with
`applyOnSuccess: false` and nothing is ever fixed by the skill itself. Distinct from
`dotnet-toolkit-consistency`, which checks whether the *docs* match the code; this one checks whether
the code is *efficient*.
