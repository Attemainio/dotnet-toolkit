# `get_scope` — what is callable *here*

## When to reach for it

Members, inherited members, locals, parameters and **applicable extension methods** at a
file/line, filtered to what is actually accessible from that position. Grep cannot answer
this: an extension method shares no text with its call site.

**Different question from `get_symbol`'s `members`.** `members` is a static, position-free
list of what a *type declares* — ask it when you already know the type. `get_scope` is
position-sensitive — it also surfaces inherited members, locals/parameters in scope at that
exact line, and extension methods applicable to a receiver, none of which `members` returns.

Call it when:
- you're about to write a helper and suspect one already exists on a variable in scope;
- you're standing at a cursor and don't yet know the receiver's type, so `get_symbol` has
  no target to query yet;
- you want "what's in scope generally" at a line (drop `receiver` for that).

Don't reach for it once you already know the type name — `get_symbol(include:"members")` is
cheaper and doesn't need a file/line/column.

```
get_scope(file: "src/DotnetToolkit.McpServer/Tools/PatchTools.cs",
          line: 185, receiver: "featureLog", filter: "methods")
```

```json
{"receiverType": "FeatureLogStore",
 "items": [{"displayString": "Append(LogEntry entry)", "kind": "Method"},
           {"displayString": "EntryCount()", "kind": "Method"},
           {"displayString": "Equals(object? obj)", "kind": "Method",
            "origin": "inherited", "definedIn": "object"}]}
```

`origin` separates what the type itself declares from what it inherits — usually the first
thing you want to know. Both `origin` and `definedIn` carry nothing on a row the `receiverType` header
already accounts for; `definedIn` likewise on a local or parameter (which has no declaring type), and it
carries the *namespace* on a type-kind row. "Nothing" reads as an **empty cell** rather than an absent
field whenever other rows in the same table do carry the column — see "Rendering" below. Within one origin, symbols this
solution declares come before BCL/NuGet ones, so a crowded cursor does not spend its budget
alphabetically in the `A`s of the referenced assemblies. Drop `receiver` to ask what is in scope at that
line generally rather than on one expression.

## Reference

Replaces: guessing a helper name, or grepping for one that may not apply at this position. Grep cannot
answer this at all — an extension method shares no text with its call site.

**Not the same question as `get_symbol`'s `members`.** `members` is a static, position-free list of
what a *type declares* — reach for it once you already know the type. `get_scope` is
position-sensitive: it also surfaces inherited members, locals/parameters in scope at that exact
line, and extension methods applicable to a receiver, none of which `members` returns. Call `get_scope`
when you're standing at a cursor deciding what to call — before writing a helper that may already
exist, or when you don't yet know a receiver's type so `get_symbol` has no target to query.

| Arg | Meaning |
|---|---|
| `file`, `line`, `column` | Required position (column defaults to 1). |
| `receiver` | Optional variable/expression — narrows to what's callable *on it*, including applicable extension methods. |
| `filter` | `all` (default) \| `methods` \| `properties` \| `locals` \| `types`. |
| `nameContains`, `limit` | Narrow a large result. `limit` defaults to 40 (cap 200) and is spent **across origins**, round-robin, so a receiver's own members cannot crowd out the applicable extension methods this tool exists to surface. A capped result carries `totalItems` and `truncated: true`. |

Real call and response (trimmed):

```
get_scope(file: "src/DotnetToolkit.McpServer/Tools/PatchTools.cs", line: 182,
          receiver: "featureLog", filter: "methods")
```

```json
{"receiverType":"FeatureLogStore","totalItems":63,"truncated":true,
 "items":[
   {"displayString":"Append(LogEntry entry)","kind":"Method"},
   {"displayString":"EntryCount()","kind":"Method"},
   {"displayString":"Equals(object? obj)","kind":"Method",
    "origin":"inherited","definedIn":"object"}]}
```

`displayString` has its containing type's prefix stripped, since `definedIn` states it when it differs
from the header. `origin` separates what the type itself declares from what it inherits, which is
usually the first thing you want to know. **Both `origin` and `definedIn` are omitted when the
`receiverType` header already implies them** — `definedIn == receiverType` makes `origin: "member"`
derivable, and restating the header on every row cost 39% of a measured response. `definedIn` is also
omitted on a **local or parameter**, which has no declaring type for the field to describe, and on a
**type-kind** row it carries that type's namespace (or its outer type, when nested) — that row's own
home, rather than the empty field it used to be.

"Omitted" is exact in JSON. In the default TOON rendering the rows are **tabular**, so one row carrying
a `definedIn` gives the whole block the column and the rows without one render as `""` — that empty
cell means absent, not a symbol defined nowhere. Ask for `json` via `set_output_format` if the
distinction matters to a parser.

Locals and parameters are restricted to the queried file's own syntax tree. Roslyn's `LookupSymbols`
returns the synthesized top-level-statements entry point's locals at **every** position in the
compilation, so before that filter a cursor anywhere in this repo was told `Program.cs`'s `builder` and
`app` were in scope. If a local you expect is missing, check it is declared in the file you asked
about — one in a different file was never really callable there.

When more is in scope than `limit` allows, the budget is spent round-robin across origins rather than
alphabetically, so applicable extension methods appear alongside the receiver's own members instead of
being crowded out; `totalItems` and `truncated` then report what was left out. Within one origin,
source-declared symbols sort ahead of metadata ones: at a cursor with 919 symbols in scope, ordering by
name alone spent the type share of the budget on `AbandonedMutexException` and friends rather than on
anything the caller was choosing between. Drop `receiver` to see
what's in scope at that line generally. (The line number above
tracks a real call site in this file; if a future refactor moves it, re-find the receiver with
`search_index`/`get_symbol` rather than assuming this line still resolves.)

## Next steps

- **Found the member you wanted** → `get_symbol` on its `displayString` — `get_symbol.md`
- **Already know the type name** → `get_symbol(include: "members")` is cheaper and needs no position
