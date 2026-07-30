# `search_index` — find symbols when you don't know exact names

## When to reach for it

`search_index` OR-es its terms and ranks the results, so one call answers for many names:

```
search_index(query: "fee ledger TryBuy TrySell")     ← one call, all four
```

Not this:

```
search_index(query: "fee"); search_index(query: "ledger"); ...   ← several × the tokens
```

Partial and camel-case-interior terms match: `Ledger` finds `FIFOLedger`, `Try` finds
`TryBuy`. When a question spans two subsystems, name both in the same query — the ranking
puts the symbols matching more of your terms first, which is exactly the overlap you want.

Each hit carries where it was found, so going straight there costs no second call. `items` is a
plain array of objects — `symbolId, name, kind, file, line, endLine` on every hit:

```json
{"items":[{"symbolId":"sym_...","name":"Sample.Lib.WidgetExtensions.SpinTwice(IWidget,int)",
           "kind":"Method","file":"Lib/Pipeline.cs","line":6,"endLine":6}]}
```

`file`/`line` are resolved from the syntax index at response time — swept for staleness on the
way — so they point at where the declaration is *now*, not where it was when the row was
written. An overload set is separated by **parameter count**, and members colliding on count too are
separated by their **parameter types**, so every member of an overload set reports its own location. A
hit that stays ambiguous even then — a name whose types reduce to different text on the two sides (one
side spelling `Int32` where the other spells `int`), or a caller with no parameter list to offer —
**omits both fields entirely** (absent, not `null`) rather than pointing at the wrong one. It still
resolves through `get_symbol`, which separates overloads by full parameter list and always returns exact
spans.
`endLine` is the declaration's own last line (trailing trivia excluded) — a cheap signal for
whether `get_symbol`'s `source` component is worth requesting on this hit before asking for it, or
whether `mechanicalFacts`/`xmlDoc`/`referenceCounts` alone would do for a large declaration, or
whether a large `endLine - line` span is worth mapping with `bodyOutline` first (see "When to reach
for `bodyOutline`" below) before deciding how much of `source` to fetch.

Only split into separate calls when you need different `kinds` filters.

Narrow to one subsystem with `pathPrefix` (folder or file, repo-root-relative, forward slashes)
instead of filtering the whole-repo result yourself:

```
search_index(query: "Search", kinds: "method", pathPrefix: "src/DotnetToolkit.McpServer/Store")
```

A hit whose `file` can't be resolved (an overloaded name — see above) is dropped rather than
guessed into scope, so an overload-heavy query can undercount. Ranking still runs over the whole
index before scoping, so a query with far more hits outside the prefix than fit an internal
overfetch cap can return fewer than `limit` even with more in-scope matches available — narrow the
query text itself if that happens, rather than raising `limit`.

### Filter by modifier or by interface

`modifiers` filters the same way `kinds` does — space/comma-separated tokens, `-token` to exclude —
but with the opposite combining rule for bare tokens: **AND, not OR**. A symbol carries several
modifiers at once (a method can be both `public` and `static`), so `modifiers: "public static"`
means both, not either — unlike `kinds`, where a symbol has exactly one kind and `"method
property"` reads naturally as "either of these". `-` tokens exclude and combine with the bare
tokens (`"public -sealed"` is public AND NOT sealed), rather than one replacing the other the way
`kinds`' mixed form does. Valid tokens are the literal C# keywords (`public`, `static`, `readonly`,
`sealed`, `override`, `async`, `partial`, …) plus a few cheap derived tags that aren't keywords:
`extension`, `indexer`, `initonly`, `disposable`, `asyncdisposable`.

`implements` narrows to the direct implementers of a named interface — resolved the same way any
symbol name is elsewhere, an unresolvable name yields an empty result rather than an error. It
narrows the ranked `query` hits the same way `pathPrefix` does, so `query` still needs a real term:

```
search_index(query: "Widget", kinds: "class", modifiers: "public sealed")   ← AND: public AND sealed
search_index(query: "Widget", kinds: "class", implements: "IWidget")        ← direct implementers only
```

