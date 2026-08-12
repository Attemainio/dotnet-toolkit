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
single-term query is skipped; its empty `items` already says the same thing — except when that one
term is itself a kind/modifier keyword, which `hint` covers instead (below).

### Hit shape

Every hit: `symbolId, name, kind, file, line, endLine`, plus `shape` and — when the default fetch
is not the right next call — `read`. `file`/`line` are resolved live at
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

**`C` is a fact at any count, but only worth acting on above roughly `C10`.** `source:code-comments`
(dropping just the comments, keeping doc comments and code) saves one token per comment line — real,
but at `C1`–`C5` the saving rounds to nothing next to the call itself. `P`, by contrast, names no
route at all: it disambiguates an overload set at the point of choosing a `get_symbol` target, which
is a real use, but no `include` answers "which overload" more cheaply than picking the right
`symbol` argument in the first place — treat it as a fact, not advice, the way `read`'s absence is.

### The `read` column — which `include` to pass next

`shape` states the facts and leaves the inference to you. `read` is that inference already made: one
token per hit naming the `get_symbol` include to call with, legend stated once per response, the
same way `shape`'s is.

| Value | Next call |
|---|---|
| `mem` | `get_symbol(include: "members")` — navigate by member list, not a full-type read |
| `out` | `get_symbol(include: "bodyOutline")` to map it, then `source:code@from-to` for the region |
| `code` | `get_symbol(include: "source:code")` — one linear block, docs stripped |
| `all` | `get_symbol(include: "all")` — the body-carrying `contentVersion` a patch needs |
| *absent* | the default fetch is already the right call |

**Absent is an assertion here, not a blank.** It is the second deliberate exception to "absent
carries no information" (`shape`'s letters are the first), and it is what keeps the column
affordable: a result of nothing but small symbols renders exactly as it did before `read` existed —
no column, no legend.

The column is cheap rather than free, though. Once *any* hit carries advice, the tabular form gains
the column for every row, and the silent ones render an empty cell:

```
items[3]{symbolId,name,line,endLine,shape,read}:
  sym_5b5c…,ContextTools,25,2480,M83 N4 L2456 D434,mem
  sym_8aca…,ReadAdvice.Legend,33,35,L3 D6,""
```

So the real cost is one legend plus one cell per hit on a mixed result, and nothing at all on a
result where the default fetch is right throughout.

`read` is deliberately redundant with `shape` — it carries no fact `shape` does not already carry.
It exists because a derivation nobody performs is not information: `L2342 M87` reliably reads as a
description rather than as "fetch the member list", and the whole 2342-line type gets fetched anyway.

### `intent` — aiming the recommendation

Without it, `read` is derived from each hit's own size and structure, and stays silent below ~60
lines. Passing what you are about to do overrides that, because your intent is a fact the shape
cannot contain:

| `intent` | Effect |
|---|---|
| `edit` | every hit reads `all` — a body patch needs the body-carrying lease whatever the symbol looks like |
| `logic` | behaviour, not docs: `code` at any size, or `out` on a long branching body |
| `surface` | the API shape: `mem` on a type, silent on everything else (the default fetch already leads with the signature) |

An unrecognized value is treated as omitted, and the response carries `intentHint` naming what it
probably was. `N…` and `A…` have no `read` route of their own —
nested types are separate symbols reached by `get_scope` or a `pathPrefix` search, and
`include: "attributes"` remains the cheap `[Authorize]`/`[Obsolete]` check to ask for directly.

The thresholds behind the no-intent path are the one guessed part of this. Analysis 3d in
`dotnet-selfeval` exists to price advice like this — follow the column, ignore it, compare — and is
what should move them.

## `query` is name text, not structure

`query` terms match against **identifier text** — type, member and file names — never against what a
symbol structurally *is*. `query: "class"` or `query: "partial class"` searches for something literally
named `class`; it does not mean "list every class" or "list every partial type", and returns nothing
whenever this codebase happens to have no symbol named that. A structural question has its own filter,
and passing it costs nothing extra alongside a real term in `query`:

- "is a class/interface/struct/record/…" → `kinds`
- "is partial/static/public/abstract/async/…" → `modifiers`
- "is nested" → not a filter — read it off a hit's `shape` (the `N` count), or fetch the containing
  type's `members` with `get_symbol`

**`query` also matches namespace segments, not only type/member/file names** — `query: "WebResearch"`
against a symbol whose name carries no such substring still hits every member under namespace
`…WebResearch`, and this is often the cheapest handle on a whole subsystem when you know the area but
not a specific name in it. `pathPrefix` narrows by folder; a namespace term in `query` narrows by
namespace, without needing to already know a real identifier inside it.

Never substitute a structural word for the term you don't have yet — that produces exactly the
`search_index(query: "partial class", kinds: "class", modifiers: "partial")` call that returns
`termsWithNoHits: ["partial","class"]` and nothing else: both words describe *shape*, and neither is
text any real symbol carries. `kinds`/`modifiers` still narrow rather than replace `query` (see below).

**`hint` catches the case `termsWithNoHits` doesn't.** When `query` is built *entirely* from words
that read as a kind or modifier keyword (`"interface"`, `"partial class"`, `"asyncdisposable"`, …)
and the result is empty, the response carries a `hint` string pointing at `kinds`/`modifiers` —
because for exactly this query shape, an empty `items` list on its own reads as "no such symbols
exist" rather than "you searched for a filter word, not a name". A query mixing in even one real
identifier or domain term gets no `hint` on a zero-hit result — that's an ordinary failed search, not
this misuse.

