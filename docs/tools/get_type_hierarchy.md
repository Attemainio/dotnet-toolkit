# `get_type_hierarchy` — a type's full inheritance shape

## When to reach for it

Base chain up to `object`, transitive interfaces (tagged `direct` vs `inherited`), and every
derived/implementing type — one hop further than `get_symbol`/`get_references` give (those
only show one level: `containingType`, or one hop of `implementations`/`overrides`).

`derived` is a flat ranked list, not a nested tree — a widely-subclassed base could have
hundreds of descendants, and the intermediate shape rarely matters; `get_symbol` on any single
result reveals its own immediate base if you need one more level. Omitted entirely when
`symbol` isn't a class/interface (structs/enums/delegates can't be derived from).

```
get_type_hierarchy(symbol: "SymbolStore")
```

```json
{"symbolId": "sym_a477...", "displayString": "SymbolStore",
 "baseChain": [{"symbolId": "sym_0230...", "displayString": "object"}],
 "interfaces": [],
 "derived": {"items": [], "totalItems": 0}}
```

`SymbolStore` is a `sealed class` with no interfaces, so `interfaces` is empty and `derived` correctly
reports zero rather than being omitted — `derived` is omitted only when `symbol` isn't a class/interface
at all (a method, a struct, an enum). For an interface or a widely-implemented base, `interfaces`/`derived`
fill in with `origin: "direct"|"inherited"` tags and non-empty `items` (`limit`, default 40, matches
`search_index`'s cap convention).

## Reference

Replaces: guessing from `get_symbol`'s one-hop `containingType`, or chaining `get_references`. Base
chain up to `object`, transitive interfaces (tagged `direct` vs `inherited`), and every
derived/implementing type — all beyond what `get_symbol`/`get_references` give in one hop.

| Arg | Meaning |
|---|---|
| `symbol` | Required. Same addressing as `get_symbol`, must resolve to a class/interface/struct/enum/delegate/record. |
| `limit` | Max derived types returned. Default 40, clamped 1-200 — mirrors `search_index`'s cap. |

`derived` is a flat ranked list, not a nested tree (a widely-subclassed base could have hundreds of
descendants, and the intermediate shape rarely matters — `get_symbol` on any result reveals its own
immediate base) and is omitted entirely when `symbol` isn't a class/interface.

Real call and response:

```
get_type_hierarchy(symbol: "SymbolStore")
```

```json
{"symbolId":"sym_a477...","displayString":"SymbolStore",
 "baseChain":[{"symbolId":"sym_0230...","displayString":"object"}],
 "interfaces":[],
 "derived":{"items":[],"totalItems":0}}
```

`SymbolStore` is a `sealed class` with no interfaces, so `derived` correctly reports zero rather than
being omitted — omission means "not a class/interface at all" (a method, struct, or enum), not "zero
found." For an interface or a widely-implemented base, `interfaces`/`derived` fill in with `origin:
"direct"|"inherited"` tags and non-empty `items`.

## Next steps

- **Inspect any base or derived type** → `get_symbol` on its `symbolId` — `get_symbol.md`
- **Who actually calls an interface member** → `get_references(direction: "implementations")` — `get_references.md`
