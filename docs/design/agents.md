# Agent design notes

> **Maintainer-facing. No agent or skill reads this file.** Each agent definition is self-contained
> and authoritative; if this page disagrees with `agents/<name>.md`, the agent file is right and this
> one is stale. Kept for the rationale that is derivable nowhere else.

This plugin ships six subagents, all read-only in intent (`dotnet-guard-redteam` included, despite its
broad tool grant — see its own section for why):

- **`dotnet-code-review`** (`agents/dotnet-code-review.md`) — a **validation layer** that checks code
  against the standards in `standards/`, not a source of standards itself. Runs *after* code exists.
  Self-contained.
- **`dotnet-explore`** (`agents/dotnet-explore.md`) — a **navigator** that maps a task onto the
  codebase's symbols and reference graph. Runs *before* code is written. Self-contained. See the
  section at the end.
- **`dotnet-perf-full-probe`, `dotnet-perf-mcp-probe` and `dotnet-perf-raw-probe`**
  (`agents/dotnet-perf-full-probe.md`, `agents/dotnet-perf-mcp-probe.md`,
  `agents/dotnet-perf-raw-probe.md`) — a **benchmark instrument trio**, not general-purpose agents:
  identical except for tool grant and guard state (both MCP + raw tools at the default guard state vs.
  MCP tools + `Read` restricted to one file under a forced block vs. `Read`/`Grep`/`Glob`/`Bash` only
  under a suspension), and deliberately **not** self-contained — none carries its own procedure or
  output format. All three read the same `skills/dotnet-performance/performance_protocol.md` for
  those; the per-run question list still comes from the invoking prompt, since it changes every run
  and a static file can't hold it. `dotnet-performance` is their only sanctioned launcher, and it runs
  them **sequentially**, never concurrently — guard state is one global switch per repo, so two probes
  in flight would either share a state neither asked for or race to change it. See the section at the
  end.
- **`dotnet-guard-redteam`** (`agents/dotnet-guard-redteam.md`) — an **enforcement tester**: tries a
  fixed battery of known and improvised bypasses against `guard-cs-edit`/`guard-cs-read`/
  `guard-cs-bash-read` with the guards left armed, and reports which got through. `dotnet-guard-audit`
  is its only sanctioned launcher. See the section at the end.

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

A related note: the `guard-cs-read` hook only *advises* against `Read` on `.cs` files a project
compiles (it stopped blocking on 2026-08-21) and `PreToolUse` hooks fire for subagents too, so a
whole-file `Read` would technically succeed here. The agent is still told to use MCP retrieval instead
on cost grounds, not because the alternative is blocked — which is why the batching above matters
regardless of hook state.

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
Hard boundaries name what is off-limits within that: no `.cs` file — the `guard-cs-read` hook is
advisory only and no longer a backstop, so this instruction is the sole boundary — and specifically
not `validate_patch.md`/`rename_symbol.md`, which document a path it has no tools for. No `Grep`/
`Glob`: a text search over C# is the thing this agent exists to replace, and non-C# files are out of
scope by design.

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

# dotnet-perf-full-probe, dotnet-perf-mcp-probe and dotnet-perf-raw-probe

Exist to solve one specific methodology problem in `dotnet-performance`: measuring the MCP tools
against `grep`/`Read` in a single Claude instance means the same context designed the outcomes,
answered them through the MCP tools, *and then* played another route — already knowing every answer.
A route played by an agent who just saw the answer is not what a session without that head start
actually produces, and no amount of instructing "don't look at the answer" closes that gap reliably,
because the knowledge is already sitting in context whether or not it's used deliberately.

The fix is three independently-spawned agents, none with any memory of the parent conversation or of
the others' runs. Originally there were two — `dotnet-perf-mcp-probe` (MCP tools only) and
`dotnet-perf-raw-probe` (raw tools only) — each an artificially restricted extreme: a ceiling and a
floor. `dotnet-perf-full-probe` was added 2026-08-22 to close the gap between them: neither extreme
answers "what does a real, unrestricted session with this plugin installed actually do," since a real
session is never tool-restricted and never runs under a guard state nobody chose. It carries both tool
families and runs at the guards' plugin-shipped default (advisory reads, blocking writes, nothing
suspended or forced) — the practice measurement the other two, by design, cannot produce.