**Don't know this codebase's vocabulary yet?** `query` only finds identifiers that already exist here —
a real class/method/field name, or a substring of one. Before the first `search_index` call in an
unfamiliar repo, `Read` its `README.md` for domain nouns and component names; if there is no
`README.md`, `Read` `CLAUDE.md` instead — both are plain Markdown, outside this skill's `.cs` scope.
Query with the nouns that document names, not the structural shape you're hoping to filter on.

## Filters

**Every filter narrows a search; none of them is one.** `query` carries the terms, and passing it empty
or whitespace returns `error: "missing_query"` rather than searching on the filters alone. Answering that
with an empty item list would be worse than the error: it reads as "no such symbols exist", which is the
silent under-report `termsWithNoHits` exists to prevent, reached through the arguments instead of through
the index.

Omitting `query` altogether is a different failure and not one this server can improve: the argument is
**required in the tool schema**, so the MCP host rejects the call before it is dispatched and returns its
own opaque `An error occurred invoking 'search_index'`. That requirement is deliberate and worth more than
a better message would be — it is what tells a caller the argument is mandatory in the first place. If you
see that error, you omitted `query`.

| Arg | Grammar | Notes |
|---|---|---|
| `kinds` | bare tokens **OR** (a symbol has one kind); `-token` excludes; mixing forms, bare wins | `class`/`type`, `interface`, `struct`, `record`, `enum`, `delegate`, `method`, `property`, `field`, `event`. An unrecognized token matches no symbol; when that leaves zero hits the response carries `kindsHint` |
| `modifiers` | bare tokens **AND** (a symbol carries several at once) — opposite of `kinds`; `-token` excludes and combines | literal C# keywords (`public`, `static`, `readonly`, `sealed`, `override`, `async`, `partial`, …) + derived tags `extension`, `indexer`, `initonly`, `disposable`, `asyncdisposable`. Same zero-hit `modifiersHint` as `kinds` |
| `implements` | narrows ranked hits like `pathPrefix` — `query` still needs a real term | direct implementers only (not transitive); unresolvable name → empty result, not error |
| `pathPrefix` | folder/file, repo-root-relative, forward slashes, matched on a path-segment boundary | a hit whose file can't resolve (ambiguous overload) is dropped, not guessed, so an overload-heavy query can undercount. Ranking runs over the whole index before scoping — narrow the query text if a far-more-hits-outside case returns fewer than `limit` |
| `xmlDoc` | same AND/exclude grammar as `modifiers` | tokens: `summary`, `returns`, `remarks`, `value`, `inheritdoc`, `params`, `typeparams`, `exceptions` — which sections a doc comment carries beyond plain `<summary>` presence |
| `origin` | — | `"source"` (default, this repo's own declarations) \| `"external"` (BCL/NuGet already referenced from this repo's source — not a general library browser) \| `"all"`. An external hit has no `file`/`line`; follow with `get_symbol` on its `symbolId`. An unrecognized value falls back to `"source"` and the response carries `originHint` |
| `summary` | — | `"has"` adds `hasSummary` (bool, cheap presence check) \| `"full"` adds `summary` (text, capped 160 chars). Read from the syntax index — free even at `index_only`. An unrecognized value is treated as omitted and the response carries `summaryHint` |
| `refs` | — | `"counts"` adds `callers` to every hit — **including `0`** — and `tests` when above zero. One batched index lookup for the whole page, not one per hit. An unrecognized value is treated as omitted and the response carries `refsHint` |
| `groupBy` | — | `"namespace"` (namespace→file→symbols) \| `"file"` (file→namespace→symbols) \| `"none"` (flat, `file`/`kind` repeated per row). **Omit it** — the server renders both shapes and keeps whichever costs fewer tokens; an explicit value is always honored as given. Whichever axis fully collapses to one value flattens its wrapper to a header field, and a leaf's `kind` drops when every hit there shares one kind. An unrecognized non-null value is treated as `"namespace"` and the response carries `groupByHint` |
| `limit` | — | default 10, cap 200 |

**`refs: "counts"` is how "is this dead code?" becomes one call.** `callers` is emitted even at `0`,
because that zero *is* the answer being asked for; suppressing it would hide the only result worth the
argument. The counts come from the same call edges `get_references` resolves, so a symbol reached only
through an interface or a virtual is counted — a `grep` for the name would miss those and report a live
symbol as unused.

**An absent `callers` is not zero.** When no reference index is available the counts are omitted
entirely and `refsHint` says so. Reading that silence as "nothing uses it" is how live code gets
deleted, which is why the two cases are distinguishable rather than both rendering as `0`.

**`summary: "has"` is usually redundant against `shape`'s `D` count**, which is already present on
every hit: measured, `hasSummary` agreed with `D > 0` on every hit checked. `D` counts doc *lines*
though, so a symbol carrying only `<remarks>` and no `<summary>` could in principle break the
equivalence — pass `"has"` only if you've actually seen that happen. `"full"` is the one that earns
its own call: the summary *text* isn't in `shape` at all.

`hint` is a response field, not an argument: present only when `query` is built entirely from
kind/modifier keywords and `items` came back empty — see above. `kindsHint`/`modifiersHint` follow the
same zero-hit gate for an unrecognized token in those two filters specifically. `originHint`,
`summaryHint`, `refsHint`, `groupByHint` and `intentHint` are simpler and unconditional: present whenever that
argument was supplied and didn't match its own vocabulary, regardless of how many hits came back — each
names the value that wasn't recognized, what it was silently treated as, and a `didYouMean`-style
suggestion when exactly one vocabulary token is a close-enough match. All six are additive: the call
still succeeds and returns the same fallback behavior it always has, only now with a signal that a
fallback happened.

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
