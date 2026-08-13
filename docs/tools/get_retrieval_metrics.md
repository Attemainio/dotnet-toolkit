# `get_retrieval_metrics` — where the tokens actually went

Replaces: guessing. Computed from this server's own telemetry.

**This server process, always.** There is no cross-session reading and no argument that asks for one.
The raw telemetry tables are emptied when the server starts and again when it stops, and every read is
additionally filtered to the ambient session id minted at startup — so a number here covers this
session's calls and nothing else. That is deliberate rather than a limitation: the whole point of these
numbers is to say how efficient the tools are *now*, and a month of accumulated history from older,
slower versions of the same tools distorts exactly that. Aggregate history was reachable through a
`scope`/`sessionIds` pair until contract 3.94, which removed both.

The dev log (`search_log`) is **not** cleared with it — that records *why* code changed, which outlives
the process that recorded it. Only cost measurement is per-session.

| Arg | Meaning |
|---|---|
| `taskIds` | One or more **caller-supplied** task ids to narrow to — the values passed as `taskId` on the tool calls themselves. This is the only narrowing there is: a task id names one caller *inside* the session. |
| `since` / `until` | Optional ISO date bounds (`yyyy-MM-dd` only) on `created_at`, inclusive on both ends. Rarely needed now — the reading already covers only this process's own lifetime — but still useful to split a long-running session by day. |
| `groupBy` | `tool` \| `symbol` \| `level` \| `session` \| `task` \| `none` (default `tool`). `session` returns a single row: this session's own id, call count, and `firstSeen`/`lastSeen` span. `task` does the same per caller-supplied task id, which is the one that separates concurrent callers. |

### Attributing calls to a caller: the `taskId` argument

Every tool that records telemetry — `get_symbol`, `search_index`, `get_references`, `get_scope`,
`get_call_slice`, `get_call_hierarchy`, `get_type_hierarchy`, `get_project_graph`,
`detect_circular_dependencies`, `get_semantic_diff`, `search_log`, `validate_patch` — takes an optional
`taskId`. Omit it and the call is attributed to the ambient session; supply one and the call can be read
back on its own.

**This is the only way to tell concurrent callers apart.** The session id is one per *server process*, so
parallel agents all share it and would otherwise attribute each other's tokens to themselves. It is also
how you measure a single call's exact cost — snapshot, call, snapshot, subtract that tool's row:

```
get_retrieval_metrics(taskIds: ["eval_flow_20260728T0900"], groupBy: "tool")
get_symbol(symbol: "Pipeline.Deep", include: "source", taskId: "eval_flow_20260728T0900")
get_retrieval_metrics(taskIds: ["eval_flow_20260728T0900"], groupBy: "tool")
```

Reading one tool's own row (rather than `totals`) keeps the snapshot calls' own cost, and any other
caller's traffic, out of the number. `groupBy: "task"` then compares whole callers against each other:

```json
{"groups":[
   {"key":"eval_flow_20260728T0900","calls":31,"tokensReturned":12904,
    "firstSeen":"2026-07-28T09:00:04...","lastSeen":"2026-07-28T09:06:51..."},
   {"key":"eval_history_20260728T0900","calls":12,"tokensReturned":3355,
    "firstSeen":"2026-07-28T09:00:07...","lastSeen":"2026-07-28T09:02:18..."}]}
```

**A task id is not unique on its own — give it a run-unique suffix.** Rows from an earlier *process*
can no longer collide with this one: the tables are cleared at startup and the read is session-scoped
either way. What remains is collision **inside** one server process — re-running a fixed probe matrix
in the same session, which is the ordinary case for `dotnet-selfeval` and `dotnet-performance`, since a
fixed matrix *guarantees* the same names recur and the second run sums onto the first. A date suffix
alone does not separate two runs on the same day. The `calls` column is the tell: a probe issued once
whose row reports `calls: 2` is a collision, and every token number in that run is suspect until it is
explained.

Five tools record nothing and never appear in these numbers: `ping`, `workspace_status`,
`set_output_format` and `reload_workspace` are constant-cost control calls, and `get_retrieval_metrics`
is excluded deliberately — a metrics tool that recorded its own calls would perturb every delta it is
used to compute. `skills/dotnet-selfeval/SKILL.md` builds its whole probe matrix on this recipe.

Real call and response (trimmed):

```
get_retrieval_metrics(groupBy: "tool")
```

```json
{"totals":{"toolCalls":77,"tokensReturned":31450,
           "validationAttempts":6,"insufficientValidations":0,"failedValidations":0},
 "groups":[
   {"key":"get_symbol","calls":49,"tokensReturned":21004},
   {"key":"search_index","calls":15,"tokensReturned":5718},
   {"key":"get_references","calls":7,"tokensReturned":3133},
   {"key":"validate_patch","calls":6,"tokensReturned":1595}]}
```

`validate_patch` writes a validation that actually ran to a separate raw-events table (`patch_events`,
not `retrieval_events`) since it records ladder fields no read tool has (`completedLevel`,
`isSufficient`, …). A call **rejected before validation** — `stale_base`, `unheld_symbol`,
`invalid_edit`, `no_edits` and the rest — is not a validation attempt and writes an ordinary
`retrieval_events` row instead, the way every other tool records its error payloads; it still cost a
round trip and the tokens of its error payload, and both belong in the totals. `totals` and the default
`tool` grouping fold both tables in alongside the read tools; `validationAttempts` above counts only the
`patch_events` side, i.e. the calls that reached the ladder, rather than raw token volume. A
`validate_patch` entry appears in `groups` only when at least one such call has been made — it's absent,
not zero, in a session with no patch activity.