`dotnet-perf-mcp-probe` has the dotnet-toolkit MCP tools plus `Read` restricted (by instruction) to one
file; `dotnet-perf-raw-probe` has only `Read`/`Grep`/`Glob`/`Bash`; `dotnet-perf-full-probe` has both
families at once — **tool absence for the actual route being measured, not a hook-blocked path either
extreme is told to avoid.** Guard state is what keeps each extreme honest instead of tool grant alone:
`dotnet-perf-raw-probe` runs under `set_hook_guards(state: "suspend")` so even its `Read` calls get no
advisory nudge, simulating a session with no plugin installed at all; `dotnet-perf-mcp-probe` runs
under `set_hook_guards(state: "block")` so its one narrow `Read` allowance (for
`performance_protocol.md`) hard-fails loudly if it ever strays onto a `.cs` file, rather than quietly
contaminating the "MCP-only ceiling" measurement with an advisory-only read nobody would notice.
`dotnet-perf-full-probe` needs no guard-state change at all — the default state **is** the condition
being measured.

**Sequential, never concurrent, because guard state is one switch per repo.** Two probes running at
once would either share a suspend/block/default state neither of them asked for, or race to change it
out from under each other — either way the run is uninterpretable. `dotnet-performance` launches
`dotnet-perf-full-probe` first (the only one needing no state change, so it goes before either state
change happens), then brackets `dotnet-perf-raw-probe` with `suspend`→run→`restore` and
`dotnet-perf-mcp-probe` with `block`→run→`restore`, verifying at each transition that the state change
actually reached the hook processes (see `docs/design/hooks.md`'s "session id goes stale" finding, the
reason neither `set_hook_guards`' own response nor `workspace_status` is trusted for this on its own).

**Deliberately not self-contained, unlike this plugin's other agents — but split into three sources,
not one.** Each file carries only its identity and its one hard constraint (which tool family it has,
and — for the two restricted probes — the guard state `dotnet-performance` puts it under). The
**procedure and output format** live in `skills/dotnet-performance/performance_protocol.md`, one file
all three agents `Read` at the start of every run, rather than any agent file inlining a copy or
`dotnet-performance` re-typing near-identical instructions into three separate prompts where a future
edit could update one and miss the others. The **question list** stays in `dotnet-performance`'s
invoking prompt, sent byte-for-byte identical to all three agents except for the one line naming each
one's tool family and guard state — it's per-run content a static file can't hold. This is the
opposite of `dotnet-explore`'s and `dotnet-code-review`'s design, and deliberately so: those two exist
to do a real job well on their own, so inlining their procedure is the right call; these three exist
only to be interchangeable except for the variable each one holds constant, so the procedure has to
live in exactly one place or the copies would drift and the comparison would stop isolating those
variables.

**Why not reuse `dotnet-explore` for the MCP side?** Its own report format (`Target`/`Blast
radius`/`Affected files`/…) doesn't match what a probe agent would naturally produce, so using it means
reconciling different reporting shapes after the fact rather than comparing like for like. It also
carries `Read` (for `docs/tools/*.md`), a stray variable this comparison doesn't need. Keeping
`dotnet-explore` unmodified and building purpose-fit instruments instead was cheaper than bending an
agent whose real job is different from a benchmark instrument's.

**Model and cost.** Haiku for all three, matching `dotnet-explore` — these are lookup tasks with
comparatively little judgment required (`dotnet-perf-full-probe`'s tool-choice decision is the closest
any of them comes to real judgment, and it is still a per-question local call, not the sustained
reasoning `dotnet-code-review` or `dotnet-guard-redteam` need), and running the same probe repeatedly
should stay cheap. Pinning all three to the same model also keeps model choice from confounding the
token comparison the way it would if `dotnet-perf-full-probe` inherited the caller's model instead. A
first head-to-head (8 blind questions, `dotnet-explore` standing in for the not-yet-built MCP probe,
predating the third probe entirely) put the MCP side's total agent cost at 37,442 tokens over 11 tool
calls against the raw side's 58,034 tokens over 25 — the fixed agent-bootstrap cost is real on every
side and should be reported alongside any per-question token count, not folded into it silently. Full
numbers and correctness findings: `.claude/dotnet-toolkit/perf/2026-08-11-dotnet-toolkit.md`, "Run 2"
(predates the dedicated MCP probe and the three-probe methodology; re-run with the actual trio
supersedes those specific numbers, not the rationale).

**Never launch any of the three for a real task.** `dotnet-perf-mcp-probe` has no raw tools and no
report format of its own, so it produces a worse-informed `dotnet-explore`; `dotnet-perf-raw-probe` has
no MCP tools at all; `dotnet-perf-full-probe` has both, but no report format or question list of its
own either — for real exploration, `dotnet-explore` is still the right agent.

# dotnet-guard-redteam

Exists because a guard hook's own doc comment claiming it blocks (or advises on) a class of call is not
the same claim as the hook actually doing so on every path a caller might take — and this repo has
already had that gap found for it once, by a consuming repo rather than by this one. The 2026-08-20
finding (`Hooks/BashCommandScanner.cs`'s `Tokenize` splitting on whitespace, so a compiled `.cs` path
containing a space was invisible to `guard-cs-bash-read`) was discovered incidentally, during an
unrelated performance run, by a user challenging that run's validity rather than by a process built to
look for exactly this. This agent is that process.

**Read-only in intent, not in tool grant — the one place this plugin's "read-only" framing is deliberately
inverted.** Every other subagent above is either genuinely incapable of writing by tool grant
(`dotnet-explore`, `dotnet-perf-mcp-probe`, `dotnet-perf-raw-probe`), capable only because a memory
namespace requires it (`dotnet-code-review`), or capable via `Bash` but kept off that path by the same
`guard-cs-bash-write` hook a normal session relies on, plus its own instruction never to attempt one
(`dotnet-perf-full-probe`, which runs at the default guard state precisely because that state is what
is being measured). This agent is granted `Edit`/`Write`/`Bash` **on purpose**, because testing whether
an edit can reach disk
unguarded requires actually attempting one. What keeps it read-only in effect is instruction, not
capability: every write-shaped attempt must be reverted in the same turn, verified by re-reading the
file, before moving to the next attempt — the agent file states this as a hard boundary, not a
suggestion, precisely because nothing else enforces it.

**Runs with the guards armed — the mirror image of the perf probes' precondition.**
`dotnet-perf-raw-probe` needs `set_hook_guards(state: "suspend")` before it can reach a `.cs` file at
all; this agent must run *without* any suspension in force, because a bypass attempt made while guards
are down proves nothing about enforcement. `dotnet-guard-audit` checks `workspace_status` for a
`hookGuards: SUSPENDED` line before launching, the same call `dotnet-performance` makes to prove a
suspension reached the hooks — used here to prove the opposite state.

**Why its battery includes write-shaped attempts through `Bash`, and why that half matters most.**
`guard-cs-edit` matches only the `Edit`/`Write`/`NotebookEdit` tool names (`hooks/hooks.json`'s
`PreToolUse` matcher for it, same as `docs/design/hooks.md` documents for the read guards), and through
2026-08-21 nothing in the shipped hook set watched `Bash` for a *write* the way `guard-cs-bash-read`
watches it for a *read* — `sed -i`, `perl -i -pe`, or a shell redirect into a compiled file were none
of them intercepted by name. Item 10 of the agent's battery is what found this: a run that day
confirmed `echo '...' >> Foo.cs` reached disk unblocked, with no compile check and no development-log
entry, which is now closed by `guard-cs-bash-write` (`docs/design/hooks.md`). The battery still runs
item 10 on every audit rather than assuming the fix holds — a scanner limitation regressing, or a new
write shape the scanner never covered, is exactly the kind of gap this agent exists to keep
re-discovering rather than to have found once and stopped checking for.

**Model: unset (inherits the caller's).** Unlike the perf probes, which are pure lookup tasks that
tolerate Haiku, constructing and correctly judging the outcome of a dozen quoting/path/tool-name
variations is closer to `dotnet-code-review`'s judgment load than to `dotnet-explore`'s navigation, so
it is not pinned to a cheaper model.

**Self-contained, unlike the perf-probe trio.** The perf probes split their procedure out into
`performance_protocol.md` because three agents need byte-identical instructions for the comparison to
isolate the variable each one holds constant. This agent has no sibling to stay identical to, so its
full battery, boundaries, and report format live in its own file, the same way `dotnet-explore`'s and
`dotnet-code-review`'s do.
