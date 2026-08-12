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

### Telling concrete implementers from abstract intermediates

A derived entry carries **`isAbstract: true`** or **`isSealed: true`** when it applies, and neither
field otherwise — so an all-concrete hierarchy pays nothing for them.

This is what makes *"which of these can I actually instantiate"* answerable from the one call.
`FindImplementationsAsync` returns abstract intermediates mixed in with concrete leaves, and without
the flag the only way to separate them is a `get_symbol` per row — so the cheap route is to guess, and
the guess is wrong on any hierarchy with an abstract base. An interface with four derived types where
two are `abstract` reads:

```json
{"derived":{"items":[
   {"symbolId":"sym_11a2...","displayString":"BitMaskIndicator<T>","kind":"Type","isAbstract":true},
   {"symbolId":"sym_3f80...","displayString":"BooleanIndicator","kind":"Type"},
   {"symbolId":"sym_9c41...","displayString":"IndicatorBase<T>","kind":"Type","isAbstract":true},
   {"symbolId":"sym_c7e2...","displayString":"ValueIndicator<T>","kind":"Type"}],
  "totalItems":4}}
```

Two implementers are concrete, not four. Answering "the concrete implementers are …" with all four is
the measured failure this field removes.

## Next steps

- **Inspect any base or derived type** → `get_symbol` on its `symbolId` — `get_symbol.md`
- **Who actually calls an interface member** → `get_references(direction: "implementations")` — `get_references.md`
