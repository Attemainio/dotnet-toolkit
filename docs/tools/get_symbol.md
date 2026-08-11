# `get_symbol` — a symbol's shape, docs, location, source

Replaces `Read` on a `.cs` file — returns the whole symbol even when split across partial-class
files (`Read` gives one fragment with no signal the rest exists), for a fraction of the tokens.

## The `include` selector

Three forms:

1. **Omitted, or `"standard"`** (default) — `xmlDoc`, `referenceCounts`, `recentLog`. Start here.
2. **`"all"`** — every component below. Reach for this only when about to edit the symbol, or
   genuinely want everything about it at once. **In practice this is `source` plus a `refs` lease**:
   every other component (`members`, `attributes`, `baseType`, `interfaces`, `xmlDoc`, `bodyOutline`)
   is suppressed once `source` is present (each component's own row above states this), so `"all"`'s
   only real addition over `include: "source"` is `referenceCounts` and the `|refs:` layer `all`
   narrows `contentVersion` to. Measured: 613 tokens vs. 603 for `source` alone on the same symbol.
3. **An explicit comma list**, e.g. `"source,members"` — **replaces** the default set rather than
   adding to it. Use whenever `standard`/`all` is close but not quite right.

An unrequested component is absent from the JSON entirely, not `null` — it costs nothing. A
misspelled name is an `invalid_component` error rather than being silently dropped.

### The unconditional skeleton

Every call, regardless of `include`, gets `kind`, `origin`, `containingType`, `declarationSites`
(`file`, `startLine`, `endLine` — exactly what a `validate_patch` edit takes, computed live so it's
correct even for a symbol split across partial-class files). `displayString`/`modifiers` sit one
tier below — also always computed, but suppressed to `null` when `source` is also requested, since
the declaration's own signature line already states both as text. **Exception: a line-sliced
`source` (`@`, below)** usually cuts the signature line out of the result, so both are restored
there. **There is no `accessibility` field** — `modifiers`' literal keyword phrase (`"public
sealed"`) already carries it.

That means **location never costs a second call** — the default `standard` fetch already carries
`declarationSites`; don't reach for `include: "all"` just to find a line number.

`origin` is `"source"` for anything this repo's own solution declares, or `"external"` for a
BCL/NuGet symbol resolved only because this repo's own code calls/constructs/implements/extends it
(`search_index(origin: "external")` is how such a symbol is found in the first place). An external
symbol's `declarationSites` is always `[]`.

`generated: true` marks a symbol whose every declaration is source-generator output — no span to
patch, the file is rewritten on every build. Roslyn still counts it as `"source"`, so
`declarationSites` being empty is this, not a lookup bug.

## Components

| Component | Returns |
|---|---|
| `source` | Full declaration rendered per its `SourceLineFormat` (see below): `[{line, text}]` (Exact, one entry per physical line) or `[{lines, text}]` (Compact, one entry per contiguous run) — `line`, or a run's start, is an absolute file number, directly usable as a `validate_patch` span. Under `toon` this renders as a raw, unescaped block (a quoted array-of-objects would turn every C# line into escape noise); `json`/`compact` keep the structured array. `"source"` = `"source:full"` (includes the leading `///` doc comment); `"source:code"` drops it — and, for a **type**, every member's own doc comment too — a **reading** mode. Either mode takes `-modifier` suffixes to subtract further: doc-tag modifiers (`summary`, `remarks`, `returns`, `value`, `inheritdoc`, `params`, `typeParams`, `exceptions`) only work under `full`; `attributes`/`comments`/`exact`/`compact` work under either (`lineNumbers` is a deprecated alias for `compact`). No `+tag` exists — a query only ever subtracts, and only ever removes a *whole* line, never an attribute/comment sharing a line with real code. |
| `sourceLineFormat` | `"exact"`/`"compact"` naming which format `Automatic` actually picked — only when `source` was requested with `Automatic` (the default). Absent when the caller forced `-exact`/`-compact` explicitly (it already knows what it asked for) or when `source` wasn't requested at all. **One exception: a multi-file partial reports `"compact"` even under a forced `-exact`**, because that request cannot be honoured (see below) — this field is how you find out it lost. Saves sniffing the `source` array's own shape (`line` vs `lines`) to find out. |
| `source@lines` | Not a separate component — `@` plus line ranges appended to `source` (after any modifiers): `"source@46-76"`, `"source:code@46-76;79-83"` (`;` separates ranges, **not** `,`, which already separates component names), `"source@-50"`, `"source@52"`. Absolute file line numbers, reusable as-is from any earlier response. Adds `sourceLines`: `"kept/whole"` (or `"none/whole"` on a miss — not an error; the response still states what would have worked). `contentVersion` still covers the **whole** symbol, so holding it is not confirmation the whole symbol was seen. A slice still leases the body layer — enough to patch from; a second edit into a member already read this way does not need `include: "all"` again. |
| `xmlDoc` | `{summary, returns, remarks, value, inheritdoc, params, typeParams, exceptions}`, XML-stripped to plain text, each absent when that tag isn't present. `params`/`typeParams` are `[{name, text}]`; `exceptions` is `[{type, text}]`; `inheritdoc` is `true` when `<inheritdoc/>` is present. Whole component absent only when none of these tags exist at all. Suppressed when `source` is also requested **and the lines actually returned carry the whole doc comment**. `source:code` exists precisely to strip the `///` block, so it never suppresses. An `@` line selection is judged on what it kept: a slice covering every line of the doc comment suppresses, one that cut any of it away serves `xmlDoc` — half a summary read as prose is not the summary. Suppressing on "is a slice" alone deleted the documentation from the response rather than deduplicating it. |
| `mechanicalFacts` | Server-computed structural facts as opaque JSON; `null` if the body changed since computed. Empty sub-fields (`throws`, `awaits`, `writes`, `locks`, `implementsMembers`, `overrides`) are omitted rather than emitted empty. |
| `bodyOutline` | Control-flow landmarks (`switch/case`, `if`, `for/foreach/while/do`, `catch`, `using`, `lock`) for mapping a long body before slicing it — purely syntactic, same cost tier as `source`, not `mechanicalFacts`' semantic tier. `text` truncated to 28 chars. A bare `try`/`else`/`finally` has no name of its own and is omitted; infer its span from the parent row. Absent (with `bodyOutlineNote`) for anything without an executable body (a type, a field, an auto-property). `bodyOutlineNote` also appears — rows still returned — when the outline is unlikely to earn its cost: under ~40 lines, or past that but averaging worse than one landmark per 25 lines (a mostly linear body). Suppressed when `source` is also requested — both exist as a structural stand-in for the body, so once `source` is present they'd only restate what it already shows. |
| `referenceCounts` | `{callers, tests}` for a member (never a type), plus `{implementations, overrides}` only where the kind makes a non-zero answer possible (interface/unsealed class/interface member; virtual/abstract member). An enum, static class or plain method omits them rather than reporting a structural `0`; the whole component is absent when nothing is left to report. |
| `recentLog` | Recent dev-log entries touching this symbol, each flagged `current: true/false` against the live body. |
| `members` | Type only: `[{symbolId, displayString, kind, line, shape, contentVersion}]` per member — **private included**, since `M` in `search_index`'s `shape` counts them too — plus `file` when a member is declared in a different file (a partial). `line`/`shape` match `search_index`'s own conventions. `contentVersion` is present only under `include: "all"` — narrowed to `decl`, enough to lease a signature/doc edit, not a body edit (`unleased_body` if attempted) — and absent on a plain `members` fetch, since a read-only pass never uses it and an about-to-edit pass re-fetches the member itself for its body version anyway. Counted per **declaration**: each part of a partial type carries its own share while this listing merges every part; a nested type is counted by `N` yet still listed here. Suppressed when `source` is also requested — the member list is a stand-in for reading the body, and `source` already shows it. |
| `attributes` | This symbol's own (non-inherited) attributes as `[{name, arguments}]` — `name` strips a trailing `Attribute` suffix. A compiler-supplied argument (`[CallerMemberName]` etc.) is dropped, so e.g. xUnit's bare `[Fact]` reports no arguments. Absent when none. Suppressed when `source` is also requested. |
| `baseType` | Type only: `{symbolId, displayString}` for the direct base type only (not the transitive chain — that's `get_type_hierarchy`). Suppressed when `source` is also requested. |
| `interfaces` | Type only: `[{symbolId, displayString}]` for direct interfaces (not `AllInterfaces`). Suppressed when `source` is also requested. |
| `usings` | File-level `using` directives, source order. **Not** suppressed by `source` — a symbol's declaration span never includes the file's usings, so it stays genuinely new information either way. |

## Reading vs. editing — the invariant that bites

A `-modifier` (or `source:code` on a type) removes a line from the **response**, never the file —
and `validate_patch` replaces `startLine`–`endLine` **verbatim**. A span built from a stripped
fetch silently deletes every line the modifier hid inside it:

```
get_symbol(include: "source:code-comments@40-80")   →  you see 40, 41, 44, 45, …
validate_patch(startLine: 40, endLine: 80, …)       →  42-43 were `// comments`. They are now gone.
```

Unsafe to anchor an edit on: `-comments`, `-attributes`, `source:code` **on a type**. Safe by
construction: a leading `///` doc comment dropped by `source:code` is always at the *start*, so a
first-to-last-line span never covered it anyway — which is also why `declarationSites` can be
wider than what `source:code` returned.

**Rule: strip on the way in, fetch whole on the way out.** Read with `source:code` or
`source:code-comments`; patch from `source`/`source:full` with the gutter left on.

`source`'s line format is **`Automatic` by default**: the server renders both the numbered gutter
(`[{line, text}]`) and the `@start-end` span form (`[{lines, text}]`) and keeps whichever is
literally fewer characters. The gutter costs a few characters *per line*; a span header costs a few
characters *per contiguous run* — so for an unmodified declaration of any real size, Compact wins
almost every time.

Under `toon`, the gutter renders as **`120│ text`, with the numbers right-aligned to the widest one
in the block**:

```
  98│     public int Bar()
  99│     {
 100│         return 1;
```

The code therefore starts at the same column on every row, so the boundary between the number and
the source it labels is never something to re-find per line — which is where a line number gets read
as part of the code. The padding is real and is charged to `Automatic`'s comparison, so a block
spanning a digit boundary is measured at what it actually costs rather than at its narrowest row.
`json`/`compact` keep the structured `[{line, text}]` array and have no gutter to align.

`-exact` forces the numbered gutter even on the rare declaration where the spans would be shorter;
`-compact` forces the spans even when the gutter would be shorter (`-lineNumbers` is a deprecated
alias for `-compact`). The two force-modifiers contradict each other — `-exact-compact` together is
an `invalid_component`.

### A partial declared across several files

`source` returns **every part**, in the same order `declarationSites` lists them — that is what the
promise of "the whole symbol, where `Read` gives one fragment" means for a partial. Through contract
3.62 it returned only the first part while `declarationSites` named them all, so the response
contradicted itself; if you are reading an older transcript, that is what you are seeing.

Each run is then prefixed with its file, because a bare line number stops identifying a place once
every part has its own line 100:

```
@src/Store/SymbolStore.cs:5-698
…
@src/Store/SymbolStore.Update.cs:5-313
…
```

Two consequences worth knowing:

- **The span form is imposed**, even against `-exact`. A per-line gutter carries a number and nothing
  else, so it has nowhere to put the file, and an `-exact` rendering here would interleave two files'
  line numbers with no way to tell them apart. `sourceLineFormat` reports `"compact"` so the override
  is visible rather than silent.
- **`sourceLines` is omitted** on a sliced fetch of a multi-file partial. Its `kept/whole` denominator
  is one file's span and there is no single one; `declarationSites` already carries every part's, and
  each run's `@file:start-end` header says which file its lines came from.

An `@` selection still applies by absolute line number across every part, so a range matching lines in
two files returns both — labelled, not merged. That is usually not what you wanted: fetch the member
you actually care about instead, which is a single-file declaration and slices normally.

Force `-exact` for anything you are about to build a `validate_patch` span from. Compact's line
number is the *start* of each run, not one per line — a line further into a run has to be counted
forward from that header, and a miscount produces a patch against the wrong span, not an error.
`Automatic` may silently return either shape, so don't rely on it for a fetch you intend to edit
from — check `sourceLineFormat` (or force `-exact`) instead of inferring the shape yourself.

## What `contentVersion` a patch needs

The `standard` token carries `decl` (+`refs`), no `body` layer. `validate_patch` rejects a
**body-changing** edit built on it (`error: "unleased_body"`) rather than skipping the staleness
check for text it would overwrite. Fetch with `include: "all"` — or anything containing `source`,
`bodyOutline`, or `mechanicalFacts` — whenever the edit rewrites a body, which is also when you
wanted the text anyway. A response is narrowed to exactly the layers it served, so a `standard`
token and an `"all"` token aren't directly comparable if diffing them yourself.

## Don't refetch what you already hold

If you fetched this exact symbol earlier in the same conversation and haven't edited it since, reuse
the `contentVersion`/`declarationSites` you already have rather than calling `get_symbol` again to get
back numbers you're already holding — a plain re-fetch for unchanged content is pure waste. This is
easy to miss right after an edit: an applied `validate_patch`/`rename_symbol` response already hands
back `newVersion` and refreshed `declarationSites` for exactly the symbols it just changed (see each
tool's "Editing the same symbol again" guidance), so a second edit to a symbol you just patched goes
straight into another `validate_patch` call — it does not need a `get_symbol` in between. A genuine
refetch is warranted only for *content* you didn't hold in the first place (a different `include`), for
a symbol this session hasn't touched, or after `workspace_status`/a tool response reports the file
`stale`.

## The large-source guard

An unsliced `source` request on a declaration of **500 lines or more** is answered once with advice
instead of the source. The response is an ordinary one — same `symbolId`, `contentVersion`,
`limitedBy`, `declarationSites` — carrying `members` (on a type) or `bodyOutline` (on anything else)
in place of what was asked for, plus a `guard` block:

```
guard:
  reason: large_source
  declaredLines: 2342
  advice: source would return about 2342 lines, so members is served instead. For one region,
          slice with source:code@start-end off declarationSites. Repeat this call unchanged to
          get the source anyway.
```

**Repeating the call verbatim gets the source.** That is the whole override: no new argument, no
flag to have known about beforehand. Consent is re-sending, because a guard with no way through
would make the genuine case — the whole 2000-line type really is wanted — cost more in region-by-
region fetches than the guard ever saved. The acknowledgement is keyed on the symbol *and* the
`include`, holds for 15 minutes, and refreshes each time you use it, so a task that reads the same
large symbol repeatedly is asked once.

It does not fire on a sliced fetch (`source@120-160`), since the slice already bounds the response,
and it does not fire on `members`, `bodyOutline` or any other component — only on `source`.

**A guard triggered by `include: "all"` still serves a leaseable `members` list**: each member row
carries its own `contentVersion`, exactly as it would outside the guard, so an about-to-edit call
does not lose that just because the type was too large to serve `source` whole.

## Several symbols in one call

`symbols` fetches a list instead of `symbol` fetching one — the same `include` applied to every
entry:

```
get_symbol(symbols: ["Sample.Lib.Widget", "Sample.Lib.IWidget"])
```

`results` is an array, one entry per requested symbol. A successful entry has `symbolId,
contentVersion, limitedBy, content`; a symbol that didn't resolve has `error` instead
(`symbol_not_found`, `ambiguous_symbol`) — one bad lookup doesn't fail the batch. Whatever **every**
entry repeats verbatim (`components` always; `origin`/`containingType` too when every symbol is
from one type) lifts into a `shared` block beside `results`, but only when that's genuinely
smaller. The batch's win is round trips — on tokens it's roughly a wash. An `@` line selection is
rejected in a batch (`lines_with_batch`): one span of file lines can't apply to several symbols.

## Resolution failures

`ambiguous_symbol` hoists the prefix every candidate shares (`sharedPrefix`) so each candidate's
`displayString` carries only the part that differs — concatenate the two, or pass the candidate's
`symbolId` instead.

`symbol_not_found` carries **`didYouMean`** — up to 5 ranked near-misses for the failed name's last
segment, each `{symbolId, name, kind}`. Absent when the segment is under 3 characters, the name was
a `sym_...` id, or nothing ranked. It's a ranked index lookup first (recovers a wrong namespace or
dropped containing type), kept only to hits whose own unqualified name still **contains** the
segment you typed — the index tokenizer splits camel case, so without that filter
`NoSuchSymbolAtAllXyz` ranked against every name merely sharing the token `All` — then an
edit-distance scan (≤3 edits, case-insensitive) only if that leaves nothing. So a genuine typo of
nothing comes back bare rather than with confident nonsense, and the scan costs nothing on the
normal path.

**Every symbol-resolving tool returns this** — `get_references`, `get_call_hierarchy`,
`get_call_slice`, `get_type_hierarchy`, `rename_symbol` too. Candidates are capped at 10;
`totalCandidates` always states the true match count, with `truncated: true` when the cap bit. **A
name like `Run` can match fifty members in a test tree — an absent candidate is not an absent
symbol.** Qualify with the containing type/namespace, or append a parameter list.

## Gate expansion on `referenceCounts`

`{callers, tests, implementations, overrides}` decides whether an expansion is worth the tokens:

- **0 callers** → usually nothing to find. **But not if the symbol can be invoked without being
  named** — `callers` counts only static call sites in the loaded solution, so anything a framework
  invokes by reflection (an entry point, a `[DI]`-registered implementation, a serialization
  target, a test/event handler) is invisible to it. **Never conclude "dead code" from a 0 alone.**
- **1–5 and a signature change is planned** → fetch them.
- **more than 5** → fetch the list without bodies first, then bodies only for what you'll actually edit.

Before writing a helper that plausibly already exists, `search_index` first — one cheap call beats
a duplicate implementation.

## Reference

| Arg | Meaning |
|---|---|
| `symbol` / `symbols` | Fully-qualified name, unique suffix, `Name(ParamType)` to pick an overload, or a `sym_…` id from any earlier response. Exactly one of the two. |
| `include` | Omitted/`"standard"` (default) \| `"all"` \| a comma list that replaces the default. |

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

## Next steps

`referenceCounts` gates what is worth fetching next (above). From here:

- **Expand to call sites** → `get_references` — `get_references.md`
- **Open-ended caller tree** → `get_call_hierarchy` — `get_call_hierarchy.md`
- **Only need part of a long member** → re-call with `include: "source:code@120-160"`
- **Editing part of one you have already read** → `include: "source@120-160"` — unstripped, and
  the slice leases the body layer, so this is a complete pre-edit fetch
- **Editing it** → keep `contentVersion` and `declarationSites`, then `validate_patch` — `validate_patch.md`
