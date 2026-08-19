---
name: dotnet-performance
description: Use when asked what dotnet-toolkit actually costs compared to doing the same work with plain tools — "is this plugin worth it", "benchmark the toolkit against grep", "how much does search_index really save over Grep/Read", "measure the MCP tools against cat/ls/find", "does this pay for itself on Windows/PowerShell". Builds one question matrix and sends it, verbatim and blind, to two dedicated subagents — dotnet-perf-mcp-probe (only the MCP tools) and dotnet-perf-raw-probe (only Grep/Glob/Read/Bash, guard hooks suspended so it can reach .cs files at all) — so neither agent's answer is informed by having seen the other's, or by the orchestrator's own prior exploration. Both routes are metered by one instrument — a PostToolUse hook that fires on harness dispatch, so Grep and get_symbol are counted the same way — and reported as request tokens (what the model generated, the dear side) against response tokens (what was injected into its context), plus calls per route. States which instrument produced each number, and reports every outcome where the raw route won or the two agents converged on the same wrong answer (a question-design finding, not a route one). Never edits .cs, and always restores the guards before it finishes.
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

**Suspension does not stop the metering.** `meter-tool-call` is a `PostToolUse` hook and is
deliberately exempt from `GuardSuspension` — it observes rather than withholding, and a benchmark
suspends the guards precisely so it can measure the *unguarded* route, which is not measured at all if
suspending also silences the measurement. Step 3 depends on this: the meter is what makes both routes
comparable.

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
3. `get_retrieval_metrics` once, to fix the baseline both probes are measured from. **Check that the
   `harness` block is present.** Absent means the `meter-tool-call` hook has recorded nothing, and this
   run has no instrument that sees both routes — the two would again be measured by different means,
   the flaw Step 3's whole design exists to remove. The usual cause is a server started before the hook
   was registered in `hooks.json`; restart it and begin again rather than reporting the run.

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
| Read a type split across partial files | The raw route returns one fragment **and no signal the rest exists** — a correctness finding, not a cost one. **Expect this row to be closer than it sounds**: where every part carries identical declaration text, one `grep` enumerates them all, and on 2026-08-17 it beat an MCP route that read ranked `search_index` hits as an enumeration and reported 5 of 13. Ask *which files* rather than *what is in it* if you want the enumeration failure specifically, and score the MCP route on whether it reached `declarationSites`. |
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
prompt. The raw probe still has no equivalent — a `taskId` is an MCP argument, and `Read`/`Grep`/`Bash`
take none. That asymmetry no longer decides the comparison, though: the `taskId` now buys only the
server-side view of the MCP responses, while the number both routes are actually compared on comes
from the harness meter, which needs nothing from either prompt (Step 3).

**Launch order:** `dotnet-perf-mcp-probe` first — it needs no guard suspension and is immune to guard
state either way. Then suspend, launch `dotnet-perf-raw-probe`, **wait for it to return**, then
restore (Step 6). Three rules make that window correct rather than approximately correct:

- **Pass `minutes` explicitly**, sized to the whole raw pass with margin (eight to twelve outcomes has
  taken 5–15 minutes). The default is 30 minutes and the cap is 4 hours; taking the default means the
  report cannot honestly state the window it ran under, because nobody chose it.
- **Join before restoring.** Restoring while the raw probe is still running re-arms the guards
  underneath it: its next `Read` of a `.cs` file is denied, it quietly falls back to whatever it can
  still reach, and the run ends up measuring a hobbled raw route without saying so. If the probe was
  backgrounded, wait for its result before calling `restore` — the restore is not a fire-and-forget
  cleanup step, and it is the one ordering mistake that silently corrupts the comparison.
- **Never let the expiry do the restoring.** A suspension that lapses on its own leaves no record of
  when it ended, and Step 6's confirmation is what the report's `Guards:` line quotes.

**Then prove the suspension actually reached the hooks, before launching the raw probe.** Do not
take `set_hook_guards`' success string for it, and do not take `workspace_status`' `hookGuards:
SUSPENDED` line for it either. Both are the *server's* view of a file it wrote; neither observes a
hook process. On 2026-08-13 both reported a suspension that was not in force, the raw probe was
denied on its first `.cs` read, and the run was lost.

