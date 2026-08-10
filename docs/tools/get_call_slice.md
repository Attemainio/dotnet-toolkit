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
| `fields` | Comma list of extras. Only `signature` is defined: render each node's full parameter-list `displayString` instead of the default compact containing-type-and-member name. Omit for the cheaper default — the parameter lists cost about a third of a slice's tokens. |

Real call and response:

```
get_call_slice(from: "PatchTools.ValidatePatch", to: "PatchTools.BodySpanOf")
```

```
found: true
path[3]{symbolId,displayString}:
  sym_83d7...,PatchTools.ValidatePatch
  sym_77fb...,PatchTools.BodyTextTouchedIdsAsync
  sym_015c...,PatchTools.BodySpanOf
depth: 2
nodesExplored: 122
```

Each node renders as a compact containing-type-and-member name with the parameter list dropped — the
same shape `get_call_hierarchy` uses, and `symbolId` still disambiguates overloads. The same call with
`fields: "signature"` returns the full signatures instead:

```json
{"found":true,"depth":2,"nodesExplored":122,
 "path":[
   {"symbolId":"sym_83d7...","displayString":"Task<string> PatchTools.ValidatePatch(...)"},
   {"symbolId":"sym_77fb...","displayString":"Task<IReadOnlyList<string>> PatchTools.BodyTextTouchedIdsAsync(...)"},
   {"symbolId":"sym_015c...","displayString":"TextSpan? PatchTools.BodySpanOf(MemberDeclarationSyntax member)"}]}
```

`found: false` means no path within `maxDepth` — not necessarily no relationship. It still reports the
nearest reachable frontier from each end, so you know where the chain actually breaks. The frontiers
render in whichever form `fields` selected, same as the path.

A `path` node whose symbol the index cannot name — one resolved through the edge cache but absent from
the `symbols` table — **omits `displayString`** rather than repeating its own `symbolId` under a second
key; `get_call_hierarchy.md` covers when that happens. The frontier lists are the exception: they are
bare strings with no `symbolId` beside them, so an unnameable entry still renders its id, because
dropping the name there would drop the node.

## Next steps

- **`found: false`, and you need the open-ended picture** → `get_call_hierarchy` — `get_call_hierarchy.md`
- **Need file/line per site along the path** → `get_references` on any node — `get_references.md`
