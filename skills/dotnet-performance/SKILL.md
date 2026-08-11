---
name: dotnet-performance
description: Use when asked what dotnet-toolkit actually costs compared to doing the same work with plain tools — "is this plugin worth it", "benchmark the toolkit against grep", "how much does search_index really save over Grep/Read", "measure the MCP tools against cat/ls/find", "does this pay for itself on Windows/PowerShell". Runs the SAME stated outcome twice in the repo it is pointed at — once through the plugin's MCP tools, once through Grep/Glob/Read and shell (the guard hooks suspended so the raw route is reachable at all) — and reports tokens and calls per route, per outcome. Measures the plugin's cost honestly rather than assuming its benefit: reports every outcome where the raw route won, and states which side's numbers are exact and which are estimated. Never edits .cs, and always restores the guards before it finishes.
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

## What makes this measurable at all

The raw route is normally unreachable: `PreToolUse` guards block `Read`, `Edit`/`Write` and shell
reads on any compiled `.cs` file. `set_hook_guards` suspends them for a bounded window, which is what
this skill is for. It expires on its own (default 30 min, cap 4 h), but **restore it explicitly when
the run ends** rather than letting it lapse — a later session in this repo would otherwise inherit an
unguarded window it never asked for, and `workspace_status` is the only place that would say so.

## Step 0 — establish the ground

1. `workspace_status`. Record `root`, the solution, file and type counts, and whether the workspace is
   `loaded` or `degraded`. **A degraded workspace invalidates the whole run** — the MCP side would be
   answering from a partial compilation, so it would look cheap for the wrong reason. Fix and reload
   first.
2. Record the repo's size honestly: file count, type count, and the size of the largest `.cs` file.
   These are the axes the answer actually depends on, and a result reported without them is not
   transferable to any other repo.
3. `get_retrieval_metrics` once, to fix the baseline the per-task deltas are measured from.

## Step 1 — choose outcomes, not calls

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

Eight to twelve outcomes is enough. Fewer than six cannot show a crossover; more than about twelve
spends more context than the finding is worth.

## Step 2 — run each outcome twice

Give every route its own `taskId` — `perf_<outcome>_mcp_<date>` and `perf_<outcome>_raw_<date>` — so
the MCP side is separable in `get_retrieval_metrics(groupBy: "task")`.

**Run the MCP route first, while the guards are still up.** It is the route whose answer you will
check the raw route against, and doing it first means the guards are protecting the reference run.

Then `set_hook_guards(state: "suspend", minutes: <enough for the whole raw pass>)` and run the raw
route for every outcome. Use what a session without this plugin would actually reach for: `Grep`,
`Glob`, `Read`, and `Bash` (`cat`/`sed`/`find`/`ls`). On Windows that means PowerShell equivalents
(`Select-String`, `Get-Content`, `Get-ChildItem`) — measure those separately if the question is about
Windows, because `Select-String` and `grep` do not cost the same.

**Play the raw route honestly, in both directions.** Don't use the MCP answer to jump straight to the
right file — that is not the run a session without the plugin gets, and it flatters the raw route.
Equally, don't pad it with searches nobody would really issue. The standard is: what would a competent
agent that had never seen the MCP answer actually type?

## Step 3 — count, and say which numbers are exact

This is the step where a careless run produces a confident wrong number.

- **The MCP side is exact.** `get_retrieval_metrics(groupBy: "task", since: <today>)` reports real
  tokens per `taskId`. Bind the date; ids reused from an earlier run otherwise report both runs.
- **The raw side is estimated.** Nothing meters `Grep`/`Read`/`Bash` output. Estimate it from the
  bytes those responses put into context (roughly 4 bytes per token for source text; state whichever
  divisor you use). Count the *whole* response, including hits you discarded — context does not care
  that you ignored them.
- **Count calls too, and separately.** A route that is cheaper in tokens but takes five round trips
  may still be the worse one, and only the call count shows it.
- **Never compare an exact number to an estimate without saying so.** Every ratio in the report gets
  the estimate marked. A 10× claim built on a byte-divisor guess is a 10× claim with a wide error bar,
  and reporting it bare is the single easiest way for this skill to mislead.

## Step 4 — check correctness before cost

For every outcome whose raw route produced an *answer*, diff it against the MCP answer:

- **Missed hits** — dispatch the text search cannot see. Report the specific symbols missed.
- **False hits** — matches inside comments, strings, or unrelated identifiers.
- **Silent truncation** — a capped grep that gave no signal there was more.
- **Partial-class fragments** — one part returned as though it were the whole declaration.

A route that was cheaper *and* missed hits is reported as **wrong, not cheap**. Put these before the
cost table in the report, because a reader who sees the ratio first will remember the ratio.

## Step 5 — report

Write to `.claude/dotnet-toolkit/perf/<date>-<repo>.md`:

```
# dotnet-toolkit performance — <date>

Specimen: <root> · <solution> · <n> projects · <n> files, <n> types · largest .cs: <n> lines
Estimation: raw-route tokens estimated at <divisor> bytes/token; MCP tokens exact from get_retrieval_metrics
Guards: suspended <start>–<end>, restored <how>

## Correctness
<per outcome: what the raw route missed, invented, or truncated — or "matched" >

## Cost
| Outcome | MCP (calls, tokens) | Raw (calls, ~tokens) | Ratio | Raw route won? |
|---|---|---|---|---|

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
- **Restores the guards even when the run fails.** A run abandoned halfway still owns the suspension
  it took.
