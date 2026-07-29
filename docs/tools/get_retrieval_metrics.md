# `get_retrieval_metrics` — where the tokens actually went

Replaces: guessing. Computed from this server's own telemetry.

| Arg | Meaning |
|---|---|
| `scope` | `session` \| `global` (default). |
| `sessionIds` | One or more session ids to merge together. Required for `scope: "session"`. Every tool call in this process already shares one ambient session id automatically (no argument needed to set it) — `sessionIds` matters only when you want to combine that with sessions from *other* (past) server processes. |
| `taskIds` | One or more **caller-supplied** task ids to narrow to — the values passed as `taskId` on the tool calls themselves. Independent of `scope`: a task id names one caller *inside* a session, not a different slice of history. |
| `since` / `until` | Optional ISO date bounds (`yyyy-MM-dd` only) on `created_at`, inclusive on both ends, usable with either `scope`. |
| `groupBy` | `tool` \| `symbol` \| `level` \| `session` \| `task` \| `none` (default `tool`). `session` groups by `session_id` with `firstSeen`/`lastSeen` — since there's no directory of past sessions, this plus `since`/`until` is how you discover which session ids existed in a date range, before feeding them back into `sessionIds`. `task` does the same for caller-supplied task ids. |

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

Five tools record nothing and never appear in these numbers: `ping`, `workspace_status`,
`set_output_format` and `reload_workspace` are constant-cost control calls, and `get_retrieval_metrics`
is excluded deliberately — a metrics tool that recorded its own calls would perturb every delta it is
used to compute. `skills/dotnet-toolkit-selfeval/SKILL.md` builds its whole probe matrix on this recipe.

Real call and response (trimmed):

```
get_retrieval_metrics(scope: "global", groupBy: "tool")
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

`validate_patch` writes to a separate raw-events table (`patch_events`, not `retrieval_events`) since it
records validation-ladder fields no read tool has (`completedLevel`, `isSufficient`, …). `totals` and the
default `tool` grouping fold its calls/tokens in alongside the read tools; `validationAttempts` above
counts the same six calls from the angle of the validation ladder rather than raw token volume. A
`validate_patch` entry appears in `groups` only when at least one such call falls in scope — it's absent,
not zero, for a scope with no patch activity.

Finding and merging past sessions — since there's no session directory, `groupBy: "session"` combined
with `since`/`until` is the discovery mechanism:

```
get_retrieval_metrics(scope: "global", since: "2026-07-07", until: "2026-07-21", groupBy: "session")
```

```json
{"totals":{...},
 "groups":[
   {"key":"ses_auto01J...","calls":214,"tokensReturned":98213,
    "firstSeen":"2026-07-19T08:03:11...","lastSeen":"2026-07-19T17:42:05..."},
   {"key":"ses_auto01H...","calls":87,"tokensReturned":31005,
    "firstSeen":"2026-07-14T09:11:02...","lastSeen":"2026-07-14T12:20:44..."}]}
```

Feed the ids found this way back into `scope: "session"` to merge them:

```
get_retrieval_metrics(scope: "session", sessionIds: ["ses_auto01J...", "ses_auto01H..."])
```

## Next steps

- **Isolating your own calls** → pass the same `taskId` on every recording tool first, then read it back here.
- **`ServerTools`' four tools and this one record nothing** — they are unmeasurable by design. See `server.md`.
