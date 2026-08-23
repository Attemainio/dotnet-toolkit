---
name: dotnet-performance
description: Use when asked what dotnet-toolkit actually costs compared to doing the same work with plain tools — "is this plugin worth it", "benchmark the toolkit against grep", "how much does search_index really save over Grep/Read", "measure the MCP tools against cat/ls/find", "does this pay for itself on Windows/PowerShell", "how does a real session actually use this plugin". Builds one question matrix and sends it, verbatim and blind, to three dedicated subagents run sequentially (guard state is global, so they cannot run in parallel) — dotnet-perf-full-probe (both tool families, the plugin's shipped default guard state — what a real session actually does), dotnet-perf-mcp-probe (only the MCP tools, guards forced to hard-deny a raw read — the ceiling) and dotnet-perf-raw-probe (only Grep/Glob/Read/Bash, guards suspended so it can reach .cs files at all — the floor) — so no agent's answer is informed by having seen another's, or by the orchestrator's own prior exploration. All three routes are metered by one instrument — a PostToolUse hook that fires on harness dispatch, so Grep and get_symbol are counted the same way — and reported as request tokens (what the model generated, the dear side) against response tokens (what was injected into its context), plus calls per route. States which instrument produced each number, and reports every outcome where a raw-tool route won, where two or more agents converged on the same wrong answer, or where the full probe's tool choice diverged from what the advisory nudges suggested (a question-design or a guard-effectiveness finding, not a route one). Never edits .cs, and always restores the guards before it finishes.
---

# Measuring what this plugin costs against not having it

`dotnet-selfeval` asks whether the *tools* are efficient against each other — which route inside the
plugin is cheapest. This skill asks the prior question: **is the plugin cheaper than not using it, in
this repo, on this kind of task — and does a real, unrestricted session actually behave the way an
idealized best-case or worst-case comparison assumes?** Those are three different questions, and they
can disagree. A tool can be the best route inside the plugin and still lose to `grep` on a repo of forty
files; a guard that only advises can still go unheeded even when it is right.

The claim under test is the one in the always-loaded rule: that text search gives *wrong* answers on
C#, not merely slower ones, and that the MCP tools cost fewer tokens than the file reads they replace.
The first half is a correctness claim and the second is a cost claim. **Measure them separately** — a
route that is cheaper *and* wrong is not a win, and reporting a single blended number hides exactly
that case. A third claim, added when this skill grew a third probe, is a *practice* claim: does a real
session, free to use either tool family and free to ignore an advisory nudge, actually land closer to
the ceiling or closer to the floor? A benchmark that only ever plays the two extremes cannot answer
that on its own.

## Why three subagents, not the orchestrator playing all the routes

A single Claude instance that designs the outcomes, answers them through the MCP tools, and *then*
plays another route is contaminated by construction: by the time the second route runs, the answer is
already sitting in context, whether or not it's used deliberately. A route played by an agent who just
saw the answer is not what a session without that head start actually produces, and no amount of
self-instruction to "not look at the answer" closes that gap reliably.

The fix is three independently-spawned agents, none with any memory of this conversation or of the
others' runs:

- **`dotnet-perf-full-probe`** — both this repo's dotnet-toolkit MCP tools *and*
  `Read`/`Grep`/`Glob`/`Bash`, run under the plugin's **shipped default** guard state (read guards
  advisory, write guards blocking, nothing suspended or forced). This is not an artificially restricted
  agent — it is what a real Claude Code session with this plugin installed actually has available, and
  its run is what such a session actually does with it: free to reach for either tool family per
  question, and free to follow or ignore an advisory note the way any session would.
- **`dotnet-perf-mcp-probe`** — only this repo's dotnet-toolkit MCP tools, no `Read`/`Grep`/`Glob`/`Bash`,
  run under `set_hook_guards(state: "block")`. The tool grant alone already keeps it off raw tools; the
  forced block is a backstop against a protocol slip (its one narrow `Read` allowance, for the protocol
  file only) turning into silent contamination instead of a loud, reportable denial. This is the
  **ceiling** — the best case the MCP tools alone can produce.
- **`dotnet-perf-raw-probe`** — only `Read`/`Grep`/`Glob`/`Bash`, no MCP tools, run under
  `set_hook_guards(state: "suspend")` so it can reach a `.cs` file at all with no advisory nudge either.
  This is the **floor** — a session that has never seen this plugin.

The three tool grants and guard states, side by side — check a probe's agent file against this row if
its permissions are ever in doubt, rather than guessing from its name:

