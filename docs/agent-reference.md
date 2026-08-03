# Agent reference: dotnet-code-review

This plugin ships one review subagent, `dotnet-code-review` (`agents/dotnet-code-review.md`) — a
read-only **validation layer** that checks code against the standards in `.claude/rules/`, not a source
of standards itself.

**This document is human-facing. The agent does not read it.** `agents/dotnet-code-review.md` is
self-contained: process, standards-loading rule, evidence bars, review modes, scope discipline, output
format, boundaries, and memory discipline all live there, and that file is the authority. This one
describes the design for maintainers — if the two disagree, the agent file is right and this one is
stale.

It used to be a mandatory second read for the agent (~2.7k tokens on top of its own file, which the
harness already puts in the system prompt), duplicating the agent file on scope discipline and the
standards list. Folding it in removed a read and a round-trip per instance.

## Design

Each invocation reviews **all quality aspects at once** — correctness, naming, styling, best practices,
performance, concurrency, security, testing, XML documentation, and cleanup/duplication — over **one
precisely stated scope**. Parallelism comes from scope partitioning, not aspect splitting: a large
target is divided into disjoint slices (per folder, per project, per changed-file cluster) and one
instance of the same agent is launched per slice, all in a single message. Each instance covers
everything about its slice; together they cover everything about the target, with no file reviewed
twice.

The standards are shared: the main agent reads the same `.claude/rules/` files at write time (per
`csharp-standards.md`'s index), so writer and reviewer work from one source of truth. A consuming repo
overrides any file by placing its own copy at `.claude/dotnet-toolkit/<name>.md`; the agent checks for
that override before falling back to the bundled default, and a repo-local file fully replaces the
bundled one rather than blending with it.

The `dotnet-review` skill teaches the main conversation how to partition scope, what to tell each
instance, and how to merge their output.

## Token budget — why the agent is shaped the way it is

Seven parallel instances each filled to ~160k tokens, starting at ~43k before reading any reviewed
code, because every instance re-pays an identical fixed cost. Three properties of the agent file exist
to hold that down, and changing them without understanding the trade re-inflates it:

- **Tiered standards loading.** Six core files always; the other seven only when
  `csharp-standards.md`'s "When" column matches the retrieved code (~19k → ~7.8k). The cost is that an
  aspect can go unexamined, so the agent must end every report with a `Standards:` line naming what it
  loaded and skipped, and an untriggered aspect is reported **not-assessed**, never clean.
- **No `skills:` grant.** `dotnet-code-query` carries the *main agent's* read protocol — task ids,
  expansion gating, the write-path handoff — none of which a read-only reviewer uses. The retrieval
  guidance it does need is inline in the agent file instead. (The skill was 41.5 KB when this
  decision was made; it is now ~9 KB, with per-tool mechanics moved to `docs/tools/`. The grant is
  still declined — the reasoning is relevance, not size.)
- **Batched retrieval.** One `get_symbol` call with a `symbols` array over the whole scope, rather than
  declaration-layer → body-layer → references per symbol.

A related constraint: the `guard-cs-read` hook blocks `Read` on `.cs` files a project compiles, and
`PreToolUse` hooks fire for subagents too. The agent cannot be told to "just read whole files" — MCP
retrieval is the only available path for in-scope C#, which is why the batching above matters.

## Tool grant

The agent has `Read`, `Grep`, `Glob`, and the read-side MCP tools: `search_index`, `get_symbol`,
`get_references`, `search_log`, `get_scope`, `get_call_slice`, `get_call_hierarchy`,
`get_type_hierarchy`, `get_semantic_diff`, `workspace_status`.

`get_project_graph` and `detect_circular_dependencies` are deliberately **not** granted: they answer
solution-wide architecture questions (project dependency direction, reference cycles) that a single
disjoint scope slice structurally cannot ask, so they cost schema tokens in every instance while being
unusable in almost all of them. Solution-wide architecture review belongs to the main agent. The agent
raises such a suspicion as a note rather than a finding.

## Read-only is by instruction, not by capability

The `tools:` frontmatter omits `Edit`/`Write`, but `memory: project` makes the harness grant them
anyway so the agent can maintain `.claude/agent-memory/dotnet-toolkit-dotnet-code-review/` — the
resolved tool list *does* include `Write` and `Edit`. What keeps it from touching source, standards,
docs, or config is the Memory and Boundaries sections of the agent file, not a capability boundary.
Don't reason about this agent as if it were sandboxed.

## Adding an aspect

A new aspect is a new `.claude/rules/*.md` file, a row in `csharp-standards.md`'s index (with a "When"
condition stated as an observable property of the code, so the reviewer's trigger matching can use it),
and one entry in the agent file's per-aspect evidence disciplines — never a new agent file. Decide
explicitly whether it joins the always-loaded core or the triggered set; the core should only grow for
something both cheap and high-cost-if-missed, which is why `security.md` is in it.