### Find a BCL/NuGet symbol this repo's own code references

`origin` defaults to `"source"` — only symbols this repo's own solution declares. `origin: "external"`
searches only BCL/NuGet symbols already discovered as a call, construction, or `implements` target from
this repo's own source — not a general library browser, only what the code here already references.
`origin: "all"` searches both.

```
search_index(query: "IDisposable", kinds: "interface", origin: "external")
```

A hit found this way carries no `file`/`line` (nothing in this repo declares it) — follow up with
`get_symbol` on its `symbolId`, whose response carries `origin: "external"` and an empty
`declarationSites`.

### Check documentation without a follow-up get_symbol call

Pass `summary` to fold an XML doc `<summary>` signal into the same search response — read from the
syntax index, so it costs nothing extra and works even at `index_only`:

- `summary: "has"` — adds `hasSummary` (bool) per item. The cheap check: is this hit even
  documented, before you decide whether it's worth a `get_symbol` round trip.
- `summary: "full"` — adds `summary` (the extracted text, capped at 160 characters with a trailing
  `…`) per item. Use it when judging whether a hit is actually the symbol you want, without paying
  for a separate fetch just to read its intent. The cap keeps one pathological doc comment from
  dominating a multi-hit response — once you've picked the symbol, `get_symbol`'s `xmlDoc.summary`
  gives you the untruncated text.
- Omit `summary` entirely for the default, unchanged response — no `hasSummary`/`summary` field on
  any item.

A hit with no `<summary>` doc comment has no `hasSummary` key at all (not `false`) — same
absent-means-absent convention as everything else in this skill.

### Filter by which XML doc sections are present

`xmlDoc` filters on whether a hit's doc comment carries specific sections beyond plain `<summary>`
presence (that's what `summary` checks) — same grammar as `modifiers`: bare tokens AND (a declaration
can carry several tags at once), a `-`-prefixed token excludes and combines with the bare tokens.
Valid tokens: `summary`, `returns`, `remarks`, `value`, `inheritdoc`, `params`, `typeparams`,
`exceptions`. Narrows the ranked `query` hits the same way `pathPrefix`/`implements` do:

```
search_index(query: "Widget", kinds: "method", xmlDoc: "returns -remarks")   ← has-returns AND NOT has-remarks
```

**If a symbol you are about to edit has no summary, see the `dotnet-change` skill** — a missing
summary on a symbol you touch is not just a gap to note, it's something `validate_patch` should fix
in the same edit.

## Reference

Replaces: `grep`/`Glob` over `.cs` files. Returns ranked symbols with ids and locations, not raw text
lines — nothing to hand-filter, no truncation to silently lose hits.