| Probe | Tool grant | Guard state | What a `.cs` read does |
|---|---|---|---|
| `dotnet-perf-full-probe` | MCP tools **and** `Read`/`Grep`/`Glob`/`Bash` | default (advisory) | a raw read succeeds and gets an advisory note nudging toward the MCP tools; not blocked |
| `dotnet-perf-raw-probe` | `Read`/`Grep`/`Glob`/`Bash` only, no MCP tools | suspended | a raw read succeeds with no advisory note at all — simulates a session that never had this plugin |
| `dotnet-perf-mcp-probe` | MCP tools, plus `Read` narrowed **by instruction, not by grant** to `performance_protocol.md` only | forced block | any other `Read` of a `.cs` file is denied outright by the hook — the grant alone doesn't stop it, the forced block does |

No probe carries `Edit`/`Write`/`NotebookEdit` or a write MCP tool — all three are read-only by tool
grant, not merely by instruction, per Boundaries below.

All three read the same `skills/dotnet-performance/performance_protocol.md` — one file, read by all
three agents, rather than this skill re-typing near-identical instructions into three separate prompts
where a future edit could update one copy and not the others. `dotnet-perf-mcp-probe` and
`dotnet-perf-full-probe` carry `Read` for exactly this one file (never a `.cs` file for its own sake,
never anything else outside the run); `dotnet-perf-raw-probe` already has `Read` as one of its four
real tools. **The question list itself is per-run, so it stays in the prompt, not the protocol file** —
see Step 2. Don't reuse `dotnet-explore` as one of the three probes: its own report format doesn't
match a probe agent's, and its `Read` fallback (for non-`.cs` docs) is a stray variable this comparison
doesn't need. `dotnet-explore` keeps doing its real job unmodified — including a supporting role in
Step 1 below, distinct from being a probe.

