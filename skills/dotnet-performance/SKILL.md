---
name: dotnet-performance
description: Use when asked what dotnet-toolkit actually costs compared to doing the same work with plain tools — "is this plugin worth it", "benchmark the toolkit against grep", "how much does search_index really save over Grep/Read", "measure the MCP tools against cat/ls/find", "does this pay for itself on Windows/PowerShell". Builds one question matrix and sends it, verbatim and blind, to two dedicated subagents — dotnet-perf-mcp-probe (only the MCP tools) and dotnet-perf-raw-probe (only Grep/Glob/Read/Bash, guard hooks suspended so it can reach .cs files at all) — so neither agent's answer is informed by having seen the other's, or by the orchestrator's own prior exploration. Reports tokens and calls per route, states which numbers are exact, and reports every outcome where the raw route won or the two agents converged on the same wrong answer (a question-design finding, not a route one). Never edits .cs, and always restores the guards before it finishes.
---

# Measuring what this plugin costs against not having it

`dotnet-selfeval` asks whether the *tools* are efficient against each other — which route inside the
plugin is cheapest. This skill asks the prior question: **is the plugin cheaper than not using it, in
this repo, on this kind of task?** Those are different questions and they can disagree. A tool can be
the best route inside the plugin and still lose to `grep` on a repo of forty files.

The claim under test is the one in the always-loaded rule: that text search gives *wrong* answers on
C#, not merely slower ones, and that the MCP tools cost fewer tokens than the file reads they replace.
The first half is a correctness claim and the second is a cost claim. **Measure them separately** — a
route that is cheaper *and* wrong is not a win, and reporting a single blended number hides exactly
that case.

## Why two subagents, not the orchestrator playing both routes

A single Claude instance that designs the outcomes, answers them through the MCP tools, and *then*
plays the raw route is contaminated by construction: by the time the raw route runs, the answer is
already sitting in context, whether or not it's used deliberately. A raw-route guess informed by
having just seen the MCP answer is not what a session without this plugin actually produces, and no
amount of self-instruction to "not look at the answer" closes that gap reliably.

The fix is two independently-spawned agents, neither with any memory of this conversation or of the
other's run:

- **`dotnet-perf-mcp-probe`** — only this repo's dotnet-toolkit MCP tools, no `Read`/`Grep`/`Glob`/`Bash`.
- **`dotnet-perf-raw-probe`** — only `Read`/`Grep`/`Glob`/`Bash`, no MCP tools.

Both are otherwise identical, deliberately minimal agent files: neither bakes in its own procedure or
output format. Instead, both read the same `skills/dotnet-performance/performance_protocol.md` —
one file, read by both agents, rather than this skill re-typing identical instructions into two
separate prompts where a future edit could update one copy and not the other. `dotnet-perf-mcp-probe`
carries `Read` for exactly this one file (never a `.cs` file, never anything else); `dotnet-perf-raw-probe`
already has `Read` as one of its four real tools. **The question list itself is per-run, so it stays in
the prompt, not the protocol file** — see Step 2. Don't reuse `dotnet-explore` as one of the two
probes: its own report format doesn't match a raw-tool agent's, and its `Read` fallback (for
non-`.cs` docs) is a stray variable this comparison doesn't need. `dotnet-explore` keeps doing its
real job unmodified — including a supporting role in Step 1 below, distinct from being a probe.

**Neither agent needs full guard suspension for its whole run.** `Grep`/`Glob` are matched by no
`PreToolUse` hook at all (`docs/design/hooks.md` says so directly) — only `Read`, `Edit`/`Write`, and
`Bash` reads of `.cs` files are gated. So `dotnet-perf-raw-probe` only needs the window open for
whichever questions require opening a file, and `dotnet-perf-mcp-probe` is immune to guard state
entirely — it has no gated tool in its grant regardless of whether guards are up or down. Step 3
still suspends for the raw probe's whole run rather than micromanaging which specific questions need
it; that's a simplification worth the small extra exposure, not a requirement.

