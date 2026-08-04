# `get_symbol` — a symbol's shape, docs, location, source

## When to reach for it

`get_symbol` takes one selector, `include`, instead of a resolution ladder. It has three forms:

1. **omitted, or `include: "standard"`** (default) — `xmlDoc`, `referenceCounts`, `recentLog`.
   The set meaningful on nearly every call. Start here.
2. **`include: "all"`** — every component below. Reach for this **only** when you already
   intend to edit the symbol, or genuinely want everything about it at once.
3. **an explicit comma list of component names** — e.g. `include: "source,members"`. This
   REPLACES the default set rather than adding to it: it is a literal query of exactly the
   columns you want, nothing implied. Use it whenever `standard`/`all` is close but not right.

Component names are exactly the response fields they control:

| Component | Returns |
|---|---|
| `source` | Full declaration source as `[{line, text}]`, one entry per physical line — not one `\n`-escaped string. Each `line` is an absolute file line, directly usable as a `validate_patch` `startLine`/`endLine` — surviving lines keep their real file number even after a modifier below drops others, never renumbered. Under the default `toon` format this renders as a raw, fully unescaped `line: text` block instead; `format:"json"`/`"compact"` keep the structured array. Suffix the component name with `:code` (`"source:code"`) to drop every `///` doc comment (the requested symbol's own and, for a type, each member's) and get just attributes + body — cheaper when you're about to edit and already have (or don't need) `xmlDoc`. Bare `"source"` is `"source:full"`. Either mode also takes `-modifier` suffixes to subtract further, e.g. `"source:full-remarks-attributes"` (drop just the `<remarks>` tag and all attributes, keep everything else) or `"source:code-comments"` (also drop `//` comments). Doc-tag modifiers (`summary`, `remarks`, `returns`, `value`, `inheritdoc`, `params`, `typeParams`, `exceptions` — same names as `xmlDoc`'s fields) only work under `full`; `attributes`/`comments`/`lineNumbers` work under either. `-lineNumbers` drops the per-line gutter and returns `[{lines, text}]` — one entry per contiguous run — instead; see the section below. No `+tag` exists — a query only ever subtracts from its mode's default, and only ever removes a *whole* line, never an attribute/comment sharing a line with real code. Either mode also takes an `@` line selector — see the row below. |
| `source@lines` | Not a separate component: `@` plus line ranges appended to `source` (after any mode/modifiers), returning only those lines. `"source@46-76"`, `"source:code@46-76;79-83"`, `"source:code-comments@60-"` (to the declaration's end), `"source@-50"` (from its start), `"source@52"` (one line). Ranges are **absolute file line numbers**, the same ones `declarationSites` and each line's own number report, so a span from any earlier response is reusable as-is; separate several with `;`, **not** `,` (that already separates component names). It is a pure filter — a line a `-modifier` dropped stays dropped even if a range names it, and nothing is ever renumbered. Past the declaration it clamps; entirely outside it returns no lines rather than erroring. A slice adds `sourceLines`: `"kept/whole"` (`"46-76/38-96"`, or `"none/38-96"` on a miss) and restores `displayString`/`modifiers`, since the signature line is usually not in the slice. Rejected with `symbols` (batch) as `lines_with_batch`. |
| `xmlDoc` | `{summary, returns, remarks, value, inheritdoc, params, typeParams, exceptions}`, each XML-stripped to plain text; a field is absent when that tag isn't present. `params`/`typeParams` are `[{name, text}]` from `<param>`/`<typeparam>`; `exceptions` is `[{type, text}]` from `<exception>`; `value` is a property's `<value>`; `inheritdoc` is `true` when `<inheritdoc/>` is present. `xmlDoc` itself is absent only when none of these tags are present at all — a doc comment with a `<returns>` but no `<summary>` still surfaces `xmlDoc.returns` |
| `mechanicalFacts` | Server-computed structural facts as opaque JSON; `null` if the body changed since computed. Members carrying nothing (an empty `throws`/`awaits`/`writes`/`locks`/`implementsMembers`, a null `overrides`) are omitted rather than emitted empty — absence means "none" |
| `bodyOutline` | Control-flow landmarks inside a method-like body — `switch`/`case`, `if`, `foreach`/`for`/`while`/`do`, `catch`, `using`, `lock` — for navigating a long body before deciding what to fetch. Purely syntactic (same cost tier as `source`, not `mechanicalFacts`' semantic-model tier). Under the default `toon` format this renders as a raw indented block, same treatment `source` gets: one `text,startLine,endLine` line per landmark, indented two spaces per nesting level instead of carrying a `depth` number — `format:"json"`/`"compact"` keep the flat `[{text, startLine, endLine, depth}]` array instead, since plain JSON has no indentation of its own to lean on. A bare `try`/`else`/`finally` has no name/condition of its own and is omitted; infer its span from the parent row. `text` is truncated to 28 characters with a trailing `..`, not summarized. Nesting counts among other landmark rows only, not raw syntax depth. Absent, with an explanatory `bodyOutlineNote`, for anything without an executable body of its own (a type, a field, an auto-property) — not a silent double-disappearance. `bodyOutlineNote` also appears (rows still returned) when the declaration is under 40 lines — see below. |
| `referenceCounts` | `{callers, tests}` for a member (never for a type), plus `{implementations, overrides}` — but each of those two only where the symbol's kind makes a non-zero answer possible: `implementations` for an interface, an unsealed class or an interface member; `overrides` for a virtual/abstract member. An enum, a static class or a plain method omits them rather than reporting a structural `0`, and the whole component is absent when nothing is left to report |
| `recentLog` | Last few dev-log entries touching this symbol, each flagged `current:true/false` against the live body |
| `members` | For a type only: `[{symbolId, displayString, kind, contentVersion}]` per member; `null` otherwise |
| `attributes` | This symbol's own (non-inherited) C# attributes as `[{name, arguments}]` — e.g. `[Authorize(Roles="Admin")]` reads back as `{name: "Authorize", arguments: "Roles = Admin"}`. `name` strips a trailing `Attribute` suffix. Absent when there are none. Suppressed when `source` is also requested |
| `baseType` | For a type only: `{symbolId, displayString}` for its direct base type — one hop, not the transitive chain (`get_type_hierarchy` owns that). Absent for anything else. Suppressed when `source` is also requested |
| `interfaces` | For a type only: `[{symbolId, displayString}]` for its direct interfaces (not `AllInterfaces`). Absent for anything else. Suppressed when `source` is also requested |
| `usings` | This symbol's file-level `using` directives (compilation-unit plus any enclosing classic namespace block), in source order. `null` if there are none. **Not** suppressed by `source` — a symbol's own declaration span never includes the file's usings, so it's genuinely new information either way |

`modifiers` is not in this table — it isn't an `include` component at all. It's computed on every call,
like the skeleton, not opt-in (see below).

The skeleton is `kind`, `origin`, `containingType`, `declarationSites` — unconditional, every call gets
it. `displayString` and `modifiers` (the literal C# modifier phrase, e.g. `"public sealed"`, `"public
override"`) sit one tier below: also computed on every call, but suppressed to `null` when `source` is
also requested, since the declaration's own signature line already states both as text — **unless the
source was line-sliced** (`@`), which usually cuts that line out, so both come back. **There is no
`accessibility` field** — `modifiers` already carries it, so a second field saying the same thing would
just be duplication. `source` itself reads exactly as it does in the file, no header line prepended.

`origin` is `"source"` for anything this repo's own solution declares, or `"external"` for a BCL/NuGet
symbol resolved only because this repo's own code calls, constructs, implements, or extends it —
`search_index(origin: "external")` is how such a symbol gets found in the first place. An external
symbol's `declarationSites` is always `[]`.

Examples:

- `include: "all"` minus the body — spell out the rest instead of subtracting:
  `include: "xmlDoc,mechanicalFacts,referenceCounts,recentLog"`. Use when you want facts and
  history but are not going to edit the symbol.
- `include: "members"` — a type's API surface with no bodies and none of the standard extras.
- `include: "xmlDoc"` — the leanest non-default fetch: just the skeleton plus the doc breakdown,
  no `referenceCounts` latency cost (it waits on the semantic model) and no history lookup.
- `include: "attributes"` — check `[Authorize]`/`[AllowAnonymous]`/`[Obsolete]` presence on a
  member without a `source` fetch; the review agent's `[security]` and `[docs]` aspects use this.
- `include: "source:code@120-160"` — one region of a long member. See below for when that pays.

An unrequested component is absent from the JSON entirely, not null, so it costs nothing. A
misspelled name is an `invalid_component` error rather than being silently dropped, and a malformed
`source:` entry gets an error about the *source grammar* rather than a list of bare component names —
`"source:code@70-81-lineNumbers"` is told that the `-modifiers` come **before** the `@` line selection,
which runs to the end of the entry.

The response's own `components` field reports what was **served**, and is emitted only when that differs
from what was asked for. Under `include: "all"` on a method, `members`/`baseType`/`interfaces` return
nothing — correctly, a method has none — and the field names the five that did come back rather than
advertising eleven. When every requested component was served it is absent, since it would only restate
the argument.

### When to slice `source` with `@`, and when not to

An `@` selection is worth it when you already know **where** in a long declaration you need to look and
the rest is dead weight — a stack frame or diagnostic naming a line, a span you noted from an earlier
fetch, or a second pass into a member you have already read once. Two hundred lines fetched to read
thirty is the case it exists for.

It is **not** worth it as a default. Below roughly 40–60 lines a whole declaration costs less than the
slice plus the follow-up you will issue when the slice turns out to be the wrong region — you pay two
round trips for one answer. Fetch the member whole the first time; slice on the way back in.

Two things to hold when you do slice:

- **`contentVersion` still covers the whole symbol.** It is fingerprinted over the entire
  declaration, not the lines you received, so holding it never means you have seen all of it. `sourceLines`
  (`"92-93/90-94"`) is the field that says what you actually got — read it rather than assuming the
  response is the member.
- **A miss is silent by design.** Ranges outside the declaration return `"source":[]` with
  `"sourceLines":"none/90-94"` instead of an error, because the response still carries the skeleton and
  the real span. If you get `none`, re-read the denominator and ask again — don't conclude the symbol has
  no body.

### Reading without line numbers

`-lineNumbers` drops the per-line `NN:` gutter, which costs ~3 tokens on every line. **How much that
saves is specimen-dependent, and the range is wide**: measured over this repo's own `src/` it is ~18%
of a `source` payload, but measured over deeply-nested lines in another repo (a lambda inside a method,
20–28 leading spaces) it was 4.9%. The gutter is a fixed cost per line, so the saving is large on short,
shallow lines and small on long ones — expect a real but modest win, not a fifth of the payload. In
exchange the component changes shape: instead of `[{line, text}]` it returns `[{lines, text}]`,
one entry per **contiguous run**, rendering under `toon` as an `@start-end` header above bare code:

```
source:
  @6-13
  public static class SourceQueryFixture
  {
      [Obsolete]
      public static int WithOwnLineAttribute() => 1;
```

The run grouping is not decoration. `-modifier` exclusions and `@` selections both drop lines, so bare
text with no headers would read as contiguous code across a gap that is really there — with
`"source:full-comments-lineNumbers"` on a type whose line 8 is a standalone comment, you get two entries
(`"5-7"` and `"9-13"`), not one block silently missing a line. Fragmentation is usually low: across this
repo's members, a `source:code` fetch averages 1.01 runs and `source:code-comments` 1.19, so the header
is normally paid once.

**This is for reading, not for editing.** The gutter is what makes a line directly usable as a
`validate_patch` `startLine`/`endLine`; without it a line has to be counted forward from its run's
header, and a miscount produces a patch that compiles against the wrong span rather than an error.
Fetch with the numbers left on whenever the next step is an edit — the saving is worth it for a read
pass over unfamiliar code, not for the fetch you are about to patch from.

Composes with everything else: `"source:code-comments-lineNumbers@46-120"` is legal, and `sourceLines`
still reports the `kept/whole` span for the `@` part.

### When to reach for `bodyOutline`

Use it as a **map before a fetch**, not a replacement for one: a long, unfamiliar member where you don't
yet know which region to slice with `source@`. A `switch` with a dozen cases, or a member `search_index`'s
`endLine - line` flags as large, is the shape it pays off on — the rows tell you which case/branch to
slice next instead of guessing a line range or reading the whole thing.

It is **not** worth it below roughly 40 lines (doc-comment-inclusive, the same bound `declarationSites`
reports) — `bodyOutlineNote` says so explicitly rather than silently degrading to something else, but the
rows still come back so the response is never a dead end. And it is not a substitute for reading the
region once you've located it: `text` is a truncated label for navigation, not a summary you can reason
from in place of the actual condition/expression.

### Location is always there

Every `get_symbol` response carries `declarationSites` — `file`, `startLine`, `endLine` —
regardless of `include`. It is part of the unconditional skeleton (`kind`, `origin`, `containingType`,
`declarationSites` — plus `displayString`/`modifiers`, computed the same way but suppressed when
an unsliced `source` is also requested), and the spans are computed live rather than read from a cache, so they are
correct even for a symbol split across partial-class files.

That means **"where does this live?" never costs a second call or an extra component**, and those
spans are exactly what a `validate_patch` edit takes. Do not reach for `include: "all"` just to
find a line number — the default `standard` call already carries `declarationSites`.

**A narrowed response returns a narrowed version token**, covering only the layers it served —
so a token from a `standard` fetch and one from `include: "all"` are not directly comparable if
you're diffing them yourself later.

That narrowing has a consequence on the write path: the `standard` token carries `decl` (+`refs`) and no
`body`, and `validate_patch` rejects a **body-changing** patch built on a token that never held the body
layer (`error: "unleased_body"`) rather than skipping the staleness check for the text it would
overwrite. Fetch with `include: "all"` — or any include containing `source`, `bodyOutline` or
`mechanicalFacts` — when the edit rewrites a body, which is also when you wanted the text anyway.

### Several symbols in one call

`symbols` fetches a list instead of `symbol` fetching one — same `include` applied to every entry:

```
get_symbol(symbols: ["Sample.Lib.Widget", "Sample.Lib.IWidget"])
```

The response's `results` is an array with one entry per requested symbol. A successful entry has
`symbolId, contentVersion, limitedBy, content`; a symbol that did not resolve has `error` instead
(`symbol_not_found`, `ambiguous_symbol`) with no `symbolId`/`contentVersion`/`content` — one failed
lookup does not fail the batch, and the two shapes are told apart by which keys are present.

Whatever **every** entry repeats verbatim is lifted into one `shared` block beside `results`:
`components` is identical by construction (one `include` covers the whole batch), and symbols from one
type repeat `origin` and `containingType` too. It appears only when that rendering is genuinely
smaller, so a batch of unrelated symbols still comes back as plain per-entry results. Read a field from
`shared` when an entry does not carry it; nothing else about an entry differs from what a single-symbol
call would have returned. The batch's win is round trips — on tokens it is roughly a wash.

Every entry carries full content — there is no lease mechanism to interact with here or anywhere
else in `get_symbol` (see "Version tokens" below).

## Reference

Replaces: `Read` on a `.cs` file. Read gives you one fragment of a symbol split across partial-class
files with no signal the rest exists, and costs the whole file's tokens for the part you wanted.

| Arg | Meaning |
|---|---|
| `symbol` / `symbols` | Fully-qualified name, unique suffix, `Name(ParamType)` to pick an overload, or a `sym_…` id from any earlier response. Exactly one of the two. `symbols` batches several under one `include` — but an `@` line selection (below) is rejected there with `lines_with_batch`, since one span of file lines cannot apply to several symbols. |
| `include` | Omitted/`"standard"` (default: `xmlDoc, referenceCounts, recentLog`) \| `"all"` (every component) \| a comma list that REPLACES the default, e.g. `"source,members"`, `"source:code,members"`, or `"source:code@46-76"`. |

Component names are exactly the response fields they control:

| Component | Returns |
|---|---|
| `source` | Full declaration source as `[{line, text}]`, one entry per physical line, `line` an absolute 1-based file line — not one `\n`/`\"`-escaped string. Each `line` is directly usable as a `validate_patch` `startLine`/`endLine` without a second lookup, even after modifiers below drop some lines: surviving entries keep their true file line number, never renumbered to close the gap. Under the default `toon` format this renders as a raw, fully unescaped `line: text` block instead of that structured array — see the worked example below; `format:"json"`/`"compact"` keep the structured array. Takes an optional `:full`\|`:code` mode suffix on the component name itself — `"source"`/`"source:full"` (the default) include the declaration's leading `///` doc comment; `"source:code"` is the same span minus that comment (attributes and the body are unchanged), for a caller that only needs enough to modify the code and already has `xmlDoc` or doesn't need it. Either mode additionally accepts subtractive `-modifier` suffixes concatenated onto it, e.g. `"source:full-remarks-attributes"` or `"source:code-comments"`: `full`-only doc-tag modifiers (`summary`, `remarks`, `returns`, `value`, `inheritdoc`, `params`, `typeParams`, `exceptions` — matching `xmlDoc`'s own field names) drop that specific tag from an otherwise-full comment; `attributes`/`comments` (valid under either mode) drop C# attributes / `//` comments; `lineNumbers` (also either mode) drops the per-line number gutter, changing the component's shape to `[{lines, text}]` — see "Reading without line numbers" below. There is no additive `+tag` — a query only ever subtracts from its mode's own default (everything, for `full`; no doc tags but attributes/comments still on, for `code`), so a doc-tag modifier under `code` is rejected as redundant rather than silently accepted. Every subtraction is **whole-line only**: an attribute or comment sharing a line with real code (`[Fact] public void Foo()`, or a trailing `// why`) is left untouched rather than partially rewriting that line. An unrecognized suffix (`"source:bogus"`, `"source:code-remarks"`, `"source@nope"`) is an `invalid_component` error, same as a misspelled component name. Finally, either mode accepts an **`@` line selector** returning only part of the declaration — see the row below. |
| `source@lines` | Not a separate component: `@` plus line ranges appended to `source` (after any mode/modifiers), narrowing the returned lines to those ranges — for reading one region of a long member instead of all of it. `"source@46-76"`, `"source:code@46-76;79-83"`, `"source:code-comments@60-"` (line 60 to the declaration's last line), `"source@-50"` (its first line through 50), `"source@52"` (one line). Ranges are **absolute file line numbers** — the same ones `declarationSites` and each rendered line's own `NN:` gutter report — so a span read off any earlier response is directly reusable; separate several with `;`, **not** `,`, which already separates `include`'s component names. Selection is a pure filter that never renumbers a line, so it commutes with the `-modifier` subtractions above: a line those dropped stays dropped even when a range names it. A range running past the declaration clamps to it; one entirely outside it returns no lines rather than erroring. Whenever `@` is used the response adds **`sourceLines`**, a `"kept/whole"` span (`"46-76/38-96"`, or `"none/38-96"` when the ranges missed the declaration entirely — which also states the span that would have worked). Read it: `contentVersion` is still fingerprinted over the **whole** symbol, so it should not be mistaken for confirmation that the whole symbol was seen. Not valid alongside `symbols` (batch) — see the `symbol`/`symbols` row. |
| `xmlDoc` | `{summary, returns, remarks, value, inheritdoc, params, typeParams, exceptions}`, each XML-stripped to plain text; a field is absent when that tag isn't in the doc comment. `params`/`typeParams` are `[{name, text}]` from `<param>`/`<typeparam>`; `exceptions` is `[{type, text}]` from `<exception>`; `inheritdoc` is `true` when `<inheritdoc/>` is present. `xmlDoc` itself is absent only when none of these tags are present at all |
| `mechanicalFacts` | Server-computed structural facts as opaque JSON; `null` if the body changed since computed. Members carrying nothing (an empty `throws`/`awaits`/`writes`/`locks`/`implementsMembers`, a null `overrides`) are omitted rather than emitted empty — absence means "none" |
| `bodyOutline` | Control-flow landmarks inside a method-like body as `[{text, startLine, endLine, depth}]` in `format:"json"`/`"compact"` — under the default `toon` format, `depth` is dropped and nesting instead reads from two-space-per-level indentation on a raw `text,startLine,endLine` block (see below), the same raw-block treatment `source` gets and for the same reason. Purely syntactic (no semantic model, so it costs the same tier as `source`, not `mechanicalFacts`' semantic-model tier) — for navigating a long body without reading it. Covers `switch`/`case`, `if`, `foreach`/`for`/`while`/`do`, `catch`, `using`, `lock`; a bare `try`/`else`/`finally` carries no name or condition of its own and is omitted — its span is inferable from the parent row. `text` is a short label (e.g. `"switch(node)"`, `"if (name.Length > 3)"`), truncated to a 28-character budget with a trailing `..`, not a semantic summary. `depth` (or indentation, under `toon`) counts nesting among *other landmark rows only*, not raw syntax depth — a landmark buried inside several plain blocks isn't deeper than one actually nested inside another landmark. Absent (with an explanatory **`bodyOutlineNote`**, e.g. `"bodyOutline is not applicable to a Type symbol - only a method has an executable body to outline"`) for anything without an executable body of its own (a type, a field, an auto-property) — rather than both fields silently disappearing. `bodyOutlineNote` also appears — without suppressing the rows — when the declaration (doc-comment-inclusive, matching `declarationSites`) is under 40 lines — advisory only ("`source:code` is likely cheaper than this outline") |
| `referenceCounts` | `{callers, tests}` for a member (never for a type), plus `{implementations, overrides}` — but each of those two only where the symbol's kind makes a non-zero answer possible: `implementations` for an interface, an unsealed class or an interface member; `overrides` for a virtual/abstract member. An enum, a static class or a plain method omits them rather than reporting a structural `0`, and the whole component is absent when nothing is left to report |
| `recentLog` | Recent dev-log entries touching this symbol, each flagged `current:true/false` against the live body |
| `members` | For a type only: `[{symbolId, displayString, kind, contentVersion}]` per member |
| `attributes` | This symbol's own (non-inherited) C# attributes as `[{name, arguments}]`; `name` strips a trailing `Attribute` suffix (e.g. `[Obsolete]` → `"Obsolete"`); `arguments` is a compact rendering of constructor/named arguments, truncated rather than reproduced in full for a long string. Absent when the symbol has no attributes. Suppressed (absent) when `source` is also requested |
| `baseType` | For a type only: `{symbolId, displayString}` for its direct base type (not `object` filtered out, not the transitive chain — `get_type_hierarchy` owns that). Absent for anything else, including when a type has no explicit base. Suppressed when `source` is also requested |
| `interfaces` | For a type only: `[{symbolId, displayString}]` for its direct interfaces (not `AllInterfaces`). Absent for anything else. Suppressed when `source` is also requested |
| `usings` | This symbol's file-level `using` directives (the compilation unit's own, plus any declared inside an enclosing classic block-scoped namespace), in source order. `null` if there are none. **Not** suppressed by `source` — a symbol's own declaration span never includes the file's `using` directives, so this stays genuinely new information even alongside `source` |

`modifiers` is not an `include` component at all — it is computed on every call, like the skeleton (see
below), not opt-in.

The skeleton — `kind`, `origin`, `containingType`, `declarationSites` (`file`, `startLine`, `endLine`) —
is unconditional: every call gets it regardless of `include`, and those line spans are exactly what a
`validate_patch` edit takes. `displayString` and `modifiers` sit one tier below the skeleton: computed on
every call the same way, but suppressed to `null` when `source` is also requested, since a declaration's
own signature line already states both as text — asking for `source` alongside `xmlDoc`/`attributes`/
`baseType`/`interfaces`/`displayString`/`modifiers` is not a bigger fetch, it is the same fetch minus the
duplication. **A line-sliced `source` (`@`, above) is the exception**: a slice usually cuts the signature
line out, so `displayString`/`modifiers` are restored rather than leaving a fragment that never says what
member it belongs to. They stay suppressed for an unsliced `source`, and the slice-only `sourceLines`
field appears alongside them. **There is no `accessibility` field** — `modifiers`' literal keyword phrase already carries
it (`"public sealed"` states both), so a second field saying the same thing would be pure duplication.
When the symbol has a leading `///` XML doc comment, `startLine` (and `source`/`source:full`) begin at the
comment, not at the attribute/signature line after it — so an edit built from `declarationSites` can
rewrite the doc comment along with the declaration. `source:code` begins at the attribute/signature line
instead, skipping the comment. Either mode reads exactly as the file does — no `"// in <ContainingType>"`
header line prepended.

`origin` is `"source"` for anything this repo's own solution declares, or `"external"` for a BCL/NuGet
symbol resolved only because this repo's own code calls, constructs, implements, or extends it (see
`search_index`'s `origin` argument below for how such a symbol gets discovered in the first place). An
external symbol's `declarationSites` is always `[]` and `source`/`xmlDoc` are effectively unavailable —
there is no file in this repo to point at.

Real `include: "source"` call and response (shown as `format:"json"`, where `source` is `[{line, text}]`
structured data) — each line is its own entry, `line` an absolute file line, rather than one string
carrying literal `\n`/`\"` escapes:

```
get_symbol(symbol: "SymbolResolver.NameWithoutParameters", include: "source")
```

```json
{"symbolId":"sym_3ea06d...","contentVersion":"decl:2b96b2c51e23|body:76ef6255ae6b",
 "content":{"kind":"Method","origin":"source",
   "containingType":{"symbolId":"sym_914205...","displayString":"SymbolResolver"},
   "declarationSites":[{"file":"src/DotnetToolkit.McpServer/Workspace/SymbolResolver.cs",
                         "startLine":86,"endLine":94}],
   "source":[
     {"line":86,"text":"/// <summary>"},
     {"line":87,"text":"    /// The name with any parameter list dropped entirely — the form the syntax index keys declarations"},
     {"line":88,"text":"    /// by, so a stored name can be matched against it."},
     {"line":89,"text":"    /// </summary>"},
     {"line":90,"text":"    public static string NameWithoutParameters(string fqName)"},
     {"line":91,"text":"    {"},
     {"line":92,"text":"        var paren = fqName.IndexOf('(');"},
     {"line":93,"text":"        return paren < 0 ? fqName : fqName[..paren];"},
     {"line":94,"text":"    }"}]}}
```

Each entry's `line` is directly usable as a `validate_patch` `startLine`/`endLine` — no separate
`get_symbol` round trip to learn where inside a large declaration a particular statement sits.
`get_references`' `includeBodies:true` content carries the identical `[{line, text}]` shape.

The same call with `include: "source:code"` instead drops the doc comment (lines 86–89 above) and starts
at the signature:

```json
"source":[
  {"line":90,"text":"    public static string NameWithoutParameters(string fqName)"},
  {"line":91,"text":"    {"},
  {"line":92,"text":"        var paren = fqName.IndexOf('(');"},
  {"line":93,"text":"        return paren < 0 ? fqName : fqName[..paren];"},
  {"line":94,"text":"    }"}]
```

`source:code` also strips a *nested* doc comment when the fetched symbol is a type — every member's own
`///` block, not just the type's. And either mode's `-modifier` suffixes subtract further: fetching a
whole type with `include: "source:full-remarks"` keeps every member's `<summary>`/`<returns>` but drops
just the `<remarks>` tag's lines, wherever they occur — `line` values still jump straight to their real
file position (e.g. `51 → 55`), never renumbered to hide the gap.

Appending `@` narrows the same call to part of the declaration. Against the identical symbol, asking for
only the two body statements — note `displayString`/`modifiers` coming back, since the signature line is
no longer in the result, and `sourceLines` stating the kept span against the whole one:

```
get_symbol(symbol: "SymbolResolver.NameWithoutParameters", include: "source:code@92-93")
```

```json
{"symbolId":"sym_3ea06da32ed71107","contentVersion":"decl:2b96b2c51e23|body:76ef6255ae6b",
 "components":["source"],
 "content":{"kind":"Method","displayString":"string SymbolResolver.NameWithoutParameters(string fqName)",
   "origin":"source",
   "containingType":{"symbolId":"sym_914205117b4bda00","displayString":"SymbolResolver"},
   "declarationSites":[{"file":"src/DotnetToolkit.McpServer/Workspace/SymbolResolver.cs",
                         "startLine":86,"endLine":94}],
   "source":[
     {"line":92,"text":"        var paren = fqName.IndexOf('(');"},
     {"line":93,"text":"        return paren < 0 ? fqName : fqName[..paren];"}],
   "sourceLines":"92-93/90-94","modifiers":"public static"}}
```

`sourceLines`' denominator is the span of the *mode's* own text, not `declarationSites` — here
`source:code` starts at line 90, so the whole is `90-94` while `declarationSites` still reports `86-94`
including the doc comment. A range that misses entirely returns `"source":[]` with
`"sourceLines":"none/90-94"`, which is why an out-of-range selection is not an error: the response
already says both that nothing was kept and what would have worked.

`include: "bodyOutline"` against `MechanicalFactsExtractor.Extract` — a real 60-line method with a
`foreach` over a `switch`, five `case` labels each guarding an `if`, and a second `foreach`/`if` pair.
`depth` counts nesting among landmark rows only (the outer `foreach` is `0`, the `switch` inside it is
`1`, each `case` is `2`, the `if` inside each `case` is `3`), and a `case` label's text is its pattern's
type name with the redundant `Syntax` suffix stripped:

```
get_symbol(symbol: "MechanicalFactsExtractor.Extract", include: "bodyOutline")
```

```json
{"content":{"kind":"Method","bodyOutline":[
  {"text":"foreach(node)","startLine":46,"endLine":76,"depth":0},
  {"text":"switch(node)","startLine":48,"endLine":75,"depth":1},
  {"text":"case ThrowStatementSyntax { Expre..","startLine":50,"endLine":53,"depth":2},
  {"text":"if (model.GetTypeInfo(thrown).Ty..)","startLine":51,"endLine":52,"depth":3},
  {"text":"case ThrowExpression","startLine":55,"endLine":58,"depth":2},
  {"text":"if (model.GetTypeInfo(throwExpr...)","startLine":56,"endLine":57,"depth":3},
  {"text":"case AwaitExpression","startLine":60,"endLine":63,"depth":2},
  {"text":"if (model.GetSymbolInfo(awaited...)","startLine":61,"endLine":62,"depth":3},
  {"text":"case LockStatement","startLine":65,"endLine":68,"depth":2},
  {"text":"if (model.GetSymbolInfo(locked.E..)","startLine":66,"endLine":67,"depth":3},
  {"text":"case AssignmentExpression","startLine":70,"endLine":74,"depth":2},
  {"text":"if (StateMemberOf(assignment.Lef..)","startLine":72,"endLine":73,"depth":3},
  {"text":"foreach(identifier)","startLine":79,"endLine":83,"depth":0},
  {"text":"if (StateMemberOf(identifier, mo..)","startLine":81,"endLine":82,"depth":1}]}}
```

(The first `case` row is itself truncated to 28 characters — the label reads
`ThrowStatementSyntax { Expression: { } thrown }` in full — which is why the `StripSyntaxSuffix` heuristic
only shows through on the shorter labels below it.)

A declaration under 40 lines (doc-comment-inclusive, here `BodyOutlineExtractor.Extract` itself) still
returns its rows plus the advisory `bodyOutlineNote`, never an error or a substituted component:

```
get_symbol(symbol: "BodyOutlineExtractor.Extract", include: "bodyOutline")
```

```json
{"content":{"kind":"Method","bodyOutline":[
  {"text":"foreach(node)","startLine":30,"endLine":34,"depth":0},
  {"text":"if (LandmarkText(node) is { } te..)","startLine":32,"endLine":33,"depth":1},
  {"text":"foreach(var (node, text))","startLine":41,"endLine":46,"depth":0}],
  "bodyOutlineNote":"declaration is 23 lines (<40) - source:code is likely cheaper than this outline"}}
```

**Under the default `toon` format, `source`/`content` render differently.** TOON's normal tabular
array-of-objects encoding would quote/escape every line containing a comma or a literal `"` — which is
nearly every real C# line — turning a method into a wall of `\"` noise. Instead, `Formats.Render`
structurally detects this one shape and splices in a raw, completely unescaped `line: text` block, at
the cost of that one field no longer being strict parseable data (only `source`/`content` — everything
else in the response stays normal TOON). A real capture, a line containing its own embedded `"`
included, unescaped exactly as the file reads:

```
source:
  50: private static TypeEntry BuildType(BaseTypeDeclarationSyntax type, string containerFq, string ns)
  51:     {
  52:         var name = type.Identifier.Text + (type is TypeDeclarationSyntax { TypeParameterList: { } tp } ? tp.ToString() : "");
  53:         var fq = Combine(containerFq, name);
  54:         var kind = type switch
  55:         {
  56:             InterfaceDeclarationSyntax => "I",
  ...
```

`format:"json"`/`"compact"` always keep the structured `[{line, text}]` array shown above — a caller
that needs `source` to stay strictly machine-parseable should use one of those, not the TOON default.

**`bodyOutline` gets the identical raw-block treatment**, for the identical reason: nesting is
information a flat, quoted array can only carry as a redundant `depth` number, when TOON already has a
convention for showing nesting — indentation. Under `toon`, each row renders as `text,startLine,endLine`
indented two spaces per depth level, `depth` itself dropped rather than repeated as text next to the
indentation that already says it. The `MechanicalFactsExtractor.Extract` call above renders as:

```
bodyOutline:
  foreach(node),46,76
    switch(node),48,75
      case ThrowStatementSyntax { Expre..,50,53
        if (model.GetTypeInfo(thrown).Ty..),51,52
      case ThrowExpression,55,58
        if (model.GetTypeInfo(throwExpr...,56,57
      case AwaitExpression,60,63
        if (model.GetSymbolInfo(awaited...,61,62
      case LockStatement,65,68
        if (model.GetSymbolInfo(locked.E..,66,67
      case AssignmentExpression,70,74
        if (StateMemberOf(assignment.Lef..,72,73
  foreach(identifier),79,83
    if (StateMemberOf(identifier, mo..,81,82
```

`format:"json"`/`"compact"` keep the flat `[{text, startLine, endLine, depth}]` shape shown in the JSON
examples above — plain JSON has no indentation convention of its own to lean on instead, so `depth`
stays explicit there.

Default call, real response:

```
get_symbol(symbol: "FeatureLogStore.Append")
```

```json
{"symbolId":"sym_c25d7c88b0e916b0","contentVersion":"decl:ddca3badaba1|refs:532f4bebd9ac",
 "content":{"kind":"Method","displayString":"string FeatureLogStore.Append(LogEntry entry)",
   "origin":"source","modifiers":"public",
   "containingType":{"symbolId":"sym_fc346a8c5efa6a88","displayString":"FeatureLogStore"},
   "declarationSites":[{"file":"src/DotnetToolkit.McpServer/Store/FeatureLogStore.cs",
                         "startLine":27,"endLine":78}],
   "xmlDoc":"Appends one log record and its per-symbol rows in a single transaction. Returns the log id.",
   "referenceCounts":{"callers":2,"implementations":0,"overrides":0,"tests":0}}}
```

`recentLog` is absent here because it had nothing to report — absence means "nothing", not "not
computed" (that distinction is `limitedBy`'s job).

Several symbols in one call — `symbols` instead of `symbol`:

```
get_symbol(symbols: ["Sample.Lib.Widget", "Sample.Lib.IWidget"], include: "members")
```

`results` becomes an array with one entry per requested symbol, with anything every entry repeats
verbatim (`components` always, plus `origin`/`containingType` for symbols from one type) lifted into a
`shared` block beside it whenever that renders smaller. A symbol that did not resolve has `error` set
(`symbol_not_found`, `ambiguous_symbol`) instead of `symbolId`/`contentVersion`/`content` — one bad
lookup does not fail the batch, and the two shapes are told apart by which keys are present.
Every entry carries full content — there is no lease mechanism in `get_symbol` to interact with.

`ambiguous_symbol` takes the same hoist: the prefix all its candidates share (namespace, and usually the
containing type — by construction, since that shared prefix is what made the name ambiguous) is emitted
once as `sharedPrefix`, and each candidate's `displayString` carries only the part that differs.
Concatenate the two for a name you can pass back, or pass the candidate's `symbolId` instead:

```json
{"error":"ambiguous_symbol","sharedPrefix":"Sample.Lib.Overloads.",
 "candidates":[{"symbolId":"sym_1a2b...","displayString":"Pick(int)"},
               {"symbolId":"sym_3c4d...","displayString":"Pick(string)"}],
 "totalCandidates":2}
```

`candidates` is capped at ten. `totalCandidates` always states how many matched, and `truncated: true`
appears when the cap bit — with a `message` naming the ways to narrow. **A name like `Run` in a test
tree can match fifty members, so an absent candidate is not an absent symbol**: qualify the name with
its containing type or namespace, or append a parameter list, rather than concluding it does not exist.
`rename_symbol` returns the identical payload from the same renderer.

## Next steps

The response's `referenceCounts` gates what is worth fetching next (see "Gate expansion on referenceCounts" in the `dotnet-code-query` skill). From here:

- **Expand to call sites** → `get_references` — `get_references.md`
- **Open-ended caller tree** → `get_call_hierarchy` — `get_call_hierarchy.md`
- **Only need part of a long member** → re-call with `include: "source:code@120-160"` (above)
- **Editing it** → keep `contentVersion` and `declarationSites`, then `validate_patch` — `validate_patch.md`
