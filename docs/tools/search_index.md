# `search_index` — find symbols when you don't know exact names

Replaces `grep`/`Glob` over `.cs` files — returns ranked symbols with ids and locations, not raw
text lines to hand-filter, and nothing lost to truncation.

## Put every term in one call

Terms are OR-ed and ranked together:

```
search_index(query: "fee ledger TryBuy TrySell")     ← one call, all four
```

not four round trips for one answer. Partial and camel-case-interior terms match — `Ledger` finds
`FIFOLedger`, `Try` finds `TryBuy`. When a question spans two subsystems, name both in the same
query: ranking puts symbols matching more terms first, which is exactly the overlap you want.

### The term floor, and `termsWithNoHits`

Each term gets a **floor share of `limit`** (`limit / terms`) before the globally ranked union
spends what's left — keeps a multi-term query ranked *across* terms rather than reading as
separate per-term lists. The floor is shallow: with 4 terms and `limit: 10`, each term is only two
deep, so a term the result never covered is named back explicitly:

```json
{"termsWithNoHits":["fitness","ledger"], "items":[ ... ]}
```

**Never read an absent term as an absent symbol.** Raise `limit` (cap 200) or re-ask the starved
term alone. Emitted for any multi-term query, including one that returned nothing at all — that's
the response with no other evidence, so it's the one that most needs the terms named. A
single-term query is skipped; its empty `items` already says the same thing.

### Hit shape

Every hit: `symbolId, name, kind, file, line, endLine`. `file`/`line` are resolved live at
response time (swept for staleness), not read from a cache. An overload set is separated by
**parameter count**, then by **parameter type**, so each member reports its own location — a hit
that stays ambiguous (types that reduce to different text, e.g. `Int32` vs `int`) omits `file`/
`line` entirely rather than guessing; it still resolves through `get_symbol`.

Two other reasons a hit carries no location, both named explicitly when they apply, never
together:

- **`generated: true`** — source-generator or build output under a pruned directory
  (`bin`/`obj`/`dist`). No span to patch — the file is rewritten on every build.
