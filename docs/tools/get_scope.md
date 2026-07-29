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
 "items": [{"displayString": "string FeatureLogStore.Append(LogEntry entry)",
            "kind": "Method", "origin": "member", "definedIn": "FeatureLogStore"},
           {"displayString": "int FeatureLogStore.EntryCount()", "origin": "member"},
           {"displayString": "bool object.Equals(object? obj)", "origin": "inherited"}]}
```

`origin` separates what the type itself declares from what it inherits — usually the first
thing you want to know. Drop `receiver` to ask what is in scope at that line generally
rather than on one expression.

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
| `nameContains`, `limit` | Narrow a large result. |

Real call and response (trimmed):

```
get_scope(file: "src/DotnetToolkit.McpServer/Tools/PatchTools.cs", line: 182,
          receiver: "featureLog", filter: "methods")
```

```json
{"receiverType":"FeatureLogStore",
 "items":[
   {"displayString":"Append(LogEntry entry)","kind":"Method","definedIn":"FeatureLogStore"},
   {"displayString":"EntryCount()","kind":"Method","definedIn":"FeatureLogStore"},
   {"displayString":"Equals(object? obj)","kind":"Method",
    "origin":"inherited","definedIn":"object"}]}
```

`displayString` has its containing type's prefix stripped — `definedIn` already states it on every row,
so repeating it in both places was pure repetition. `origin` separates what the type itself declares
from what it inherits, which is usually the first thing you want to know; it is omitted when it would
just be `"member"` alongside a `receiverType` header, since `definedIn == receiverType` already says
that. Drop `receiver` to see what's in scope at that line generally. (The line number above
tracks a real call site in this file; if a future refactor moves it, re-find the receiver with
`search_index`/`get_symbol` rather than assuming this line still resolves.)

## Next steps

- **Found the member you wanted** → `get_symbol` on its `displayString` — `get_symbol.md`
- **Already know the type name** → `get_symbol(include: "members")` is cheaper and needs no position