**Guard state is global to this repo, so the three probes never run concurrently.** Two probes in
flight at once would either share a guard state neither of them asked for, or race to change it out
from under each other — either way the run is uninterpretable. `dotnet-perf-full-probe` needs the
guards in their *default* state, `dotnet-perf-mcp-probe` needs them *blocked*, and
`dotnet-perf-raw-probe` needs them *suspended*; these are three different, mutually exclusive states of
the same global switch. Launch one, wait for it to fully return, change state (or don't) for the next,
and only then launch the next. Step 3 is the exact order and the verification at each transition.

**Suspension and forced-block do not stop the metering.** `meter-tool-call` is a `PostToolUse` hook and
is deliberately exempt from `GuardSuspension` — it observes rather than withholding, and a benchmark
changes the guard state precisely so it can measure a route under that state, which is not measured at
all if the state change also silences the measurement. Step 4 depends on this: the meter is what makes
all three routes comparable.

**A freshly created or edited agent file is not visible to the current session.** The harness
snapshots the agent registry when the plugin loads, not per subagent spawn (`docs/design/agents.md`
has the confirmed details). If any probe agent was just added or changed, it needs a session restart
before this skill can launch it — a `not found` error on any agent name is that, not a setup mistake.

**Launching an agent with the `Agent` tool blocks until it returns — there is nothing to poll.**
`dotnet-explore` in Step 1 and each probe in Step 3 are ordinary (non-`fork`) `Agent` calls: the tool
call itself does not complete until the subagent's final message is ready, so by the time you have a
result at all, the wait is already over. **Never call `ScheduleWakeup` after launching one of these
agents** — it requires a `prompt` whenever `stop` isn't set, has nothing sensible to fire, and exists
for a different situation (resuming a `/loop`), not for waiting on a subagent you just called. If you
notice yourself about to say an agent "is running in the background" and that you'll "wait before
continuing," stop: that framing describes a background task this tool call is not, and the correct
next action is simply to read the result you already have and move to the next step.

## Step 0 — establish the ground

1. `workspace_status`. Record `root`, the solution, file and type counts, and whether the workspace is
   `loaded` or `degraded`. **A degraded workspace invalidates the whole run** — the MCP side would be
   answering from a partial compilation, so it would look cheap for the wrong reason. Fix and reload
   first. Also record `pluginRoot` from this same response — Step 2 needs it to point all three probe
   agents at `performance_protocol.md`.
2. **Confirm the guards are at their default state before touching anything.** The same `workspace_status`
   call's `hookGuards` line must be absent — no `SUSPENDED`, no `BLOCKED`. A leftover state from an
   unrelated task would corrupt not just one probe but potentially all three (a stale suspension makes
   `dotnet-perf-full-probe`'s run indistinguishable from `dotnet-perf-raw-probe`'s). If a line is
   present, resolve it first (wait it out, or `set_hook_guards(state: "restore")` after checking with
   the user that it is safe to clear) rather than starting the run underneath it.
3. Record the repo's size honestly: file count, type count, and the size of the largest `.cs` file.
   These are the axes the answer actually depends on, and a result reported without them is not
   transferable to any other repo.
4. `get_retrieval_metrics` once, to fix the baseline all three probes are measured from. **Check that
   the `harness` block is present.** Absent means the `meter-tool-call` hook has recorded nothing, and
   this run has no instrument that sees all three routes — they would again be measured by different
   means, the flaw this design exists to remove. The usual cause is a server started before the hook
   was registered in `hooks.json`; restart it and begin again rather than reporting the run.

## Step 1 — build the question matrix

**Survey the repo for real targets before writing questions, and delegate that survey rather than
doing it yourself.** A question built around a symbol that turns out not to exist, or that isn't
actually the shape the outcome needs (no partial class in a small repo, say), is discovered wrong
only after every probe has already run. Launch `dotnet-explore` with a brief naming the outcome
types below and asking it to report real candidates for each — a partial class, a private method
with a common-word name, an interface with at least one implementer, and so on — with `symbolId`s
and locations. This is `dotnet-explore` doing its actual job (mapping the codebase for a caller about
to act on it), not a fourth probe: its findings go to *you*, never to any probe agent, so knowing
the real targets while writing neutral questions is normal design work, not contamination. The
contamination risk this whole skill exists to avoid is specific to the probe agents *answering* the
questions, not to the orchestrator *knowing* what a good question looks like.

Score **a stated outcome**, the way `dotnet-selfeval`'s 3a does — never a single call against a single
command. "Find the definition of `X`" is an outcome; `search_index` is not.

Pick outcomes that span the range where the answer plausibly changes sign, and say which is which:

| Outcome | Where the plugin should win, and why |
|---|---|
| Find a type by exact name | **Narrowly, or not at all** — `grep -rn "class Foo"` is one cheap call. Expect this row to be close, and report it honestly if a raw-tool route wins. |
| Find a symbol whose exact name you don't know | Ranked hits vs. a grep that needs several guesses; count *every* guess a raw-tool route took, including the ones that returned nothing. |
| Read one method out of a 2000-line file | The file read has no way to return less than the file. |
| Read a type split across partial files | The raw route returns one fragment **and no signal the rest exists** — a correctness finding, not a cost one. **Expect this row to be closer than it sounds**: where every part carries identical declaration text, one `grep` enumerates them all, and on 2026-08-17 it beat an MCP route that read ranked `search_index` hits as an enumeration and reported 5 of 13. Ask *which files* rather than *what is in it* if you want the enumeration failure specifically, and score the MCP-tool answer on whether it reached `declarationSites`. |
| Find every caller of a method | A raw-tool route cannot see interface, virtual or delegate dispatch. Verify against the MCP answer and count what text search *missed*, not just what it cost. |
| Find implementers of an interface | Same shape as callers. |
| Confirm a symbol is unused before deleting it | A raw-tool route's answer is unsound here; report that rather than a token ratio. |
| Callers of a method with a short or common name | A false-hit stress test: string literals, unrelated identifiers, or a same-named method in a different class can all make raw search's answer wrong, not just noisy. |

Eight to twelve outcomes is enough. Fewer than six cannot show a crossover; more than about twelve
spends more context than the finding is worth.

**Phrase each outcome as a neutral question, not a leading one.** "Find `SymbolStore`" is fine — the
outcome itself names the target. "Find the class that stores X" for a fuzzy-name outcome must not
contain the real class name anywhere in the wording, or every agent inherits a hint the real session
wouldn't have. Write the question once; all three agents get the identical text.

## Step 2 — one shared prompt, sent to all three agents

Construct a single prompt template covering the whole matrix, and send it to all three agents
essentially unchanged — the only difference is the one line naming which tool family (and, for
`dotnet-perf-full-probe`, that the guards are at their default state) that agent has (each agent's own
file already states its constraints, so this is confirmation, not new information). The numbered
question list must be byte-for-byte identical across all three prompts, because that identity is what
makes the resulting comparison mean something.

**Point all three agents at the protocol file instead of restating its contents.** Resolve `pluginRoot`
from Step 0's `workspace_status` call and give every prompt the same instruction: *"Read
`<pluginRoot>/skills/dotnet-performance/performance_protocol.md` first — it has your exact output
format and how to play each question."* That file, not this skill's prose, is where the output format
and the "play it straight" instructions actually live; a change to any of them belongs there, in the
one copy all three agents read, not duplicated into this step.

Give `dotnet-perf-mcp-probe` and `dotnet-perf-full-probe` each their own `taskId` to pass on every MCP
call (`perf_mcp_<date>`, `perf_full_<date>`), stated in their copies of the prompt — this is what lets
Step 4 pull `dotnet-perf-full-probe`'s MCP-side calls out of `get_retrieval_metrics` specifically, on
top of what the harness meter already attributes to it by `agent_type`. `dotnet-perf-raw-probe` still
has no equivalent — a `taskId` is an MCP argument, and `Read`/`Grep`/`Bash` take none. That asymmetry no
longer decides the comparison, though: the `taskId`s buy only the server-side view of the MCP
responses, while the number every route is actually compared on comes from the harness meter, which
needs nothing from any prompt (Step 4).

## Step 3 — launch order and guard housekeeping

Three sequential passes, in this order, because only the first needs no guard-state change and the
other two each need a **different**, mutually exclusive state. **The main agent (you) sets the guard
mode explicitly between every launch — never leave a probe to run under whatever state the previous
one left behind.** Spelled out as one checklist, with the full reasoning and the positive-control
detail for each line in the three passes below it:

1. Guards are already at default (confirmed in Step 0.2) — no mode change needed.
2. Launch `dotnet-perf-full-probe`. **Wait for it to return** (see the note above Step 0 — this is a
   blocking call, not a background one).
3. `set_hook_guards(state: "suspend", minutes: <sized to the pass>)`.
4. Prove the suspension reached the hooks (Pass 2, step 2's positive control) before launching anything.
5. Launch `dotnet-perf-raw-probe`. **Wait for it to return.**
6. `set_hook_guards(state: "restore")`, then confirm via `workspace_status` that `hookGuards` is gone.
7. `set_hook_guards(state: "block", minutes: <same sizing>)`.
8. Prove the block reached the hooks (Pass 3, step 2's positive control) before launching anything.
9. Launch `dotnet-perf-mcp-probe`. **Wait for it to return.**
10. `set_hook_guards(state: "restore")`, then confirm via `workspace_status` that `hookGuards` is gone.

### Pass 1 — `dotnet-perf-full-probe` (default state, no change needed)

Launch it now, while the guards are still confirmed at their default (Step 0.2). Wait for it to return.
Nothing to suspend, nothing to restore — this pass is why it goes first, so neither of the
state-changing passes below has to hand a clean default back to it later.

### Pass 2 — `dotnet-perf-raw-probe` (suspend → run → restore)

1. `set_hook_guards(state: "suspend", minutes: <sized to the whole pass with margin>)`. Eight to twelve
   outcomes has taken 5–15 minutes; the default is 30 minutes and the cap is 4 hours, so pass `minutes`
   explicitly rather than taking the default — a report that cannot state the window it ran under
   because nobody chose it is not honestly reporting anything.
2. **Prove the suspension actually reached the hooks before launching the probe.** Do not take
   `set_hook_guards`' success string for it, and do not take `workspace_status`' `hookGuards: SUSPENDED`
   line for it either — both are the *server's* view of a file it wrote, and neither observes a hook
   process. On 2026-08-13 both reported a suspension that was not in force, the raw probe was denied on
   its first `.cs` read, and the run was lost. Since `guard-cs-bash-read` stopped denying on
   2026-08-21, "not denied" is no longer a valid positive control either — an unsuspended-but-advisory
   guard now allows the read too. Use one call, `Bash` with `head -1` on any `.cs` file under `src/`,
   and check whether the tool result carries an added advisory note pointing at `dotnet-read`:
   - **No advisory note** → the suspension is real; launch the probe.
   - **An advisory note appears** → the suspension did not reach the hooks. **Stop.** Do not launch the
     probe, do not report numbers for this pass, and restore before doing anything else. The known cause
     is a session-id mismatch between the long-lived MCP server and the live session
     (`docs/design/hooks.md`); restarting the MCP server clears it.
3. Launch `dotnet-perf-raw-probe` and **wait for it to return** before doing anything else with guard
   state. Restoring while it is still running re-arms the guards underneath it: its next `Read` of a
   `.cs` file is denied, it quietly falls back to whatever it can still reach, and the run ends up
   measuring a hobbled raw route without saying so.
4. `set_hook_guards(state: "restore")`, then `workspace_status` to confirm the `hookGuards` line is
   gone. **Never let the expiry do the restoring** — a suspension that lapses on its own leaves no
   record of when it ended, and this confirmation is what the report's `Guards:` line quotes.

### Pass 3 — `dotnet-perf-mcp-probe` (block → run → restore)

1. `set_hook_guards(state: "block", minutes: <same sizing as pass 2>)`.
2. **Prove the block actually reached the hooks before launching the probe**, the mirror image of pass
   2's check: `Read` (not `Bash` — this exercises `guard-cs-read` directly, the guard that matters for
   this probe's one narrow `Read` allowance) on any `.cs` file under `src/`, and check the result:
   - **Denied** (`PreToolUse` blocks the call) → the block is real; launch the probe.
   - **Allowed, with or without an advisory note** → the block did not reach the hooks. **Stop**, for
     the same reason and via the same fix as pass 2's failure mode — a session-id mismatch between the
     server and the live session.
3. Launch `dotnet-perf-mcp-probe` and **wait for it to return** before restoring, for the identical
   reason pass 2 does — restoring early re-arms nothing dangerous here (the probe has no raw tools to
   fall back to), but it does silently drop the block mid-run if the probe's one `Read` allowance is
   ever exercised, which would let a protocol slip through unblocked and unreported.
4. `set_hook_guards(state: "restore")`, then `workspace_status` to confirm the `hookGuards` line is
   gone.

**If either pass 2 or pass 3 has to stop at its positive control, restore immediately and report the
failure rather than attempting the remaining pass.** A partial run with an honest explanation is worth
more than a full run where one leg's guard state was never actually verified.

## Step 4 — count, and say which instrument produced each number

This is the step where a careless run produces a confident wrong number. Three instruments are
available, they answer three different questions, and the report must name which produced each figure.

| Instrument | Covers | Answers |
|---|---|---|
| `get_retrieval_metrics`'s `harness` block, `byAgent` rows | **all three routes**, every tool the harness dispatched | What did each route's tool calls cost, split into request and response tokens? |
| Each `Agent` call's own `subagent_tokens` / `tool_uses` | all three routes, whole run | What did the whole route cost in production, bootstrap and reasoning included? |
| `get_retrieval_metrics(taskIds: ["perf_mcp_<date>", "perf_full_<date>"])` | **MCP calls only**, server-side, split by `taskId` | What did each agent's MCP responses alone cost, as the server itself measured them? |

- **The `harness` block is the comparison.** It is the only instrument that measures all three routes
  with the same code on the same payload: the `meter-tool-call` `PostToolUse` hook fires on harness
  dispatch, independent of which tools an agent's grant contains, so a `Grep` is metered exactly as a
  `get_symbol` is. Read it once after all three probes have returned and take the `byAgent` rows —
  `dotnet-perf-mcp-probe`, `dotnet-perf-raw-probe` and `dotnet-perf-full-probe` each appear under their
  own `agent_type`, so no probe has to label itself and no self-report has to be trusted. Before this
  existed, each route was measured by a different mechanism, which is not a comparison however
  carefully it is presented.
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
  to all three routes, so the *ratios* between them are sound while the absolute figures are not — state
  that rather than presenting them as exact.
- **`subagent_tokens` remains the production number.** It is exact and includes each agent's own
  bootstrap and reasoning, which the meter never sees. A route can meter cheaply and still cost more
  overall because it reasoned longer; report both and let neither stand in for the other.
- **Count calls too, and separately.** A route cheaper in tokens but taking many more round trips may
  still be the worse one. The meter's `calls` per agent is now the ground truth for this.
- **`byAgentTool` is the exact per-agent, per-tool breakdown — read it instead of inferring one.**
  `byTool` folds every agent together per tool; `byAgent` folds every tool together per agent; neither
  alone answers "how many `Read` calls did `dotnet-perf-full-probe` make, versus `get_symbol`."
  `byAgentTool` crosses the two directly, from the same metered rows, so it is exact where the
  per-question "Calls made" log below is self-reported and reliably undercounted. Pull it once per run,
  right alongside the aggregate table, and **report it as three separate tables, one per agent, each
  closed with its own total row** — a single table with every agent's rows interleaved is the aggregate
  table's job, not this one's; this one exists so a reader can see one agent's whole tool profile without
  filtering rows in their head. For `dotnet-perf-full-probe` specifically, its table is the aggregate
  confirmation of tool choice (which family it reached for overall); which family it chose *per
  question* belongs in the per-question table instead (Step 6), not repeated here.
- **Reconcile three ways, every run, for every probe, as your own sanity check — it does not need a
  report section of its own.** Each probe's self-reported `Total tool calls` line, the meter's `calls`
  for that `agent_type`, and the `Agent` call's `tool_uses`. The meter and `tool_uses` should agree
  closely — a gap between *them* points at a metering failure, which does belong in the report, as a
  one-line flag under Cost. A gap between either and the self-report means calls went missing from the
  per-question **Calls made** lists; **subtract the known offset before calling anything a
  discrepancy**: each probe's `Read` of `performance_protocol.md` is setup its own protocol tells it to
  leave out of the log, but the harness dispatched it, so the meter counts it — expect the meter to
  exceed each self-report by at least one on that account alone, plus any `workspace_status` readiness
  call the probe treats the same way. A gap of one or two is that and needs no mention; a gap of a third
  or a half is compression, and *that* is worth one sentence in the report naming which questions look
  compressed (several same-tool lines collapsed into one) — the report already carries the meter's own
  numbers in Cost, so the reconciliation table itself (three routes × three counts) would only be
  repeating a check the reader has no way to act on. The raw route has undercounted itself on every run
  so far — by roughly half on 2026-08-11, on 2026-08-12, and again on 2026-08-17 (26 logged lines against
  53 metered calls) — which is exactly why the self-report is no longer the instrument, only a cross-check
  on it.
- **Never compute a ratio from the per-question table.** Those counts are self-reported, and on the raw
  side reliably too low, so a "calls per question" comparison built from them is a measurement of the
  agents' bookkeeping. The table is a *shape* signal — which questions took several tries, where a
  route flailed, which family `dotnet-perf-full-probe` reached for — and the report must label it as
  such. Every number a cost claim rests on comes from the meter or from `subagent_tokens`.

## Step 5 — check correctness and tool choice before cost

Compare all three agents' answers question by question:

- **Missed hits** — dispatch a raw-tool route's text search cannot see. Report the specific symbols
  missed.
- **False hits** — matches inside comments, strings, or unrelated identifiers.
- **Silent truncation** — a capped search that gave no signal there was more.
- **Partial-class fragments** — one part returned as though it were the whole declaration.
- **Undercounting** — a route that reached the right file and the right scope but still enumerated an
  incomplete set of real matches. This is a subtler failure than a false hit and only shows up once
  the obvious ambiguity is already avoided — don't stop checking a "right verdict" answer for a
  complete one.

**If two or more agents land on the same wrong answer, that is a finding about the question, not about
any route.** A vaguely-worded outcome that a competent tool-assisted agent and a competent raw agent
both misread the same way means the wording was underspecified, not that either route failed — score
it as a wash and say so, rather than letting it inflate or deflate any side's tally.

A route that was cheaper *and* missed hits (or undercounted, or shared a wrong answer that happens to
look like a "win") is reported as **wrong, not cheap**. Put correctness before the cost table in the
report, because a reader who sees the ratio first will remember the ratio.

**For `dotnet-perf-full-probe` specifically, record which tool family it used per question, and whether
that lines up with what an advisory note (if one appeared) suggested.** Three shapes are all real
findings, and none of them is a failure of the probe: it used the MCP tools without needing a nudge
(the advisory was redundant for this question); it used a raw tool, got advised, and switched (the
advisory worked); it used a raw tool, got advised, and *didn't* switch (the advisory was seen and
overridden by judgment, or ignored — say which, if the probe's own reasoning makes that clear). A run
where the full probe never once reaches for a raw tool is not obviously a win to report uncritically —
check whether that is because the MCP tools were genuinely the better choice every time, or because the
probe defaulted to them out of habit on a question where the raw route was actually cheaper (Step 1's
"find a type by exact name" row is the outcome most likely to expose this). **This finding is a column
in Step 6's per-question table, not a chapter of its own** — a reader comparing questions already has
that table open, and a separate "tool choice" section repeats the same per-question facts one screen
away instead of next to them.

**Report correctness as a table, one row per question, one column per probe, plus a verdict column —
referenced by question number, not a restated question.** Step 6's report carries the exact question
text once, verbatim, in its own section; a table cell only needs each probe's answer in its shortest
form (a file:line, a count, "matched") and the verdict column names what, if anything, differs. **Save
the prose for the rows that need it.** Write a sentence or two of reasoning only where a row shows a
real discrepancy — a miss, a false hit, undercounting, or two-or-more agents converging on the same
wrong answer — and say what it implies (a plugin gap, a raw-tool blind spot, or an underspecified
question). A row where all three matched needs nothing beyond the table cell; padding it with a
restated "all three agree" sentence is the same fact a third time. If the whole table comes back with no
discrepancy anywhere, that absence is itself worth one closing paragraph: say plainly that nothing
diverged, then give the honesty check — what about this question matrix kept correctness from being
stressed (shallow hierarchy, no divergent partial-class content, no deep dispatch chain), and what a
harder follow-up run would need to target instead. That paragraph is reasoning about how to improve the
next run, which is exactly the kind of reasoning this report should carry — it just shouldn't be
duplicated onto rows that have nothing to explain.

## Step 6 — report

**Every run is independent. Don't read `.claude/dotnet-toolkit/perf/` before or during a run, and
don't append to a file that's already there.** A prior report was written by a different question
matrix, a possibly-different repo state, and — if it predates a protocol or a probe-set change — a
possibly-different methodology; treating it as context for this run's questions or numbers is exactly
the kind of contamination Step 1 already goes out of its way to avoid on the probe side. Write a fresh
file every time, even if one already exists for today's date. Only read or extend an existing report
when the user explicitly asks to — "compare this against the last run," "update that report" — and even
then, say plainly in the new report which prior run you're comparing against and why, rather than
quietly merging numbers from two different methodologies into one table. **A report written before this
skill grew its third probe used a two-route methodology** — flag that explicitly rather than silently
comparing a two-route baseline against a three-route one.

Write to `.claude/dotnet-toolkit/perf/<date>-<HHmmss>-<repo>.md` (the time component is what keeps
same-day runs from colliding into an append). **The report is for a human to read and act on without
re-running `dotnet-selfeval`** — favor lists and tables over prose paragraphs throughout, reserve
prose for the spots called out below, and never compress a table into a `·`-joined line just to save
space: a reader who has to parse delimiters to find the one number that matters is paying in
readability for a saving that was never the point.

```
# dotnet-toolkit performance — <date>

## Specimen

<root>

Project structure (<n> projects)
- <project 1>
- <project 2>
...

<n> files · <n> types

## Cost basis

- All three routes are metered identically by the harness's `PostToolUse` hook and read from
  `get_retrieval_metrics`'s `harness` block.
- Tokens are request (output — what the model generated to make the call) and response (input — what
  landed in context), a `<tokenEstimator>` approximation applied identically to all three routes.
- Whole-route totals are each `Agent` call's exact `subagent_tokens`.
- The MCP-capable routes additionally report server-side response tokens via
  `get_retrieval_metrics(taskIds)`.

## Guards

| Pass | Agent | Guard state | Duration | Positive control | Result |
|---|---|---|---|---|---|
| 1 | full probe | default (no change) | — | — | n/a |
| 2 | raw probe | suspended | <minutes>m at <time> | <the exact check run> | <confirmed real / did not reach hooks> |
| 3 | mcp probe | blocked | <minutes>m at <time> | <the exact check run> | <confirmed real / did not reach hooks> |

Restored after each of passes 2 and 3; both confirmed via `workspace_status` (`hookGuards` line absent).

## Questions
<the numbered question list, verbatim — byte-for-byte the same text all three agents received per Step
2. This is the one place the question text appears; Correctness and Cost below reference it by number
rather than repeating it>

## Correctness

| Q | Full probe | MCP probe | Raw probe | Verdict |
|---|---|---|---|---|
<one row per question. Each probe's cell is its answer in the shortest form that settles it — a
file:line, a count, a yes/no, or "matched" when it agrees with the row's converged answer. The verdict
column names the outcome in a few words: "matched", "N agents converged on the same wrong answer" (a
question-design finding, not a route one), or a short tag for the discrepancy ("raw undercounted",
"raw missed dispatch caller", "full over-enumerated">

<Prose reasoning belongs here, and only here — one short paragraph per row that has something to
explain (a miss, a false hit, undercounting, silent truncation, or a shared wrong answer), saying what
it implies for the plugin or the question design. A row that matched cleanly needs no prose; don't
restate "all three agree" under a table cell that already says so. If no row in the whole table shows a
discrepancy, close the section with the honesty check instead: say so plainly, then explain what about
this question matrix kept correctness from being stressed (shallow hierarchy? no divergent partial-class
content? no deep dispatch chain?) and what a harder follow-up run would need to target — that is the
improvement-facing reasoning this report exists to carry, not a claim that the plugin is flawless>

## Cost

Aggregate — all three routes, one instrument (the harness meter):

| Route | Calls | Request tokens (output) | Response tokens (input) | Weighted (req×5 + resp) | Whole-agent tokens |
|---|---|---|---|---|---|
| Full probe | | | | | |
| MCP probe | | | | | |
| Raw probe | | | | | |

**Context injected** — the response-token column — is the plugin's central claim, so state it on its
own line for each pair worth comparing: full vs. raw (<n> vs <n>, a <n>× difference — what the plugin
actually saves in practice), and MCP vs. raw (<n> vs <n>, a <n>× difference — the ceiling).

### Per-question detail

| Q | Full probe (calls, family) | MCP probe (calls) | Raw probe (calls) | Which route won |
|---|---|---|---|---|

Self-reported call counts — a **shape signal, not a cost measurement** (the raw side's counts are
reliably low; no ratio anywhere in this report is derived from this table). "Family" is which tool
family the full probe actually reached for on that question — `MCP`, `Raw`, or `Both (switched after
advisory)` — this is the full probe's tool-choice finding, reported once, here, rather than in a
separate chapter.

### dotnet-perf-full-probe — tool calls

`get_retrieval_metrics`'s `byAgentTool`, exact:

| Tool | Calls | Request tokens | Response tokens |
|---|---|---|---|
<one row per tool this agent used>
| **Total** | | | |

### dotnet-perf-mcp-probe — tool calls

| Tool | Calls | Request tokens | Response tokens |
|---|---|---|---|
<one row per tool this agent used>
| **Total** | | | |

### dotnet-perf-raw-probe — tool calls

| Tool | Calls | Request tokens | Response tokens |
|---|---|---|---|
<one row per tool this agent used>
| **Total** | | | |

<Only add one more sentence here — no table — if a probe's self-reported total differs from the meter's
by more than the ~2-call setup offset (the protocol-file read plus a readiness call): name which probe
and which questions look compressed. If every probe is within that margin, omit this note; the numbers
above already come from the meter, which is the instrument, not the self-report>

## Where each route wins

### Where dotnet-toolkit shines
<the rows where an MCP-capable route won on cost, correctness, or both, and why — cite the question
number and the mechanism (structured lookup vs. text search, dispatch-aware vs. blind, and so on)>

### Where raw tools win
<the honest list — every row where the raw route won, and why, even though it costs this report a clean
narrative. If it's empty, say what you checked and why you believe that rather than just asserting it —
an empty section with no reasoning reads as a run that wasn't really trying>

### Closing the gap
<for each row above where raw won: what would need to change — in the tools, in a question's shape, or
in the advisory wording — to close it, and whether that's realistic. Guards are advisory by design now
(read guards stopped hard-denying on 2026-08-21, trading enforcement for flexibility), so a real session
already keeps the option to take the raw route when it's genuinely cheaper — closing a gap here means
making the MCP route cheaper or the advisory more persuasive, never proposing to make the guard block
harder or route around a session's own choice to bypass it. State the flexibility/performance trade-off
plainly: which of these rows are worth chasing, and which are an acceptable cost of keeping the read
path unblocked>

## What this covers, and what it doesn't

Specimen ceiling: largest `.cs` file is <n> lines (<path>). This run's targets: <sizes> — call out
plainly if they're mid-sized against that ceiling rather than a real stress case.

Stress conditions this run exercised — `(x)` if Step 1's matrix targeted it, `( )` if it didn't (add or
drop rows to match what the matrix actually covered):
- (x) partial-class enumeration, disjoint content across N files
- ( ) interface/virtual dispatch callers
- ( ) deep interface-forwarding or multi-layer dispatch
- ( ) generic overload resolution
- ( ) large-file extraction near the repo's real ceiling
- ( ) common-name collision at scale (many false-positive hits for one real definition)

Rows likely to flip on a harder repo or question:
<one line per row from "Where raw tools win" above: what would need to be true of the repo or the
question for that row to reverse, tying back to the unchecked conditions above>
```

## Boundaries

- **Never edits `.cs`.** Not through `validate_patch`, and emphatically not through the raw `Edit` a
  suspension makes reachable for the raw probe. The suspension is for *reading* like an unequipped
  session; an edit made through it would land unrecorded, which is the exact failure the guards exist
  to prevent. `dotnet-perf-full-probe` carries no `Edit`/`Write`/`NotebookEdit` at all, and no write MCP
  tool either, so it is read-only by tool grant, not merely by instruction.
- **Findings are about the plugin**, never about the consuming repo's code.
- **Each run stands alone.** No reading prior `.claude/dotnet-toolkit/perf/` reports for context, no
  appending to one, unless the user explicitly asked for a comparison against a named prior run.
- **Restores the guards after every pass that changed them, even when that pass's run fails.** A pass
  abandoned halfway still owns the suspend or block it took, and the next pass must not inherit it.
- **`dotnet-perf-mcp-probe`, `dotnet-perf-raw-probe` and `dotnet-perf-full-probe` are launched only from
  here.** None carries its own question list, and none has anywhere else to learn the output format
  except `performance_protocol.md` — invoking any of them outside this skill's prompt leaves it with a
  question list to answer but no shared format to answer in.