The positive control is one call: read a `.cs` file the way the raw probe would — `Bash` with
`head -1` on any file under `src/` — and confirm it is **not** denied.

- **Not denied** → the suspension is real; launch the raw probe.
- **Denied by a `PreToolUse` guard** → the suspension did not reach the hooks. **Stop.** Do not
  launch the raw probe, do not report numbers, and restore before doing anything else. A run whose
  raw route is blocked produces a comparison against a route that was prevented from working,
  which is worse than no comparison — and the denial is the only honest signal you will get, since
  the two instruments above will keep insisting the guards are down.

The known cause is a session-id mismatch between the long-lived MCP server and the live session
(`docs/design/hooks.md`). Restarting the MCP server clears it. Verify rather than assume the fix
holds: this check costs one call and is the only thing standing between a silent failure and a
published wrong number.

## Step 3 — count, and say which instrument produced each number

This is the step where a careless run produces a confident wrong number. Three instruments are
available, they answer three different questions, and the report must name which produced each figure.

| Instrument | Covers | Answers |
|---|---|---|
| `get_retrieval_metrics`'s `harness` block, `byAgent` rows | **both routes**, every tool the harness dispatched | What did each route's tool calls cost, split into request and response tokens? |
| Each `Agent` call's own `subagent_tokens` / `tool_uses` | both routes, whole run | What did the whole route cost in production, bootstrap and reasoning included? |
| `get_retrieval_metrics(taskIds: ["perf_mcp_<date>"])` | **MCP only**, server-side | What did the MCP responses alone cost, as the server itself measured them? |

- **The `harness` block is the comparison.** It is the only instrument that measures both routes with
  the same code on the same payload: the `meter-tool-call` `PostToolUse` hook fires on harness
  dispatch, independent of which tools an agent's grant contains, so a `Grep` is metered exactly as a
  `get_symbol` is. Read it once after both probes return and take the `byAgent` rows —
  `dotnet-perf-mcp-probe` and `dotnet-perf-raw-probe` appear under their own `agent_type`, so neither
  probe has to label itself and no self-report has to be trusted. Before this existed, each route was
  measured by a different mechanism, which is not a comparison however carefully it is presented.
- **Report both directions; never blend them into one number.** `responseTokens` is what the call
  loaded into the model's context (**input** tokens); `requestTokens` is what the model had to
  generate to make the call (**output** tokens). Output runs roughly **5× dearer** — Opus 5 is $5/$25
  per MTok and Haiku 4.5 $1/$5 — so `requestTokens × 5 + responseTokens` is the comparable unit. The
  caller applies that weighting, not the server, which has no idea which model is running.
- **`responseTokens` is the context-bloat number, and it deserves its own line.** The plugin's central
  claim is about what lands in the context window, and this column is exactly that. Requests are
  usually small — a one-line command, a short argument list — so a route with more calls but smaller
  responses can inject far less context than a route with fewer, larger ones. A blended total hides
  precisely that.
- **Both token counts are approximations, and the block says so.** `tokenEstimator` names what
  produced them: `chars4` is `(length + 3) / 4` over the serialized payload. It is applied identically
  to both routes, so the *ratio* between them is sound while the absolute figures are not — state that
  rather than presenting them as exact.
- **`subagent_tokens` remains the production number.** It is exact and includes each agent's own
  bootstrap and reasoning, which the meter never sees. A route can meter cheaply and still cost more
  overall because it reasoned longer; report both and let neither stand in for the other.
- **Count calls too, and separately.** A route cheaper in tokens but taking many more round trips may
  still be the worse one. The meter's `calls` per agent is now the ground truth for this.
