# Skill reference

The plugin ships five skills under `skills/`. Each is a workflow definition the model loads when its
trigger matches; this page is the catalog of what each one is for and what it reads or writes.

## `dotnet-code-query` — the read protocol

**When**: exploring, searching, inspecting, or analyzing C# — orienting in a codebase, finding a
symbol, callers/references, implementations, type shapes.

Carries the retrieval protocol for the read-side MCP tools: session/task ids, resolution escalation,
expansion gating, leases, and refetch-after-compaction. The reason it exists: Grep and Read give wrong
answers on C# (text search cannot see interface/virtual/delegate dispatch, counts comment matches as
hits, silently under-reports on truncation), while the MCP tools answer from a Roslyn semantic model at
a fraction of the tokens.

## `dotnet-change` — the write protocol

**When**: changing C# — editing a method or type, changing a signature, renaming, fixing a compile
error.

Carries the `validate_patch` protocol: `baseVersions` from `get_symbol`'s `contentVersion`, the
`applyOnSuccess: false` dry run, the sufficiency triple, the required `intent`, and batching from
`suggestedInspection`. Also carries the pre-edit standards step: before the first C# edit of a session,
read the relevant `.claude/rules/` standards per `csharp-standards.md`'s index, and give any touched
symbol lacking a `<summary>` one in the same edit.

## `dotnet-review` — delegating review

**When**: the user asks for a code review of any kind — PR/diff review, naming/styling, performance,
concurrency, dead code, XML docs, test coverage, security.

Teaches the main conversation to delegate to the `dotnet-code-review` subagent: each instance reviews
**all quality aspects** of one stated scope, and large targets are partitioned into disjoint scopes
reviewed by parallel instances. Covers how to partition, what context each instance needs (scope,
`mode`, baseline, the exceptional `focus:` narrowing, hot-path hints), and how to merge per-scope
results. See `docs/agent-reference.md` for the agent's own process.

## `dotnet-toolkit-init` — wiring a consuming repo

**When**: "set up dotnet-toolkit here", "make Claude use the MCP tools in this repo".

Installing the plugin makes the tools *available*; nothing makes a fresh session in a consuming repo
*prefer* them or follow the standards — plugins cannot ship auto-loading rules, only a repo's own
`.claude/rules/` is scanned. This skill writes that guidance into the target repo: an always-loaded
protocol rule (tool table, write path, standards index, write-time checklist) plus copies of the nine
standards files into the repo's `.claude/rules/`. Approval-gated, backed up, additive — it never touches
the repo's CLAUDE.md, and uninstall is deleting the listed files.

## `dotnet-toolkit-consistency` — the self-audit

**When**: after any tool addition/removal/rename/signature change, after editing a hook or script, after
adding a new doc/skill/rule file, or whenever something describing the tool surface looks stale.

Audits `Tools/*.cs` as ground truth against every file that describes the tool surface — docs, skills,
the agent definition, rules, hooks, CLAUDE.md, README — and fixes exact drift file by file. Ships to
consumers but its primary use is on this repo itself.
