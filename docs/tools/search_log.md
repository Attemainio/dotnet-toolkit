# `search_log` — why past changes were made

Replaces: guessing from the code, or re-proposing a design that was already tried and rejected. Only
covers changes applied through `validate_patch` — an empty result is not proof nothing relevant
happened, just that nothing relevant went through this tool.

| Arg | Meaning |
|---|---|
| `query` | Whitespace-separated terms matched against recorded intents; **every** term must appear, in any order. Omit to list the most recent entries. |
| `limit` | Default 10. |

Real call and response (trimmed to the fields that matter):

```
search_log(limit: 3)
```

```json
{"items":[
   {"logId":"log_01KY07FZ...","date":"2026-07-20",
    "intent":"Fix get_symbol's [Description]: the batch-mode response was documented as an array, but it's actually column-shaped like search_index/get_references"},
   {"logId":"log_01KY07F8...","date":"2026-07-20",
    "intent":"Remove unused toolCallId/patchId/validationAttemptId parameters from Error/StaleBase/BuildResponse, ..."}]}
```

Each entry carries `logId, date, intent`, plus `tags` (a JSON array) only when the patch that created it
actually supplied one — `validate_patch`'s `tags` argument is optional and rarely used in practice, so
most entries carry no `tags` field at all rather than an empty array. Matching is over `intent` only —
there is no tag-based filter today.

**Terms are AND-ed, not OR-ed — the opposite of `search_index`.** The query is split on whitespace and
an entry matches only when its intent contains every term, in any order:

```
search_log(query: "task id telemetry")   → matches "…attribute this call to its own task id in telemetry"
search_log(query: "telemetry task id")   → the same entry; order is irrelevant
search_log(query: "task id parrot")      → no hits; adding a term narrows, never widens
```

The asymmetry is deliberate. `search_index` ranks its OR-ed hits, so a loose term merely sorts lower;
`search_log` has no relevance ranking — rows come back newest-first under a `LIMIT` — so an OR would let
one common word fill the result with recent entries and push the genuinely matching one past the limit.

Until 2026-07-28 this matched the **whole query as one literal substring**, so `"task id telemetry"`
returned nothing unless those words appeared adjacent in that exact order, while `"task id"` matched
fine. Zero hits reads as "no such history exists", which is the one answer this tool must never give
wrongly — see `search_log(query: "search_log match")` for the fix's own entry.

Terms match as **substrings, without stemming**, so a term is effectively a prefix/infix filter: `match`
finds intents containing "match", "matches" and "matching", while `matching` finds only the last. When a
query comes back empty, shortening a term to its stem is usually the fix.

## Next steps

- **Read the code the entry describes** → `get_symbol` — `get_symbol.md`
- **See what actually changed in that commit** → `get_semantic_diff` — `get_semantic_diff.md`