`groupBy: "session"` returns exactly one row, this session's, which is how you read its id and how long
it has been running:

```
get_retrieval_metrics(groupBy: "session")
```

```json
{"totals":{...},
 "groups":[
   {"key":"ses_auto01KZX2A55T32GMZQAND2Y73W5F","calls":25,"tokensReturned":25725,
    "firstSeen":"2026-08-13T08:02:42...","lastSeen":"2026-08-13T08:30:27..."}]}
```

An empty `groups` and zeroed `totals` right after a restart is the expected reading, not a fault: the
raw tables were cleared and this process has not recorded a call yet.

## The `harness` block — what every tool cost, MCP or not

`totals`/`groups` above come from the server's own telemetry, which only MCP tools reach: a `Grep` or
a `Read` never enters this process, so it can never appear there. The `harness` block comes from a
different instrument — the `meter-tool-call` `PostToolUse` hook, which fires on **harness dispatch**
and therefore sees every tool call, whatever the caller's tool grant. It is what makes an MCP route
and a raw `Grep`/`Read` route comparable at all; before it, each side was measured by a different
mechanism, which is not a comparison however carefully it is presented.

```
get_retrieval_metrics()
```

```json
{"totals":{...},"groups":[...],
 "harness":{
   "toolCalls":45,"requestTokens":1310,"responseTokens":31702,"tokenEstimator":"chars4",
   "byTool":[{"key":"Read","calls":12,"requestTokens":240,"responseTokens":19800},
             {"key":"Grep","calls":19,"requestTokens":610,"responseTokens":8300}],
   "byAgent":[{"key":"dotnet-perf-raw-probe","calls":31,"requestTokens":850,"responseTokens":28100},
              {"key":"(main thread)","calls":14,"requestTokens":460,"responseTokens":3602}]}}
```

**The two directions are priced differently, which is why they are never summed.**

| Column | Is | Priced as |
|---|---|---|
| `responseTokens` | what the call loaded into the model's context | **input** tokens — the cheap side, and the context-bloat number |
| `requestTokens` | what the model had to generate to make the call | **output** tokens — roughly **5×** dearer |

Output is ~5× input on both Opus 5 ($5/$25 per MTok) and Haiku 4.5 ($1/$5), so `requestTokens × 5 +
responseTokens` is the comparable unit. The server does not apply that weighting, because it does not
know which model is running — the caller does. Requests are typically small (a one-line command, a
short argument list) and responses large, so the weighting rarely reverses a comparison, but it does
change the margin.

`byAgent` keys on the subagent's `agent_type`, with `(main thread)` for calls outside any subagent.
That is what attributes a call to one side of a benchmark without any agent labelling itself — a
self-reported call log is precisely what this replaces, and self-reports have undercounted badly on
every measured run: on 2026-08-13 a `dotnet-explore` run reported "20 calls used" against a metered 34
and a true `tool_uses` of 35. That is an MCP-equipped agent, not a grep one — under-reporting is an
agent-reasoning artefact, not a property of the raw route.

**`byAgent` merges parallel instances of the same agent type.** `agent_id` is recorded in
`tool_call_events` but no grouping reads it back, so four concurrent `dotnet-code-review` instances
appear as one row with their calls summed. Harmless when the agent types differ (`dotnet-performance`
runs exactly two, one per route); wrong for any fan-out of one type, where the block reports the
fleet's total and no per-instance split exists.

Five things to read correctly:

- **`tokenEstimator` names an approximation, not a measurement.** `chars4` is `(length + 3) / 4` over
  the serialized payload. It is applied identically to both routes, so ratios between them hold even
  though absolute figures do not.
- **The block is absent, never zeroed, when the meter recorded nothing** — the same way a
  `validate_patch` group is absent in a session with no patch activity. "The meter recorded nothing"
  and "the tools cost nothing" are different claims, and a zero would state the second. The usual cause
  is a server started before `hooks.json` registered the hook.
- **It is omitted entirely when you pass `taskIds`.** A hook has no way to know a caller-supplied task
  id, so metered rows carry none; returning them unfiltered beside task-filtered retrieval numbers
  would invite a comparison between two differently scoped figures.
- **A call a `PreToolUse` guard denied is not here, by construction.** The meter is a `PostToolUse`
  hook, and a denied call never runs, so `PostToolUse` never fires for it. The meter therefore counts
  *completed dispatches*, not attempts. When the meter's `calls` for an agent falls short of that
  agent's `tool_uses`, the gap is denials — on 2026-08-13 a blocked probe metered 5 against
  `tool_uses` 8, and those 3 were exactly its guard denials. Read that gap as a signal about what
  happened to the agent, never as a metering fault.

Metered rows are cleared with the rest of the raw telemetry at startup and on a graceful stop, so this
block is this session's, exactly like the numbers above it.

## Next steps

- **Isolating your own calls** → pass the same `taskId` on every recording tool first, then read it back here.
- **`ServerTools`' four tools and this one record nothing** — they are unmeasurable by design. See `server.md`.