**A freshly created or edited agent file is not visible to the current session.** The harness
snapshots the agent registry when the plugin loads, not per subagent spawn (`docs/design/agents.md`
has the confirmed details). If either probe agent was just added or changed, it needs a session
restart before this skill can launch it — a `not found` error on either agent name is that, not a
setup mistake.

## Step 0 — establish the ground

1. `workspace_status`. Record `root`, the solution, file and type counts, and whether the workspace is
   `loaded` or `degraded`. **A degraded workspace invalidates the whole run** — the MCP side would be
   answering from a partial compilation, so it would look cheap for the wrong reason. Fix and reload
   first. Also record `pluginRoot` from this same response — Step 2 needs it to point both probe
   agents at `performance_protocol.md`.
2. Record the repo's size honestly: file count, type count, and the size of the largest `.cs` file.
   These are the axes the answer actually depends on, and a result reported without them is not
   transferable to any other repo.
3. `get_retrieval_metrics` once, to fix the baseline the MCP probe's tagged calls are measured from.

## Step 1 — build the question matrix

**Survey the repo for real targets before writing questions, and delegate that survey rather than
doing it yourself.** A question built around a symbol that turns out not to exist, or that isn't
actually the shape the outcome needs (no partial class in a small repo, say), is discovered wrong
only after both probes have already run. Launch `dotnet-explore` with a brief naming the outcome
types below and asking it to report real candidates for each — a partial class, a private method
with a common-word name, an interface with at least one implementer, and so on — with `symbolId`s
and locations. This is `dotnet-explore` doing its actual job (mapping the codebase for a caller about
to act on it), not a third probe: its findings go to *you*, never to either probe agent, so knowing
the real targets while writing neutral questions is normal design work, not contamination. The
contamination risk this whole skill exists to avoid is specific to the two agents *answering* the
questions, not to the orchestrator *knowing* what a good question looks like.

Score **a stated outcome**, the way `dotnet-selfeval`'s 3a does — never a single call against a single
command. "Find the definition of `X`" is an outcome; `search_index` is not.

Pick outcomes that span the range where the answer plausibly changes sign, and say which is which:

| Outcome | Where the plugin should win, and why |
|---|---|
| Find a type by exact name | **Narrowly, or not at all** — `grep -rn "class Foo"` is one cheap call. Expect this row to be close, and report it honestly if the raw route wins. |
| Find a symbol whose exact name you don't know | Ranked hits vs. a grep that needs several guesses; count *every* guess the raw route took, including the ones that returned nothing. |
| Read one method out of a 2000-line file | The file read has no way to return less than the file. |
| Read a type split across partial files | The raw route returns one fragment **and no signal the rest exists** — a correctness finding, not a cost one. |
| Find every caller of a method | The raw route cannot see interface, virtual or delegate dispatch. Verify against the MCP answer and count what text search *missed*, not just what it cost. |
| Find implementers of an interface | Same shape as callers. |
| Confirm a symbol is unused before deleting it | The raw route's answer is unsound here; report that rather than a token ratio. |
| Callers of a method with a short or common name | A false-hit stress test: string literals, unrelated identifiers, or a same-named method in a different class can all make raw search's answer wrong, not just noisy. |

Eight to twelve outcomes is enough. Fewer than six cannot show a crossover; more than about twelve
spends more context than the finding is worth.

**Phrase each outcome as a neutral question, not a leading one.** "Find `SymbolStore`" is fine — the
outcome itself names the target. "Find the class that stores X" for a fuzzy-name outcome must not
contain the real class name anywhere in the wording, or both agents inherit a hint the real session
wouldn't have. Write the question once; both agents get the identical text.

## Step 2 — one shared prompt, sent to both agents