| Arg | Meaning |
|---|---|
| `query` | Free-text, OR-ed and ranked. **Put every term you want in one call**: `"fee ledger TryBuy TrySell"` returns all four in one ranked response — not four separate calls. |
| `kinds` | Optional kind filter, space- or comma-separated: `class`/`type`, `interface`, `struct`, `record`, `enum`, `delegate`, `method`, `property`, `field`, `event`. Bare tokens are an include-only filter (`"method property"` searches only those two). Prefix a token with `-` to exclude it instead (`"-struct -enum"` searches every kind except those two). Mixing both forms in one call: the bare tokens win and the `-` tokens are dropped, rather than combining. |
| `modifiers` | Optional modifier filter, space- or comma-separated: the literal C# keywords (`public`, `private`, `protected`, `internal`, `static`, `const`, `readonly`, `volatile`, `virtual`, `abstract`, `sealed`, `override`, `async`, `extern`, `partial` — `"private protected"`/`"protected internal"` also match their bare halves) plus a few cheap derived tags that aren't keywords: `extension`, `indexer`, `initonly`, `disposable`, `asyncdisposable`. **Unlike `kinds`, bare tokens are AND-ed, not OR-ed** — modifiers are multi-valued per symbol (`"public static"` means both), where kind is single-valued (so `kinds`' OR makes sense but wouldn't here). `-` tokens exclude and *combine* with the bare tokens rather than one replacing the other, e.g. `"public -sealed"` is public AND NOT sealed. |
| `implements` | Optional interface name — narrows to its direct implementers only (not transitive). Resolved the same way any symbol name is resolved elsewhere; an unresolvable name yields an empty result rather than an error. Narrows the ranked `query` hits the same way `pathPrefix` does — not a standalone browse-by-interface mode, so `query` still needs a real search term. |
| `pathPrefix` | Optional folder/file scope, e.g. `"src/Tools"` or `"src/Tools/ContextTools.cs"` — relative to the repo root, forward slashes, matched on a full path-segment boundary (`"Tools"` cannot match `"ToolsFoo"`). A hit whose file can't be resolved (an overloaded name) is dropped rather than guessed at, so scoped results can undercount for an overload-heavy query. Ranking still runs over the whole index first, so a query with far more hits outside the prefix than the internal overfetch cap can return fewer than `limit` even though more in-scope matches exist — narrow the query text itself if that happens. |
| `limit` | Default 10, cap 50. |
| `summary` | Optional XML doc `<summary>` signal per hit, read from the syntax index (no MSBuild needed, so it works at `index_only` too). `"has"` adds `hasSummary` (bool) — a cheap presence check with no text. `"full"` adds `summary` (string, the extracted text capped at 160 characters with a trailing `…`; absent if the symbol has no `<summary>`). The cap keeps a pathologically long doc comment from dominating a multi-hit response — fetch `get_symbol`'s `xmlDoc.summary` for the untruncated text once you've picked a symbol. Omit `summary` for the pre-existing response, byte-for-byte — no extra field, no extra cost. An unrecognized value is treated as omitted, same precedent as `kinds`' unrecognized tokens. |
| `xmlDoc` | Optional filter on which XML doc sections beyond plain `<summary>` presence a hit's declaration carries (that's what `summary` checks) — space/comma-separated tokens: `summary`, `returns`, `remarks`, `value`, `inheritdoc`, `params`, `typeparams`, `exceptions`. Same grammar as `modifiers`: bare tokens AND (a declaration must carry every included section, since a doc comment can carry several tags at once), and a `-`-prefixed token excludes and *combines* with the bare tokens rather than replacing them, e.g. `"returns -remarks"` is has-returns AND NOT has-remarks. Narrows the ranked `query` hits the same way `pathPrefix`/`implements` do — `query` still needs a real search term. Read from the syntax index, so it costs nothing extra and works at `index_only` too. Omit for no doc-section filtering. |
| `groupBy` | How results are nested. `"namespace"` groups namespace → file → symbols; `"file"` groups file → namespace → symbols; `"none"` returns the flat `items[]` list shown below, with `file`/`kind` repeated on every row and no `namespace` field. **Omit this parameter entirely** (rather than passing `"namespace"` explicitly) to let the server render both the flat and namespace-grouped shapes from the same data and keep whichever actually costs fewer tokens — grouping only pays for itself when hits concentrate onto few namespaces/files; on scattered results the nesting overhead can cost more than it saves. An explicit value is always honored as given, with no comparison. Whichever axis the whole result set collapses to a single value on additionally collapses its wrapper array to a flat `namespace`/`file` header field instead of a nested array, and a leaf's `kind` column drops out whenever every hit in that leaf shares one kind. An unrecognized non-null value is treated as `"namespace"`. |
| `origin` | `"source"` (default) searches only symbols this repo's own solution declares — existing callers see no behavior change. `"external"` searches only BCL/NuGet symbols already discovered as a call/construction/implements target from this repo's own source (`search_index(query: "IDisposable", kinds: "interface", origin: "external")` finds `System.IDisposable` once something here implements it) — not a general library browser, only what this repo's code already references. `"all"` searches both. An unrecognized value is treated as `"source"`. |

Real call and response, `groupBy: "none"` — the flat shape, `file`/`kind` repeated per row:

```
search_index(query: "validate_patch FeatureLogStore", limit: 5, groupBy: "none")
```

