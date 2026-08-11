# Agent design notes

> **Maintainer-facing. No agent or skill reads this file.** Each agent definition is self-contained
> and authoritative; if this page disagrees with `agents/<name>.md`, the agent file is right and this
> one is stale. Kept for the rationale that is derivable nowhere else.

This plugin ships four subagents, all read-only in intent:

- **`dotnet-code-review`** (`agents/dotnet-code-review.md`) — a **validation layer** that checks code
  against the standards in `standards/`, not a source of standards itself. Runs *after* code exists.
  Self-contained.
- **`dotnet-explore`** (`agents/dotnet-explore.md`) — a **navigator** that maps a task onto the
  codebase's symbols and reference graph. Runs *before* code is written. Self-contained. See the
  section at the end.
- **`dotnet-perf-mcp-probe`** and **`dotnet-perf-raw-probe`** (`agents/dotnet-perf-mcp-probe.md`,
  `agents/dotnet-perf-raw-probe.md`) — a **benchmark instrument pair**, not general-purpose agents:
  identical except for their tool grant (MCP tools + `Read` restricted to one file vs.
  `Read`/`Grep`/`Glob`/`Bash` only), and deliberately **not** self-contained — neither carries its own
  procedure or output format. Both read the same `skills/dotnet-performance/performance_protocol.md`
  for those; the per-run question list still comes from the invoking prompt, since it changes every
  run and a static file can't hold it. `dotnet-performance` is their only sanctioned
  launcher. See the section at the end.

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

The standards are shared: the main agent reads the same `standards/` files at write time (per the
table in `standards/index.md`), so writer and reviewer work from one source of truth. A consuming repo
reads exactly the same files the writer does — the plugin's own, resolved through `workspace_status`'s
`pluginRoot`. There is no per-repo override tier: one copy of each standard exists, so writer and
reviewer cannot be judging against different text.

The `dotnet-review` skill teaches the main conversation how to partition scope, what to tell each
instance, and how to merge their output.

## Token budget — why the agent is shaped the way it is

Seven parallel instances each filled to ~160k tokens, starting at ~43k before reading any reviewed
code, because every instance re-pays an identical fixed cost. Three properties of the agent file exist
to hold that down, and changing them without understanding the trade re-inflates it:

- **The inherited floor.** `CLAUDE.md` and `.claude/rules/dotnet-index.md` are injected into every
  subagent with no opt-out, so ~13 KB of Tier-0 text is part of each instance's fixed cost before it
  reads anything. Trimming those two files is the only lever on it; the agent file cannot decline
  them, and an instruction telling an agent not to read them saves a round trip, not a byte.
- **Tiered standards loading.** Six core files always; the other seven only when the standards
  table's "When" column (in `standards/index.md`) matches the retrieved code (~19k → ~7.8k). The
  cost is that an aspect can go unexamined, so the agent must end every report with a `Standards:`
  line naming what it loaded and skipped, and an untriggered aspect is reported **not-assessed**,
  never clean.
- **No `skills:` grant.** There is no read-protocol skill to grant: `dotnet-code-query` held one, but
  every part of it was a second copy of `dotnet-index.md`'s router or a `docs/tools/` manual, so it
  was deleted rather than shrunk again. The retrieval guidance the agent needs is inline in the
  agent file; routing is the always-loaded rule's job, and per-tool mechanics are one `Read` away.
- **Batched retrieval.** One `get_symbol` call with a `symbols` array over the whole scope, rather than
  declaration-layer → body-layer → references per symbol.

A related constraint: the `guard-cs-read` hook blocks `Read` on `.cs` files a project compiles, and
`PreToolUse` hooks fire for subagents too. The agent cannot be told to "just read whole files" — MCP
retrieval is the only available path for in-scope C#, which is why the batching above matters.

## Tool grant — why two tools are withheld

The authoritative list is the agent file's `tools:` frontmatter; it is not restated here.

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

A new aspect is a new `standards/*.md` file, a row in `standards/index.md`'s table (with a "When"
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
is then discarded, and returns only the residue. `dotnet-write`'s step 2 ("know the blast radius")
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
which is what `dotnet-write`'s pointer says.

Reproducing any of this needs the agent's `taskId`. The agent file requires it — one `explore_<slug>` id
minted per run, passed on every call except `workspace_status` (which takes no arguments and records no
telemetry), and echoed in the report's **Target** line — so that
`get_retrieval_metrics(groupBy: "task", taskIds: [...])` gives one exploration's exact cost. Without it
the ambient session id is shared with the main agent and snapshot-subtraction is the only method.