Construct a single prompt template covering the whole matrix, and send it to both agents essentially
unchanged — the only difference is the one line naming which tool family that agent has (each
agent's own file already states its hard constraint, so this is confirmation, not new information).
The numbered question list must be byte-for-byte identical between the two prompts, because that
identity is what makes the resulting comparison mean something.

**Point both agents at the protocol file instead of restating its contents.** Resolve
`pluginRoot` from Step 0's `workspace_status` call and give both prompts the same instruction:
*"Read `<pluginRoot>/skills/dotnet-performance/performance_protocol.md` first — it has your exact
output format and how to play each question."* That file, not this skill's prose, is where the
output format and the "play it straight" instructions actually live; a change to either belongs
there, in the one copy both agents read, not duplicated into this step.

Give the MCP probe a `taskId` to pass on every call (`perf_mcp_<date>`), stated in its copy of the
prompt. The raw probe has no equivalent — nothing meters `Read`/`Grep`/`Bash`.

**Launch order:** `dotnet-perf-mcp-probe` first, needs no guard suspension. Then
`set_hook_guards(state: "suspend", minutes: <enough for the whole raw pass>)`, launch
`dotnet-perf-raw-probe`, then restore (Step 5). Foreground or background both calls as convenient —
what matters is the guard window brackets only the raw probe's run.

## Step 3 — count, and say which numbers are exact

This is the step where a careless run produces a confident wrong number.

- **Both totals are now exact, not estimated.** Each `Agent` call reports the spawned agent's real
  `subagent_tokens` and `tool_uses` in its own result — that is the true cost of that route's run,
  bootstrap and reasoning included, for both sides equally. This replaces the old byte-per-token
  estimate for the raw side; nothing about a raw-tool response needs guessing at anymore, because the
  number being reported is the agent's actual usage, not a reconstruction from its output bytes.
- **A second, finer number exists only for the MCP side:** `get_retrieval_metrics(groupBy: "task",
  taskIds: ["perf_mcp_<date>"])` isolates the MCP tool-response bytes alone, excluding the probe
  agent's own bootstrap/reasoning overhead. This is the number that answers "how much does the
  *retrieval call itself* cost" — a different, narrower question than "what did this whole route cost
  in production." Report both, and say which is which; don't let a reader assume they're the same
  measurement.
- **The raw side has no equivalent finer number.** `Grep`/`Read`/`Bash` aren't metered at all, so the
  raw probe's total agent cost is the only number available for it — which is fine, since it's exact,
  just not decomposable per question the way the MCP side's tagged calls are.
- **Count calls too, and separately.** A route that is cheaper in tokens but takes many more round
  trips may still be the worse one, and only the call count shows it. Each agent's own **Calls made**
  section is the source for this, not a guess.
- **Reconcile the self-reported total against the true one, for both agents, every run.** Each probe's
  final `Total tool calls` line (from `performance_protocol.md`) is its own tally of its per-question
  **Calls made** lists; the `Agent` call's own `tool_uses` is ground truth. Report both numbers for
  both probes. A gap means calls went missing from the per-question logs above — say which questions'
  **Calls made** lists look compressed (multiple same-tool lines collapsed into one), not just that a
  gap exists. This has recurred across runs (the raw route undercounted itself by roughly half on
  2026-08-11 and again on 2026-08-12) — treat a clean match as worth noting too, not just a mismatch.

## Step 4 — check correctness before cost

Compare the two agents' answers question by question:

- **Missed hits** — dispatch the raw agent's text search cannot see. Report the specific symbols
  missed.
- **False hits** — matches inside comments, strings, or unrelated identifiers.
- **Silent truncation** — a capped search that gave no signal there was more.
- **Partial-class fragments** — one part returned as though it were the whole declaration.
- **Undercounting** — a route that reached the right file and the right scope but still enumerated an
  incomplete set of real matches. This is a subtler failure than a false hit and only shows up once
  the obvious ambiguity is already avoided — don't stop checking a "right verdict" answer for a
  complete one.

**If both agents land on the same wrong answer, that is a finding about the question, not about
either route.** A vaguely-worded outcome that a competent tool-assisted agent and a competent raw
agent both misread the same way means the wording was underspecified, not that either route failed —
score it as a wash and say so, rather than letting it inflate or deflate either side's tally.

A route that was cheaper *and* missed hits (or undercounted, or shared a wrong answer that happens to
look like a "win") is reported as **wrong, not cheap**. Put correctness before the cost table in the
report, because a reader who sees the ratio first will remember the ratio.

**Write the correctness verdict against the question number, not a restated question.** Step 5's
report carries the exact question text once, verbatim, in its own section — repeating it per row in
Correctness (as an "Outcome" column, say) is the same fact twice for no reader benefit. Reference `Q1`,
`Q2`, etc. and spend the words on the verdict instead.

## Step 5 — report

**Every run is independent. Don't read `.claude/dotnet-toolkit/perf/` before or during a run, and
don't append to a file that's already there.** A prior report was written by a different question
matrix, a possibly-different repo state, and — if it predates a protocol change — a possibly-different
cost methodology; treating it as context for this run's questions or numbers is exactly the kind of
contamination Step 1 already goes out of its way to avoid on the probe side. Write a fresh file every
time, even if one already exists for today's date. Only read or extend an existing report when the
user explicitly asks to — "compare this against the last run," "update that report" — and even then,
say plainly in the new report which prior run you're comparing against and why, rather than quietly
merging numbers from two different methodologies into one table.

Write to `.claude/dotnet-toolkit/perf/<date>-<HHmmss>-<repo>.md` (the time component is what keeps
same-day runs from colliding into an append):

```
# dotnet-toolkit performance — <date>

Specimen: <root> · <solution> · <n> projects · <n> files, <n> types · largest .cs: <n> lines
Cost basis: both totals are exact subagent_tokens from the Agent call; the MCP side additionally
reports exact tool-response-only tokens via get_retrieval_metrics(taskId), which the raw side has
no equivalent for.
Guards: suspended <start>–<end> (scoped to this session), restored <how>

## Questions
<the numbered question list, verbatim — byte-for-byte the same text both agents received per Step 2.
This is the one place the question text appears; Correctness and Cost below reference it by number
rather than repeating it>

## Correctness
<per question (by number — see Questions above, don't restate the text): what the raw agent missed,
invented, undercounted, or truncated — "matched" — or "both agents converged on the same wrong
answer" (a question-design finding)>

## Cost
| Question | MCP probe (calls, total tokens, tool-only tokens) | Raw probe (calls, total tokens) | Which route won |
|---|---|---|---|

Self-reported vs. true tool_uses: MCP probe reported <N> (`Total tool calls`) against a true
`tool_uses` of <N>; raw probe reported <N> against a true <N>. <If either gap is non-trivial, name
which questions' **Calls made** lists look compressed, per Step 3 — don't just note the gap exists.>

## Where the raw route wins
<the honest list — if it is empty, say why you believe that rather than just asserting it>

## What this does and does not transfer to
<the size axes from Step 0, and which rows would plausibly flip on a much smaller or larger repo>
```

**The "where the raw route wins" section is the one that makes this report worth reading.** A
benchmark of a tool against its own alternative, written by the tool's own plugin, is worthless if it
never finds a row that goes the other way. If nothing did, say what you checked and why you believe
the result — an empty section with no reasoning reads as a run that was not really trying.

## Step 6 — restore the guards

`set_hook_guards(state: "restore")`, then `workspace_status` to confirm the `hookGuards` line is gone.
Report both in the run's `Guards:` line. If `restore` reports that `DOTNET_TOOLKIT_DISABLE_HOOKS` is
holding them open, say so in the report — that is an environment the next session inherits, and only a
server restart clears it.

## Boundaries

- **Never edits `.cs`.** Not through `validate_patch`, and emphatically not through the raw `Edit` the
  suspension makes reachable. The suspension is for *reading* like an unequipped session; an edit made
  through it would land unrecorded, which is the exact failure the guards exist to prevent.
- **Findings are about the plugin**, never about the consuming repo's code.
- **Each run stands alone.** No reading prior `.claude/dotnet-toolkit/perf/` reports for context, no
  appending to one, unless the user explicitly asked for a comparison against a named prior run.
- **Restores the guards even when the run fails.** A run abandoned halfway still owns the suspension
  it took.
- **`dotnet-perf-mcp-probe` and `dotnet-perf-raw-probe` are launched only from here.** Neither carries
  its own question list, and neither has anywhere else to learn the output format except
  `performance_protocol.md` — invoking either outside this skill's prompt leaves it with a question
  list to answer but no shared format to answer in.