```json
{"items":[
   {"symbolId":"sym_dd78...","name":"DotnetToolkit.McpServer.Tools.PatchTools.ValidatePatch(...)",
    "kind":"Method","file":"src/DotnetToolkit.McpServer/Tools/PatchTools.cs","line":29,"endLine":151},
   {"symbolId":"sym_17cd...","name":"DotnetToolkit.McpServer.Store.FeatureLogStore.LogEntry",
    "kind":"Record","file":"src/DotnetToolkit.McpServer/Store/FeatureLogStore.cs","line":22,"endLine":24},
   {"symbolId":"sym_fc34...","name":"DotnetToolkit.McpServer.Store.FeatureLogStore",
    "kind":"Type","file":"src/DotnetToolkit.McpServer/Store/FeatureLogStore.cs","line":10,"endLine":260}]}
```

`name` is directly usable as `get_symbol`'s `symbol` argument — its parameter types are shortened but
their separating whitespace is kept, so `List<(DateTime time, decimal amount)>` and `params object[]`
read as the types they are (matching is whitespace-blind, so the shortened form still resolves).
Overloads are told apart by parameter count and then by parameter types, so each reports its own
`file`/`line`; a hit that stays ambiguous carries no `file` at all rather than pointing at the wrong one
— resolve those through `get_symbol`, which separates overloads by full parameter list. `endLine` is the declaration's own
last line (trailing trivia excluded, leading doc comment excluded — so it stays comparable to `line`,
which never counts the doc comment either) — a cheap size signal for judging whether `get_symbol`'s
`source` component is worth requesting before asking for it. `ValidatePatch` spans over a hundred
lines; `LogEntry` is a three-line record — a caller can tell the two apart without a round trip.

The same shape of query with the default `groupBy` omitted — a real capture against the fixture
solution, `"Spin"` matching four methods across two files under one namespace. The namespace and
each file are stated once instead of on every row, and `kind` hoists to each file's own header
since every hit in that file is a `Method`:

```
search_index(query: "Spin", kinds: "method")
```

```json
{"groupedBy":"namespace","namespaces":[{"name":"Sample.Lib","files":[
   {"path":"Lib/Pipeline.cs","kind":"Method","symbols":[
      {"symbolId":"sym_e5da...","name":"WidgetExtensions.SpinTwice(IWidget,int)","line":6,"endLine":6}]},
   {"path":"Lib/Widget.cs","kind":"Method","symbols":[
      {"symbolId":"sym_a87e...","name":"Widget.Spin(int)","line":12,"endLine":12},
      {"symbolId":"sym_ab80...","name":"IWidget.Spin(int)","line":5,"endLine":5},
      {"symbolId":"sym_0b3a...","name":"TurboWidget.Spin(int)","line":18,"endLine":18}]}]}]}
```

`groupBy: "file"` inverts the nesting to file → namespace → symbols instead. And when a query's
whole result set shares one namespace *and* one file — `limit: 1` on the query above, isolating
just `SpinTwice` — both wrapper arrays collapse to flat header fields, since there is nothing left
to nest:

```
search_index(query: "Spin", kinds: "method", limit: 1)
```

```json
{"namespace":"Sample.Lib","file":"Lib/Pipeline.cs","kind":"Method",
 "symbols":[{"symbolId":"sym_e5da...","name":"WidgetExtensions.SpinTwice(IWidget,int)","line":6,"endLine":6}]}
```

Scoped to one folder — `pathPrefix` narrows the same ranked search to a subsystem instead of the
whole repo:

```
search_index(query: "Search", kinds: "method", pathPrefix: "src/DotnetToolkit.McpServer/Store", limit: 5, groupBy: "none")
```

```json
{"items":[
   {"symbolId":"sym_6c0b...","name":"DotnetToolkit.McpServer.Store.SearchText.Segments(string)",
    "kind":"Method","file":"src/DotnetToolkit.McpServer/Store/SearchText.cs","line":63,"endLine":80},
   {"symbolId":"sym_a487...","name":"DotnetToolkit.McpServer.Store.SearchText.ForIndex(string)",
    "kind":"Method","file":"src/DotnetToolkit.McpServer/Store/SearchText.cs","line":18,"endLine":34}]}
```

