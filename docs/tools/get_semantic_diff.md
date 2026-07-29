# `get_semantic_diff` — what changed, semantically

## When to reach for it

Symbols added, removed and changed between two git refs, with which version layers moved and
the API impact. Use it instead of reading a textual diff to judge a commit or a branch.

```
get_semantic_diff(fromRef: "9f20936~1", toRef: "9f20936")
```

```json
{"symbolsAdded": ["...Store.SearchText::method ForIndex/1", "..."],
 "symbolsChanged": [{"displayString": "...Store.SymbolStore::method Search/3",
                     "layersChanged": ["body"], "apiImpact": "non-breaking"}],
 "apiImpactSummary": {"breaking": 0, "nonBreaking": 3, "added": 16, "removed": 0}}
```

It is trivia-blind, so a formatting- or comment-only commit correctly reports **no change**.
Read an all-empty result as "nothing semantic moved" — which also covers a commit that only
touched non-C# files. Defaults are `HEAD~1`..`HEAD`.

## Reference

Replaces: reading a textual `git diff` and inferring. Trivia-blind, so a formatting- or comment-only
commit correctly reports no change.

| Arg | Meaning |
|---|---|
| `fromRef` | Default `HEAD~1`. |
| `toRef` | Default `HEAD`. |
| `limit` | Max entries kept in each of `symbolsAdded`/`symbolsRemoved`/`symbolsChanged`, capped independently. Default 50, cap 200. |

Real call and response (trimmed):

```
get_semantic_diff(fromRef: "HEAD~1", toRef: "HEAD")
```

```json
{"range":{"from":"HEAD~1","to":"HEAD","commits":1},
 "symbolsAdded":["...Output::type JsonHoist","...Output.JsonHoist::method Split/1", "..."],
 "symbolsRemoved":["DotnetToolkit.McpServer.Tools.ContextTools::method GetSymbol/15"],
 "symbolsChanged":[
   {"displayString":"...Contract::field Id","layersChanged":["body"],"apiImpact":"non-breaking"},
   {"displayString":"...ContextTools::method GetReferences/9",
    "layersChanged":["decl","body"],"apiImpact":"breaking-public"}],
 "apiImpactSummary":{"breaking":1,"nonBreaking":5,"added":11,"removed":1}}
```

A list beyond `limit` also carries its own `symbolsAddedTruncated`/`symbolsRemovedTruncated`/
`symbolsChangedTruncated`: true — `apiImpactSummary`'s counts are never capped, so it stays the fastest
way to judge whether a commit or branch is safe to build on top of even when the lists themselves were
cut off.

## Next steps

- **Inspect a changed symbol** → `get_symbol` on its `displayString` — `get_symbol.md`
- **Why it changed** → `search_log` — `search_log.md`
- **Judge blast radius before reverting** → `get_call_hierarchy` — `get_call_hierarchy.md`