- **Reconcile three ways, every run.** Each probe's self-reported `Total tool calls` line, the meter's
  `calls` for that `agent_type`, and the `Agent` call's `tool_uses`. The meter and `tool_uses` should
  agree closely — a gap between *them* points at a metering failure. A gap between either and the
  self-report means calls went missing from the per-question **Calls made** lists, and you should name
  which questions look compressed (several same-tool lines collapsed into one). **Subtract the known
  offset before calling anything a discrepancy:** each probe's `Read` of `performance_protocol.md` is
  setup its own protocol tells it to leave out of the log, but the harness dispatched it, so the meter
  counts it — expect the meter to exceed each self-report by at least one on that account alone, plus
  any `workspace_status` readiness call the probe treats the same way. A gap of one or two is that; a
  gap of a third or a half is compression. The raw route has
  undercounted itself on every run so far — by roughly half on 2026-08-11, on 2026-08-12, and again on
  2026-08-17 (26 logged lines against 53 metered calls) — which is exactly why the self-report is no
  longer the instrument, only a cross-check on it. The protocol now tells both probes to keep the log
  as they go rather than reconstruct it at the end; if a run still shows that gap, report it as a
  standing property of self-reports rather than as this run's anomaly.
- **Never compute a ratio from the per-question table.** Those counts are self-reported, and on the raw
  side reliably too low, so a "calls per question" comparison built from them is a measurement of two
  agents' bookkeeping. The table is a *shape* signal — which questions took several tries, where a
  route flailed — and the report must label it as such. Every number a cost claim rests on comes from
  the meter or from `subagent_tokens`.

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
Cost basis: both routes metered by the same PostToolUse hook and read from get_retrieval_metrics'
harness block, as request/response tokens (<tokenEstimator> approximation, applied identically to
both); whole-route totals are exact subagent_tokens from each Agent call; the MCP side additionally
reports server-side response tokens via get_retrieval_metrics(taskId).
Guards: <the scope sentence set_hook_guards returned on suspend, verbatim> · suspended <start> for
<minutes>m · raw probe returned <time> · restored <the restore call's own response line, verbatim>

## Questions
<the numbered question list, verbatim — byte-for-byte the same text both agents received per Step 2.
This is the one place the question text appears; Correctness and Cost below reference it by number
rather than repeating it>

## Correctness
<per question (by number — see Questions above, don't restate the text): what the raw agent missed,
invented, undercounted, or truncated — "matched" — or "both agents converged on the same wrong
answer" (a question-design finding)>

## Cost

Aggregate — both routes, one instrument (the harness meter):

| Route | Calls | Request tokens (output) | Response tokens (input) | Weighted (req×5 + resp) | Whole-agent tokens |
|---|---|---|---|---|---|
| MCP probe | | | | | |
| Raw probe | | | | | |

**Context injected** — the response-token column — is the plugin's central claim, so state it on its
own line: <n> vs <n> tokens, a <n>× difference in what each route loaded into the context window.

Per question (self-reported call counts — the only per-question signal either route has, since the
meter attributes per agent rather than per question. **A shape signal, not a cost measurement**: these
are the probes' own tallies, the raw route's are reliably low, and no ratio in this report is derived
from them):

| Question | MCP probe (calls) | Raw probe (calls) | Which route won |
|---|---|---|---|

Reconciliation: MCP probe self-reported <N> calls, meter <N>, true `tool_uses` <N>; raw probe
self-reported <N>, meter <N>, true <N>. <A meter-vs-tool_uses gap is a metering failure; a
self-report gap means calls went missing from the per-question **Calls made** lists — name which
questions look compressed, per Step 3, rather than just noting the gap exists.>

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

Only once the raw probe has actually returned (Step 2). `set_hook_guards(state: "restore")`, then
`workspace_status` to confirm the `hookGuards` line is gone.

**Quote the tool's own scope sentence verbatim in the `Guards:` line rather than paraphrasing it.**
The report's statement about what the suspension covered is a claim about blast radius, and the only
authority on it is the tool that took the lock — "scoped to this session", written from memory, has
appeared in a report that never checked. Paste what `set_hook_guards` actually returned, for both the
suspend and the restore. If `restore` reports that `DOTNET_TOOLKIT_DISABLE_HOOKS` is holding the
guards open, say so — that is an environment the next session inherits, and only a server restart
clears it.

**Then scan the run for guard denials, as a validity check.** If the raw probe's transcript carries a
`PreToolUse` denial on a `.cs` read, the window did not in fact cover its run: the numbers describe a
raw route that was blocked partway through, and the run is invalid rather than publishable. Say so and
re-run; a comparison against a route that was prevented from working is worse than no comparison.

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
