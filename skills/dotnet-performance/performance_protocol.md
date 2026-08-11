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
Calls made: <every tool call for this question, in order, each with a one-line result: N hits / not
  found / etc.>
Confidence: <certain | fairly sure | guessed — and why, in one line>
Anything you couldn't tell from your tools alone: <or "None">
```

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
