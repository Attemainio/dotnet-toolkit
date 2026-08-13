# `get_references` — callers, implementations, overrides

Replaces: grep for a name. Grep cannot see interface, virtual or delegate dispatch, counts comment and
string matches as hits, and silently drops sites when output is truncated.

| Arg | Meaning |
|---|---|
| `symbol` | Required. Same addressing as `get_symbol`. |
| `direction` | `callers` (default) \| `implementations` \| `overrides`. An unrecognized value falls back to `callers` and the response carries `directionHint` naming what it probably was. |
| `limit` | Max items per page (default `50`, cap `200`). Lower it when a few worked examples are enough — a high-fan-in symbol's full page is the most expensive response this server produces. |
| `offset` | Items to skip before `limit` (default `0`). Pass the previous response's `nextOffset` to reach the rest. |
| `includeBodies` | Inline each caller's source as `content: [{line, text}]` — same per-line shape as `get_symbol`'s `source`, including the `toon`-format raw-block rendering (default `false` — fetch bodies only for the ones you'll actually edit). |
| `fields` | Comma list of extras beyond the default `symbolId`/`displayString`/`sites`: `contentVersion` (this item's own version, for leasing it independently — rarely needed), `signature` (the full parameter-list `displayString` instead of the default compact name/arity form), `crefs` (also return the XML-doc `<see cref="..."/>` sites excluded by default, each tagged `kind:"cref"`). |

Real call and response (trimmed):

```
get_references(symbol: "FeatureLogStore.Append")
```

```json
{"targetSymbolId":"sym_c25d7c88b0e916b0",
 "items":[
   {"symbolId":"sym_0e0e...",
    "displayString":"DevlogMigration.Run/3",
    "sites":[{"file":"src/DotnetToolkit.McpServer/Devlog/DevlogMigration.cs","line":29,
              "snippet":"log.Append(new FeatureLogStore.LogEntry("}]},
   {"symbolId":"sym_2b15...",
    "displayString":"PatchTools.AppendLog/6",
    "sites":[{"file":"src/DotnetToolkit.McpServer/Tools/PatchTools.cs","line":190,
              "snippet":"featureLog.Append(new FeatureLogStore.LogEntry("}]}],
 "dispatchKind":"direct",
 "totalItems":2,"excludedTextMatches":1}
```

`dispatchKind` is reported once at the top level, not per item — it describes the *target* symbol's own
dispatch (direct/virtual/interface/delegate), which cannot vary across items within one call. It is
**omitted entirely for a class, record, struct or interface root**: those have no call sites of their
own, so this direction reports the members that *reference* the type and there is no dispatch to
describe — emitting `direct` there stated a fact about nothing and read as a claim the references were
non-virtual. A **delegate** type keeps its kind, because `delegate` is a true statement about how the
members returned actually invoke it. Each item carries `symbolId, displayString, sites` on every call;
`contentVersion` (with `fields:"contentVersion"`), `isTest` (emitted only when `true`) and `content`
(with `includeBodies:true`) are present only when they apply — absent, not `null`, otherwise.
`excludedTextMatches` is the count of comment/string matches a grep would have wrongly included — 1
here, correctly excluded. `targetSymbolId` and `targetDisplayString` are omitted when `symbol` was
already a `sym_...` id, since they would only restate the input.

### `testInvocationHint` — a 0-caller test method is not evidence of dead code

Present only on a `direction: "callers"` call where `totalItems` is `0` **and** the target itself
carries a recognized test-framework attribute (`[Fact]`, `[Theory]`, `[Test]`, `[TestMethod]`, and
their NUnit/MSTest siblings — the same set `TestAttributes.IsTestMethod` uses to mark a *caller* as
`isTest`, applied here to the *target*). A test runner discovers and invokes such a method by
reflection, which leaves no call-site edge for any static reference search — including this one — to
find. The zero this tool reports is real, but it answers "is there a static call site", not "is this
used"; reading it as "safe to delete" is a confirmed failure mode, not a hypothetical one — a blind
A/B benchmark of this tool against plain-text search on this repo (`.claude/dotnet-toolkit/perf/`)
caught exactly this: the MCP route's `get_references` returned 0 callers for a `[Fact]` test method
and concluded it was safe to delete, while a route that read the file's text saw the attribute and
correctly refused to. This field closes that gap by surfacing the same caveat a human would catch from
reading the source, without requiring a second call.

```
get_references(symbol: "WorkspaceIntegrationTests.ReferenceCounts_TestsNeverExceedCallers")
```

```
totalItems: 0
testInvocationHint: "0 callers, but this method carries a test-framework attribute recognized as a
  reflection-invoked entry point ([Fact]/[Theory]/[Test]/[TestMethod] or similar). Test runners
  discover and call it by reflection, which leaves no call-site edge for this tool to index — zero
  references here is not evidence it is unused."
```

Absent on every other call shape: a non-`callers` direction, a target with any real caller, or a
target that carries no recognized test attribute — an ordinary 0-caller method still reads as
ordinary dead code, uncaveated.

**A call made from inside a local function or a lambda is attributed to the member that encloses it.**
Roslyn names the local function as the caller, but a local function is not a fetch target: it is not in
the symbol index, so `get_symbol` answers `symbol_not_found` for the very handle this tool just handed
out, and its documentation-comment id is minted as though it were a member of the containing *type* —
four same-signature `Fail` helpers in four different methods of one class all collapsed onto one id,
simultaneously ambiguous and dead. Attributing upward makes every row a real `get_symbol` target, and
nothing is lost: each `site` still carries the exact file, line and source line of the call itself. A
member that reaches the target both directly and through a local function inside it is one item with
both sites, not two items.

### Which symbol did it actually answer for

`targetSymbolId` is a hash, so it confirms the binding only to a caller who looks it up.
**`targetDisplayString` names the resolved symbol in full.** A `symbol` handle that is a bare name or a
suffix can bind to something you never meant — a same-named member on an unrelated type, or a different
overload — and the resulting caller list looks exactly as authoritative as a correct one. Read this
field before trusting a surprising count; it is the cheapest check available, and the failure it
catches is the one that makes a confident answer wrong rather than thin.

### Direct calls vs. cascaded ones

Roslyn's reference search **cascades**: a call written against a base or interface declaration is
reported as a caller of every symbol that overrides or implements it, because it might dispatch there
at runtime. Those sites carry **`indirect: true`**, and the envelope splits the totals into
**`directItems`** and **`indirectItems`** whenever any indirect site is present (both are omitted when
every site is direct, so the common case costs nothing).

This matters most on an `override` with a common name. A large total under `dispatchKind: virtual` is
not evidence of a large real fan-in: it may be mostly cascaded sites that only reach this symbol when
the receiver's runtime type is this one. Read `directItems` first, and treat `indirectItems` as
"could land here", not "does".

### Paging a high-fan-in symbol

`totalItems` is always the **full** count, not the page's. When it exceeds what came back, `truncated`
is set and `nextOffset` names the offset that reaches the rest; `offset` echoes back any page you asked
past. So a symbol with hundreds of referencing members is fully retrievable, one page at a time — where
a fixed cap left everything past it unreachable by any argument while still charging a caller who
wanted three for the whole page.

```
get_references(symbol: "Sample.Lib.IWidget", limit: 2)      → totalItems: 3, nextOffset: 2, truncated: true
get_references(symbol: "Sample.Lib.IWidget", limit: 2, offset: 2)  → offset: 2, the remaining item
```

At high fan-in, weigh this against `get_call_hierarchy(maxDepth: 1)`, which returns the caller list
without the per-site snippets — roughly an eighth of the tokens at ~105 callers. Below about a dozen
callers it inverts: `get_references` gives you the sites for free.

### What a site is, and what is not one

**One row per `{file, line}`.** A line naming the symbol several times — a multi-parameter signature, a
tuple return type that names its own interface — is one site, and its `snippet` is that whole line, so
nothing the repeats carried is lost. Emitting a byte-identical row per occurrence cost a 585-line fluent
interface ~1,700 tokens of pure repetition on one call.

**XML-doc `cref` mentions are excluded by default.** Roslyn binds a `<see cref="IWidget"/>` to the
symbol, so `FindReferences` hands doc comments back among the real sites — the same category as the
comment and string matches this tool refuses to return, arriving by a different route. They are dropped,
counted as `excludedDocMentions` (present only when non-zero), and an item left with nothing but doc
mentions goes with them. On a heavily documented API this is the difference between roughly half the
response being comments and none of it being. Pass `fields: "crefs"` to get them back, each tagged
`kind: "cref"` so code sites stay distinguishable.

`includeBodies:true`'s `content` is the same `[{line, text}]` shape as `get_symbol`'s `source` (shown
here as `format:"json"`, trimmed to one caller):

```
get_references(symbol: "SearchText.CamelParts", includeBodies: true)
```

```json
{"targetSymbolId":"sym_bfdafc...",
 "items":[{"symbolId":"sym_528271...",
   "displayString":"SearchText.ForQuery/1",
   "sites":[{"file":"src/DotnetToolkit.McpServer/Store/SearchText.cs","line":50,
             "snippet":"foreach (var candidate in new[] { segment }.Concat(CamelParts(segment)))"}],
   "content":[
     {"line":43,"text":"    public static string? ForQuery(string query)"},
     {"line":54,"text":"                // Prefix match, so \"Ledg\" still finds \"Ledger\". Quoted to keep FTS5 from reading a"},
     {"line":56,"text":"                terms.Add($\"\\\"{candidate.Replace(\"\\\"\", \"\\\"\\\"\")}\\\"*\");"}]}]}
```

Under the default `toon` format, that same `content` renders as the raw block described above —
a line building a doubled-up quoted string literal, unescaped exactly as the file reads:

```
content:
  43: public static string? ForQuery(string query)
  ...
  54:                 // Prefix match, so "Ledg" still finds "Ledger". Quoted to keep FTS5 from reading a
  55:                 // term as one of its operators (NOT, OR, NEAR) or choking on a stray character.
  56:                 terms.Add($"\"{candidate.Replace("\"", "\"\"")}\"*");
```

## Named types (including delegates)

A class, record, interface or delegate has no call sites of its own, so `callers` on one answers the
question that *was* asked — which members reference the type — rather than an empty list: the field,
parameter, return type, event declaration or construction site, one item per referencing member.

The delegate below still reports `dispatchKind: delegate`, since that describes how those members
invoke it. A class or interface root reports **no** `dispatchKind` at all — see above.

```
get_references(symbol: "Sample.Lib.Transform")
```

```
targetSymbolId: sym_9a97...
items[3]{symbolId,displayString,sites}:
  ...,"DelegateSample.Apply/2",[{Lib/DelegateSample.cs,34,"public int Apply(Transform transform, int value)"}]
  ...,"DelegateSample.Applied",[{Lib/DelegateSample.cs,23,"public event Transform? Applied;"}]
  ...,"DelegateSample.Describe/2",[{Lib/DelegateSample.cs,45,"public string Describe(Projector<int, string> projector, int value)"}]
dispatchKind: delegate
```

For an interface, `implementations` still answers "who implements it" — `callers` answers the different
question of who merely mentions the type.

### A typo'd `direction`

A value that matches none of `callers`/`implementations`/`overrides` falls back to `callers`
silently — a typo of the non-default value gets you the wrong answer, not an error. When that
happens the response carries `directionHint`, e.g. for `direction: "implementaton"`:

```
directionHint: direction:'implementaton' was not recognized and defaulted to 'callers'. Did you mean 'implementations'?
```

Absent whenever `direction` matched, so an ordinary call carries no extra field.

## Next steps

- **Need the tree, not one hop** → `get_call_hierarchy` — `get_call_hierarchy.md`
- **Need the path to one known destination** → `get_call_slice` — `get_call_slice.md`
- **Editing every caller** → one `validate_patch` call per touched symbol, sharing one `intent` — `validate_patch.md`