**Confirmed, not just suspected: the harness snapshots the agent registry when the plugin loads, not
when a subagent spawns.** Three probes run after edits to this file all behaved like the pre-edit
version — no `taskId`, no `What would need to change` section, and a call count above the budget then
in force — which was originally logged here as unverified. `dotnet-perf-raw-probe`'s own addition
settled it more sharply: a **brand-new** agent file, never edited from a prior version, was rejected
outright as `Agent type 'dotnet-perf-raw-probe' not found` for the entire remainder of the session it
was created in, and only became callable after the harness restarted. That rules out a weaker theory
(the harness re-reads a *known* agent's file lazily but caches the *set* of known agents) — the whole
registry is fixed at load time. So any change here, including adding a new agent outright, needs a
session/plugin restart before a probe can test it, and a probe run in the same session as the edit
measures the old file (or finds no file at all). Don't read such a run as the instruction being
ignored.

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

# dotnet-perf-mcp-probe and dotnet-perf-raw-probe

Exist to solve one specific methodology problem in `dotnet-performance`: measuring the MCP tools
against `grep`/`Read` in a single Claude instance means the same context designed the outcomes,
answered them through the MCP tools, *and then* played the raw route — already knowing every answer.
A raw-route guess informed by having just seen the MCP answer is not what a session without this
plugin actually produces, and no amount of instructing "don't look at the answer" closes that gap
reliably, because the knowledge is already sitting in context whether or not it's used deliberately.

The fix is two independently-spawned agents, neither with any memory of the parent conversation:
`dotnet-perf-mcp-probe` has the dotnet-toolkit MCP tools plus `Read` restricted (by instruction) to
one file, `dotnet-perf-raw-probe` has only `Read`/`Grep`/`Glob`/`Bash` — **tool absence for the actual
route being measured, not a hook-blocked path either is told to avoid.** `Grep`/`Glob` aren't gated
by any `PreToolUse` hook in the first place (`docs/design/hooks.md` says so directly), so
`dotnet-perf-raw-probe` needs no guard suspension for most questions; only the ones that require
opening a file need the window down, and even then only for the fraction of its run that touches
`Read`. `dotnet-perf-mcp-probe` needs no guard state at all — its one `Read` target isn't a `.cs`
file, so nothing about it is gated regardless.

**Deliberately not self-contained, unlike this plugin's other two agents — but split into two
sources, not one.** Both files carry only their identity and their one hard constraint (which tool
family is missing, and — for the MCP probe — that `Read` covers exactly one file). The **procedure
and output format** live in `skills/dotnet-performance/performance_protocol.md`, one file both agents
`Read` at the start of every run, rather than either agent file inlining a copy or
`dotnet-performance` re-typing identical instructions into two separate prompts where a future edit
could update one and miss the other. The **question list** stays in `dotnet-performance`'s invoking
prompt, sent byte-for-byte identical to both agents except for the one line naming the tool family —
it's per-run content a static file can't hold. This is the opposite of `dotnet-explore`'s and
`dotnet-code-review`'s design, and deliberately so: those two exist to do a real job well on their
own, so inlining their procedure is the right call; these two exist only to be interchangeable except
for one variable, so the procedure has to live in exactly one place or the two copies would drift and
the comparison would stop isolating that one variable.

**Why not reuse `dotnet-explore` for the MCP side?** Its own report format (`Target`/`Blast
radius`/`Affected files`/…) doesn't match what a raw-tool agent would naturally produce, so using it
means reconciling two different reporting shapes after the fact rather than comparing like for like.
It also carries `Read` (for `docs/tools/*.md`), a stray variable this comparison doesn't need. Keeping
`dotnet-explore` unmodified and building a purpose-fit pair instead was cheaper than bending an agent
whose real job is different from a benchmark instrument's.

**Model and cost.** Haiku for both, matching `dotnet-explore` — these are lookup tasks with little
judgment required, and running the same probe repeatedly should stay cheap. A first head-to-head (8
blind questions, `dotnet-explore` standing in for the not-yet-built MCP probe) put the MCP side's
total agent cost at 37,442 tokens over 11 tool calls against the raw side's 58,034 tokens over 25 —
the fixed agent-bootstrap cost is real on both sides and should be reported alongside any per-question
token count, not folded into it silently. Full numbers and correctness findings:
`.claude/dotnet-toolkit/perf/2026-08-11-dotnet-toolkit.md`, "Run 2" (predates the dedicated MCP probe;
re-run with the actual pair supersedes those specific numbers, not the methodology).

**Never launch either for a real task.** `dotnet-perf-mcp-probe` has no raw tools and no report
format of its own, so it produces a worse-informed `dotnet-explore`; `dotnet-perf-raw-probe` has no
MCP tools at all.
