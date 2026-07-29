# `get_call_slice` — how does X reach Y

## When to reach for it

The shortest call path between two symbols **you can already name**. Use it for "does X
reach Y, and through what" instead of walking outwards with repeated `get_references` calls,
which costs a round trip per hop and leaves you assembling the chain yourself.

Call it when both endpoints are known — e.g. confirming a proposed removal is safe by
checking whether an entry point still reaches it, or explaining an unexpected side effect by
finding the path between the trigger and the symbol that causes it. It requires `to` as well
as `from`: it cannot answer an open-ended "who (eventually) calls this" with no destination
in mind — that's `get_call_hierarchy`, below.

```
get_call_slice(from: "PatchTools.ValidatePatch", to: "FeatureLogStore.Append")
```

```json
{"found": true, "depth": 2, "nodesExplored": 69,
 "path": [{"displayString": "Task<string> PatchTools.ValidatePatch(...)"},
          {"displayString": "void PatchTools.AppendLog(...)"},
          {"displayString": "string FeatureLogStore.Append(LogEntry entry)"}]}
```

A miss is still informative: it reports the nearest reachable frontier from each end, which
tells you where the chain actually breaks. `found: false` means no path within `maxDepth`
(default 8) — not necessarily no relationship.

## Reference

Replaces: walking the graph with repeated `get_references` calls — one round trip per hop, and you
assemble the chain yourself.

**Point-to-point only — both `from` and `to` must already be known.** It cannot answer an
open-ended "who (eventually) calls this" with no destination in mind; that's `get_call_hierarchy`,
below. Reach for `get_call_slice` when you can name both ends — e.g. confirming a proposed removal is
safe by checking whether a known entry point still reaches it, or explaining an unexpected side effect
by finding the path between a known trigger and the symbol that causes it.

| Arg | Meaning |
|---|---|
| `from`, `to` | Origin/destination symbols, same addressing as `get_symbol`. |
| `maxDepth` | Default 8. |

Real call and response:

```
get_call_slice(from: "PatchTools.ValidatePatch", to: "FeatureLogStore.Append")
```

```json
{"found":true,"depth":2,"nodesExplored":71,
 "path":[
   {"symbolId":"sym_dd78...","displayString":"Task<string> PatchTools.ValidatePatch(...)"},
   {"symbolId":"sym_2b15...","displayString":"void PatchTools.AppendLog(...)"},
   {"symbolId":"sym_c25d...","displayString":"string FeatureLogStore.Append(LogEntry entry)"}]}
```

`found: false` means no path within `maxDepth` — not necessarily no relationship. It still reports the
nearest reachable frontier from each end, so you know where the chain actually breaks.

## Next steps

- **`found: false`, and you need the open-ended picture** → `get_call_hierarchy` — `get_call_hierarchy.md`
- **Need file/line per site along the path** → `get_references` on any node — `get_references.md`