Filtering by modifier and by interface — `"Widget"` matches both `Widget` and `TurboWidget`;
`modifiers: "public sealed"` (AND, not OR) narrows to the one that is both:

```
search_index(query: "Widget", kinds: "class", modifiers: "public sealed", limit: 5, groupBy: "none")
```

```json
{"items":[
   {"symbolId":"sym_...","name":"Sample.Lib.TurboWidget","kind":"Type",
    "file":"Lib/Widget.cs","line":16,"endLine":19}]}
```

`implements` narrows to direct implementers of a named interface instead — both widgets implement
`IWidget`, so both come back:

```
search_index(query: "Widget", kinds: "class", implements: "IWidget", limit: 5, groupBy: "none")
```

```json
{"items":[
   {"symbolId":"sym_...","name":"Sample.Lib.Widget","kind":"Type","file":"Lib/Widget.cs","line":9,"endLine":13},
   {"symbolId":"sym_...","name":"Sample.Lib.TurboWidget","kind":"Type","file":"Lib/Widget.cs","line":16,"endLine":19}]}
```

Checking documentation coverage before spending a `get_symbol` call — `summary: "has"` is the cheap
presence check, no text sent:

```
search_index(query: "Spin", kinds: "method", summary: "has", groupBy: "none")
```

```json
{"items":[
   {"symbolId":"sym_...","name":"Sample.Lib.Widget.Spin(int)","kind":"Method",
    "file":"Lib/Widget.cs","line":12,"endLine":12,"hasSummary":true},
   {"symbolId":"sym_...","name":"Sample.Lib.WidgetExtensions.SpinTwice(IWidget,int)","kind":"Method",
    "file":"Lib/Pipeline.cs","line":6,"endLine":6}]}
```

`SpinTwice` has no `hasSummary` key at all (not `false`) — it has no `<summary>` doc comment. Ask for
the actual text with `summary: "full"` instead of a follow-up `get_symbol`:

```
search_index(query: "Widget.Spin", kinds: "method", summary: "full", groupBy: "none")
```

```json
{"items":[{"symbolId":"sym_...","name":"Sample.Lib.Widget.Spin(int)","kind":"Method",
   "file":"Lib/Widget.cs","line":12,"endLine":12,"summary":"Spins the widget."}]}
```

Filtering by which doc sections a declaration actually carries — `xmlDoc: "returns"` matches only
methods documented with a `<returns>` tag, `-remarks` on top narrows further to ones that also lack a
`<remarks>` tag:

```
search_index(query: "Full ReturnsOnly Undocumented", kinds: "method", xmlDoc: "returns -remarks", groupBy: "none")
```

```json
{"items":[
   {"symbolId":"sym_...","name":"Sample.Lib.DocSectionsFixture.ReturnsOnly()","kind":"Method",
    "file":"Lib/Widget.cs","line":40,"endLine":40}]}
```

`Full()` carries both `<returns>` and `<remarks>`, so it's excluded once `-remarks` is added; plain
`xmlDoc: "returns"` (no exclude) would have returned both `Full()` and `ReturnsOnly()`, but not
`Undocumented()`.

## Next steps

You now hold `symbolId`s plus `file`/`line` for each hit. From here:

- **See a symbol's shape, docs or source** → `get_symbol` (pass the `symbolId` directly) — `get_symbol.md`
- **Find who calls it, with file/line per site** → `get_references` — `get_references.md`
- **Just the caller list, one hop** → `get_call_hierarchy(maxDepth: 1)` — cheaper — `get_call_hierarchy.md`
- **A type's base chain and every implementer** → `get_type_hierarchy` — `get_type_hierarchy.md`
- **About to edit it** → `get_symbol` for `contentVersion` + `declarationSites`, then `validate_patch` — `validate_patch.md`