- **`outsideRoot: true`** — real source, but a `Compile` item from outside the repo root (e.g. the
  test SDK's synthesized entry point). `get_symbol` still resolves its path — a `../../…` one — but
  it isn't yours to edit.

### Already know the file? Don't `pathPrefix` down to it

If `pathPrefix` is about to name one exact `.cs` file rather than a folder, stop — you already know
where the symbol lives, so this is no longer a search. `get_symbol(symbol: "TypeName", include:
"members")` on that type answers "what's in this file" directly, with signatures, docs and source on
top, for the same or fewer tokens than a ranked whole-index query scoped down to one file.
`search_index` earns its cost by ranking *across* files; asked about a single one, it pays that ranking
pass to answer a question `get_symbol` was built to answer for free. Reach for `search_index` with a
single-file `pathPrefix` only when you don't yet know the type name inside that file either —
otherwise this is the `grep`-shaped habit this tool exists to replace, aimed at the wrong target.

### The `shape` column — what a hit is, and what fetching it costs

Every hit whose location resolved carries `shape`, one string, legend stated once per response —
**none of its 8 facts are gated by size**; a letter is absent only when its count is zero or
doesn't apply to that kind, never "below some threshold":

| Letter | Means | On which kinds |
|---|---|---|
| `P` | declared parameters | method, constructor, operator, delegate |
| `M` | members declared, private included | class, struct, record, interface, enum |
| `N` | nested types | class, struct, record, interface |
| `L` | own line count | anything with a resolved location |
| `O` | body landmarks (`switch/case`, `if`, `for/foreach/while/do`, `catch`, `using`, `lock`) | anything with an executable body |
| `D` | XML doc comment lines | any |
| `C` | non-doc comment lines | any |
| `A` | attributes | any |

`M` and `P` are mutually exclusive by construction. `O` appears only where a body exists — `0`
means a body with no branching, absent means no body at all. `M` counts every member including
private, matching `get_symbol`'s `members` component — but counted **per declaration**, so a
partial type's parts each carry their own share while `get_symbol`'s `members` merges them; a
nested type is counted by `N` yet still listed as a member there too. `C` on a type is the
**transitive total** across its members (double-counts each member's own `C` by design — it
answers "what would fetching the whole type cost"). `D` never overlaps `C`.

Next call, by shape:

| Shape | Next call |
|---|---|
| small `L`, no `M`/`O` | `get_symbol(symbol: id)` — default fetch is right |
| big `L` + big `O` | `get_symbol(include: "bodyOutline")` to map it, then `source:code@from-to` |
| big `L` + small `O` | one linear block — `source:code` whole, or `@from-to` if the region is known |
| `M…` | `get_symbol(include: "members")` — navigate by member list, not a full-type read |
| `N…` | nested types are separate symbols — `get_scope` or a `pathPrefix` search reaches them |
| big `D` | default fetch already carries it; `source:code` skips it |
| big `C` | `source:code-comments` when inspecting behavior, not rationale |
| `A…` | `get_symbol(include: "attributes")` — the cheap `[Authorize]`/`[Obsolete]` check |
| about to **edit** | `include: "all"` regardless of shape — a body patch needs the body-carrying `contentVersion` |

## Filters

| Arg | Grammar | Notes |
|---|---|---|
| `kinds` | bare tokens **OR** (a symbol has one kind); `-token` excludes; mixing forms, bare wins | `class`/`type`, `interface`, `struct`, `record`, `enum`, `delegate`, `method`, `property`, `field`, `event` |
| `modifiers` | bare tokens **AND** (a symbol carries several at once) — opposite of `kinds`; `-token` excludes and combines | literal C# keywords (`public`, `static`, `readonly`, `sealed`, `override`, `async`, `partial`, …) + derived tags `extension`, `indexer`, `initonly`, `disposable`, `asyncdisposable` |
| `implements` | narrows ranked hits like `pathPrefix` — `query` still needs a real term | direct implementers only (not transitive); unresolvable name → empty result, not error |
| `pathPrefix` | folder/file, repo-root-relative, forward slashes, matched on a path-segment boundary | a hit whose file can't resolve (ambiguous overload) is dropped, not guessed, so an overload-heavy query can undercount. Ranking runs over the whole index before scoping — narrow the query text if a far-more-hits-outside case returns fewer than `limit` |
| `xmlDoc` | same AND/exclude grammar as `modifiers` | tokens: `summary`, `returns`, `remarks`, `value`, `inheritdoc`, `params`, `typeparams`, `exceptions` — which sections a doc comment carries beyond plain `<summary>` presence |
| `origin` | — | `"source"` (default, this repo's own declarations) \| `"external"` (BCL/NuGet already referenced from this repo's source — not a general library browser) \| `"all"`. An external hit has no `file`/`line`; follow with `get_symbol` on its `symbolId` |
| `summary` | — | `"has"` adds `hasSummary` (bool, cheap presence check) \| `"full"` adds `summary` (text, capped 160 chars). Read from the syntax index — free even at `index_only` |
| `groupBy` | — | `"namespace"` (namespace→file→symbols) \| `"file"` (file→namespace→symbols) \| `"none"` (flat, `file`/`kind` repeated per row). **Omit it** — the server renders both shapes and keeps whichever costs fewer tokens; an explicit value is always honored as given. Whichever axis fully collapses to one value flattens its wrapper to a header field, and a leaf's `kind` drops when every hit there shares one kind |
| `limit` | — | default 10, cap 200 |

## Reference

```
search_index(query: "validate_patch FeatureLogStore", limit: 5, groupBy: "none")
```
```json
{"items":[
   {"symbolId":"sym_dd78...","name":"DotnetToolkit.McpServer.Tools.PatchTools.ValidatePatch(...)",
    "kind":"Method","file":"src/DotnetToolkit.McpServer/Tools/PatchTools.cs","line":29,"endLine":151},
   {"symbolId":"sym_fc34...","name":"DotnetToolkit.McpServer.Store.FeatureLogStore",
    "kind":"Type","file":"src/DotnetToolkit.McpServer/Store/FeatureLogStore.cs","line":10,"endLine":260}]}
```

`name` is directly usable as `get_symbol`'s `symbol` argument — parameter types are shortened but
matching is whitespace-blind, so the shortened form still resolves.

**If a symbol you're about to edit has no summary**, see the `dotnet-write` skill — a missing
summary on code you touch is something `validate_patch` should fix in the same edit, not just note.

## Next steps

- **See a symbol's shape, docs or source** → `get_symbol` (pass the `symbolId` directly) — `get_symbol.md`
- **Find who calls it, with file/line per site** → `get_references` — `get_references.md`
- **Just the caller list, one hop** → `get_call_hierarchy(maxDepth: 1)` — cheaper — `get_call_hierarchy.md`
- **A type's base chain and every implementer** → `get_type_hierarchy` — `get_type_hierarchy.md`
- **About to edit it** → `get_symbol` for `contentVersion` + `declarationSites`, then `validate_patch` — `validate_patch.md`
