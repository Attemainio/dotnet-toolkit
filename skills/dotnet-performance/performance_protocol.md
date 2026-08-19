# dotnet-performance protocol

Read this whole file before answering any question in your prompt. It applies identically to both
probe agents in this run — `dotnet-perf-mcp-probe` and `dotnet-perf-raw-probe`. The only difference
between you and the other agent answering the same questions right now is which tool family you
have; everything else, including this file, is the same for both of you.

## Output format

**Hold every answer until the end and emit all blocks together in your final message.** The
orchestrator only ever sees your last turn's text — if you write Question 1's block, run its tool
calls, then write Question 2's block in a separate turn, everything before your final turn is
invisible to the caller and the report is silently missing questions. Work through the questions
across as many tool-call turns as you need, but don't emit any `## Question` text until you are
done with all of them, then output every block in one message.

Answer every question in this exact shape, one block per question, nothing before or after:

```
## Question N
Answer: <the direct answer — file:line, symbol name, call-site list, whatever the question asks for>
Calls made: <every tool call for this question, in order, one line per call>
Confidence: <certain | fairly sure | guessed — and why, in one line>
Anything you couldn't tell from your tools alone: <or "None">
```

**"Calls made" is a call log, not a summary — one line per actual tool invocation.** If you called
`Read` three times to check three files, that's three lines, not "read the relevant files." Never
fold a retry, a false start, or a repeated call into the same line as another call just because they
served the same step of reasoning — a wrong guess and the call that corrected it are two separate
lines. **The per-question split is the whole value of this log**: it is the only place the cost of
answering *one* question is visible, so a line that folds three calls together silently makes that
question look cheaper than it was, and the questions where a route actually struggled are exactly
the ones most likely to get compressed.

**Keep the log as you go; never reconstruct it at the end.** You are holding all output until your
final message, so the temptation is to write every "Calls made" list from memory once the answers are
in — and memory compresses exactly the questions where you struggled, which are the ones the log
exists to capture. Instead, in each working turn, write the calls that turn made as plain lines in
that turn's text before you move on. The orchestrator never sees those turns, but you do: the final
message then *copies* an existing log instead of recalling one. Measured across three runs
(2026-08-11, 2026-08-12, 2026-08-17), the raw route's reconstructed lists came back at roughly half
its real call count — on 2026-08-17, 26 logged lines against 53 metered calls, including one logged
call for a file that had nothing to do with the question it sat under.

After your last `## Question` block, add one closing line, counted by hand from the lists above:

```
Total tool calls: <N>
```

`N` is the sum of every line across every question's "Calls made" list — it should equal your real
number of tool invocations this run. If tallying reveals it doesn't, that means a line above folded
more than one call together; go back and split it out rather than adjusting this number to match.

## How to play it

- **Search first, read only what you need.** Don't open or fetch more than the question requires to
  answer it.
- **A wrong first guess is normal, not a failure.** Report it as a call in your **Calls made** list
  rather than silently retrying until you get lucky, and rather than omitting it because it didn't
  help — a guess that came back empty is part of this route's honest cost.
- **Don't pad the call count** with lookups nobody would really issue just to look thorough, and
  don't skip a call that would genuinely change your answer just to look cheap. Answer the way a
  competent, unhurried agent actually would.
- **Answer only from what your own tools showed you.** Don't reason about what the other agent's
  tool family would have returned, and don't fill a gap with prior knowledge of this codebase — you
  have none, and the point of this run is measuring what your tools alone can establish.

## Order

Answer the questions in the order given. No summary, no commentary, no section beyond the format
above and the ones in it.
