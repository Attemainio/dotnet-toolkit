# Agent reference

This plugin ships two subagents, both read-only in intent and both self-contained:

- **`dotnet-code-review`** (`agents/dotnet-code-review.md`) — a **validation layer** that checks code
  against the standards in `.claude/rules/`, not a source of standards itself. Runs *after* code exists.
- **`dotnet-explore`** (`agents/dotnet-explore.md`) — a **navigator** that maps a task onto the
  codebase's symbols and reference graph. Runs *before* code is written. See the section at the end.

**This document is human-facing. Neither agent reads it.** Each agent file is self-contained — process,
loading rules, output format, boundaries — and is the authority. This one describes the design for
maintainers; if the two disagree, the agent file is right and this one is stale.

Everything from here to the `dotnet-explore` section is about `dotnet-code-review`.

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

## Adding an aspect (dotnet-code-review)

A new aspect is a new `.claude/rules/*.md` file, a row in `csharp-standards.md`'s index (with a "When"
condition stated as an observable property of the code, so the reviewer's trigger matching can use it),
and one entry in the agent file's per-aspect evidence disciplines — never a new agent file. Decide
explicitly whether it joins the always-loaded core or the triggered set; the core should only grow for
something both cheap and high-cost-if-missed, which is why `security.md` is in it.

# dotnet-explore

A Haiku-model **navigator**: given a task someone is about to perform, it returns where that task lives
— entry-point `symbolId`s, direct references by relation, affected files grouped by project, the
transitive reach when asked, and a `Suggested next calls` list the main agent can execute verbatim. It
draws the map; it never builds on it, and it never judges what it finds.

It exists because the main agent's fan-out phase is the cheapest work in a change and the most
context-expensive to keep: a dozen `search_index`/`get_references`/`get_symbol` responses stay in the
main window for the rest of the session, when all that survives their usefulness is a handful of
`symbolId`s and file paths. Delegating the fan-out to Haiku pays for the wide search in a context that
is then discarded, and returns only the residue. `dotnet-change`'s step 2 ("know the blast radius")
points at it for exactly that reason.

## Read-only *is* a capability boundary here — unlike the reviewer

Deliberately **no `memory:` key.** That is the whole difference from `dotnet-code-review`, whose
`memory: project` makes the harness grant `Write`/`Edit` back so it can maintain its memory namespace
(see "Read-only is by instruction, not by capability" above). `dotnet-explore` gets no memory namespace,
so its resolved tool list contains no writer at all: no `Write`, `Edit`, `NotebookEdit`,
`validate_patch`, or `rename_symbol`. Adding project memory to this agent would silently hand it
`Write`/`Edit` and turn a real boundary back into an instruction — don't, and if a future version
genuinely needs memory, the trade has to be stated in the agent file's Hard boundaries section.

## Tool grant

Every read-side MCP tool, **including `get_project_graph` and `detect_circular_dependencies`** — the two
the reviewer is denied. The reasoning inverts: those answer solution-wide questions a disjoint review
slice structurally cannot ask, but "which projects does this change reach" is precisely a blast-radius
question, so they are core here.

Plus `Read`, restricted **by instruction** to `docs/tools/<tool>.md` — the on-demand escape hatch for
tool mechanics, so the agent file itself can stay a compact router instead of an inlined manual. Its
Hard boundaries name what is off-limits within that: no `.cs` file (the `guard-cs-read` hook blocks it
anyway), and specifically not `validate_patch.md`/`rename_symbol.md`, which document a path it has no
tools for. No `Grep`/`Glob`: a text search over C# is the thing this agent exists to replace, and
non-C# files are out of scope by design.

## Why it must not report a `contentVersion`

The agent calls `get_symbol` at the *default* include and is forbidden from passing `include: "all"` or
echoing a `contentVersion`. A version is an edit lease scoped to the layers served, and it goes stale
the moment anything moves — a main agent that patched against a version relayed through a subagent
would hit `stale_base` at best and, if the file moved underneath, could revert other changes in it. So
the handoff is `symbolId` + locations, and the caller leases its own version. That is what the
`# lease your own contentVersion` comment in its `Suggested next calls` output is for.

## Measured behavior — three probes against this repo

First runs, on `get_symbol`'s `include` grammar, on adding a fifth hook subcommand, and on the
provisional-id prefixes: **39k–58k subagent tokens over 18–26 tool calls**, returning ~1–2 KB of map.
Spot-checked against source, the substance held (`HookCli`'s dispatch switch located exactly; caller
counts matching `referenceCounts`).

A fourth probe (the loopback control server) was measured exactly, by snapshotting
`get_retrieval_metrics` either side of it: **23 calls returning 11,450 tokens** of tool responses,
handed back as a ~975-token report. That is the whole argument for the agent — **a 12× reduction in what
lands in the main window**, with the fan-out paid in a context that is then discarded.

**It is not a token saving, and shouldn't be sold as one.** That probe billed 38.8k subagent tokens to
keep 11.5k out of the main window, so total spend rose ~3–4×. What it buys is window *occupancy*:
retrieval in the main context is paid once and then carried for every remaining turn of the session,
and it is what drives a session toward the auto-compaction cliff that truncates invoked skills. Two
honest deflators on the 12×: a main agent that already knows the codebase would have spent maybe 5–7k
rather than 11.5k on the same question (so ~5–6× like-for-like), and delegation costs ~1.1k plus an
~80–100s round trip on the caller's side. For anything answerable in two calls, delegating is worse —
which is what `dotnet-change`'s pointer says.

Reproducing any of this needs the agent's `taskId`. The agent file requires it — one `explore_<slug>` id
minted per run, passed on every call except `workspace_status` (which takes no arguments and records no
telemetry), and echoed in the report's **Target** line — so that
`get_retrieval_metrics(groupBy: "task", taskIds: [...])` gives one exploration's exact cost. Without it
the ambient session id is shared with the main agent and snapshot-subtraction is the only method.

**Unverified as of this writing, for a reason worth knowing when you tune this agent: the harness
appears to snapshot an agent definition when the plugin loads, not when a subagent spawns.** Three
probes run after edits to the file all behaved like the pre-edit version — no `taskId`, no
`What would need to change` section, and a call count above the budget then in force. So any change here
needs a session/plugin restart before a probe can test it, and a probe run in the same session as the
edit measures the old file. Don't read such a run as the instruction being ignored.

Three things the probes changed in the agent file, worth knowing before tuning it again:

- **The call budget was 8–12 and was ignored twice.** A real blast-radius question needs ~20, so the
  ceiling is now 20 with a stated hard stop. A budget the agent routinely blows through teaches it that
  the file's limits are soft — pick a number the work actually fits.
- **It invented a "what would need to change" section, and the section was better than the spec.** It
  is now part of the contract rather than something the format bans.
- **It reported a declaration span read off a `search_index` hit** (`113-160` where
  `declarationSites` says `102-160` — the doc-comment lines). Since the caller anchors an edit there,
  the file now requires spans from `declarationSites` and nothing else.

## Honesty contract

Same shape as the reviewer's `Standards:` line, applied to retrieval instead of standards: a mandatory
**Not covered** section carrying `limitedBy` verbatim, the call-budget stop if it hit one, any term that
returned nothing, and any narrowing of a vague request. A map that says where it ends is useful; one
that looks complete is dangerous — particularly under `stale` (line numbers already wrong) or
`degraded` (results possibly wrong, not merely thin).
