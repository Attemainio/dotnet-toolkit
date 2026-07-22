---
name: dotnet-code-query
description: Use when exploring, searching, inspecting or analyzing C# code in a .NET repo - orienting in the codebase, finding a class/method/symbol, callers or references, interface implementations, or type signatures/APIs. Grep and Read give WRONG ANSWERS on C# - grep cannot see interface, virtual or delegate dispatch, counts comment and string matches as hits, and silently under-reports when output is truncated. Use the dotnet-toolkit MCP tools instead; they answer from a Roslyn semantic model, are complete, and cost a fraction of the tokens.
---

# Retrieving C# code without reading files

This repo has the dotnet-toolkit MCP server. For C# questions, retrieve **symbols**, not
files. The server answers from a live Roslyn semantic model, so it sees calls through
interfaces, virtual dispatch and delegates that a text search cannot.

Tool names below are prefixed `mcp__plugin_dotnet-toolkit_dotnet__`.

## Never fall back to grep

If you find yourself about to run Grep, Glob or Read against a `.cs` file, stop — that is
the mistake this skill exists to prevent. Reach for the MCP tool instead, even when it
costs an extra step to load the tool schema first. Measured on a real repo, `grep -rn` for
a method name found **3 of 5** call sites (truncation dropped two) and would have returned
**58** comment/XML-doc matches to hand-filter; `get_references` returned all 5, no false
positives, fewer tokens. A wrong caller list produces a wrong answer, not a slower one.

The only legitimate reasons to read a file directly: non-C# files (csproj, json, md), or
lines you are about to edit that `get_symbol` did not return.

## Session attribution is automatic

No tool takes a `sessionId`/`taskId` argument — there is nothing to pass. Every call in a
server process shares one ambient session id automatically for that process's whole
lifetime, so usage metrics group correctly with no setup and no way to get it wrong.
`get_retrieval_metrics` reads that same id back via `scope: "session"`; see
`docs/tool-reference.md` for merging several past sessions together or discovering session
ids in a date range with `groupBy: "session"`.

## Decision table

| You want | Call | Do NOT |
|---|---|---|
| Find symbols when you don't know the exact names | `search_index`, **all terms in one call** | Grep/Glob over .cs files; one call per word |
| A type or member's shape, docs, location | `get_symbol` | Read the .cs file |
| A type's member list | `get_symbol` with `include: "members"` | Read the file |
| Callers / usages | `get_references` (`direction: "callers"`) | Grep the name — it misses interface dispatch and returns comment hits |
| Implementations, derived types, overrides | `get_references` (`direction: "implementations"` or `"overrides"`) | Grep for `: IFoo` |
| What is callable at a cursor position — locals, inherited members, extension methods, not just a type's own declared list (that's `get_symbol` with `include:"members"`, no position involved) | `get_scope` | Guess, or grep for a helper that may not apply here |
| Whether a *known* symbol X reaches a *known* symbol Y, and through what path | `get_call_slice` | Walk the graph with repeated `get_references` calls and assemble the chain yourself |
| Who eventually calls/is eventually called by a symbol, several levels deep — an open-ended tree, not one known destination | `get_call_hierarchy` | Chain `get_references` by hand, one level at a time, and assemble the tree yourself |
| A type's full base chain, transitive interfaces, and every derived/implementing type | `get_type_hierarchy` | Guess from `get_symbol`'s one-hop `containingType`, or chain `get_references` |
| The solution's project reference graph | `get_project_graph` | Open every `.csproj` and read `<ProjectReference>` by hand |
| Circular project references | `detect_circular_dependencies` | Manually trace references looking for a loop |
| What a commit or branch actually changed | `get_semantic_diff` | Read `git diff` and infer |
| Why past code looks the way it does | `search_log` | Guess from the code |
| Whether a change is safe | `validate_patch` (see the dotnet-change skill) | `dotnet build` |

Read a .cs file only when you are about to edit lines that `get_symbol` did not give you,
or for non-C# files (csproj, json, md) where Read/Grep are the right tools.

`get_call_slice` needs both `from` and `to` already known — it is point-to-point pathfinding,
not an open-ended walk. For "who calls this, and who calls those, up to the entry points"
(Visual Studio's *View Call Hierarchy*), use `get_call_hierarchy` instead — see below.

## Search for everything at once

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
plain array of objects — `symbolId, name, kind, file, line` on every hit:

```json
{"items":[{"symbolId":"sym_...","name":"Sample.Lib.WidgetExtensions.SpinTwice(IWidget,int)",
           "kind":"Method","file":"Lib/Pipeline.cs","line":6}]}
```

`file`/`line` are resolved from the syntax index at response time — swept for staleness on the
way — so they point at where the declaration is *now*, not where it was when the row was
written. **A name that maps to several declarations (overloads) omits both fields entirely**
(absent, not `null`) rather than pointing at the wrong one; that hit still resolves through
`get_symbol`, which separates overloads by parameter list and always returns exact spans.

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

**If a symbol you are about to edit has no summary, see the `dotnet-change` skill** — a missing
summary on a symbol you touch is not just a gap to note, it's something `validate_patch` should fix
in the same edit.

## Choose exactly what you need with include

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
| `source` | Full declaration source text |
| `xmlDoc` | `{summary, returns, remarks, value, inheritdoc, params, typeParams, exceptions}`, each XML-stripped to plain text; a field is absent when that tag isn't present. `params`/`typeParams` are `[{name, text}]` from `<param>`/`<typeparam>`; `exceptions` is `[{type, text}]` from `<exception>`; `value` is a property's `<value>`; `inheritdoc` is `true` when `<inheritdoc/>` is present. `xmlDoc` itself is absent only when none of these tags are present at all — a doc comment with a `<returns>` but no `<summary>` still surfaces `xmlDoc.returns` |
| `mechanicalFacts` | Server-computed structural facts as opaque JSON; `null` if the body changed since computed |
| `referenceCounts` | `{implementations, overrides}` always; adds `{callers, tests}` for a member (never for a type) |
| `recentLog` | Last few dev-log entries touching this symbol, each flagged `current:true/false` against the live body |
| `members` | For a type only: `[{symbolId, displayString, kind, contentVersion}]` per member; `null` otherwise |
| `attributes` | This symbol's own (non-inherited) C# attributes as `[{name, arguments}]` — e.g. `[Authorize(Roles="Admin")]` reads back as `{name: "Authorize", arguments: "Roles = Admin"}`. `name` strips a trailing `Attribute` suffix. Absent when there are none |
| `modifiers` | The literal C# modifier phrase in declaration order, e.g. `"public sealed"`, `"public override"`, `"public static readonly"`. Declaration-only — same cost tier as `xmlDoc`, no body walk |
| `baseType` | For a type only: `{symbolId, displayString}` for its direct base type — one hop, not the transitive chain (`get_type_hierarchy` owns that). Absent for anything else |
| `interfaces` | For a type only: `[{symbolId, displayString}]` for its direct interfaces (not `AllInterfaces`). Absent for anything else |

Examples:

- `include: "all"` minus the body — spell out the rest instead of subtracting:
  `include: "xmlDoc,mechanicalFacts,referenceCounts,recentLog"`. Use when you want facts and
  history but are not going to edit the symbol.
- `include: "members"` — a type's API surface with no bodies and none of the standard extras.
- `include: "xmlDoc"` — the leanest non-default fetch: just the skeleton plus the doc breakdown,
  no `referenceCounts` latency cost (it waits on the semantic model) and no history lookup.
- `include: "attributes"` — check `[Authorize]`/`[AllowAnonymous]`/`[Obsolete]` presence on a
  member without a `source` fetch; the review agent's `[security]` and `[docs]` aspects use this.

An unrequested component is absent from the JSON entirely, not null, so it costs nothing. A
misspelled name is an `invalid_component` error rather than being silently dropped.

### Location is always there

Every `get_symbol` response carries `declarationSites` — `file`, `startLine`, `endLine` —
regardless of `include`. It is part of the unconditional skeleton (`kind`, `displayString`,
`accessibility`, `containingType`, `declarationSites`), and the spans are computed live rather
than read from a cache, so they are correct even for a symbol split across partial-class files.

That means **"where does this live?" never costs a second call or an extra component**, and those
spans are exactly what a `validate_patch` edit takes. Do not reach for `include: "all"` just to
find a line number — the default `standard` call already carries `declarationSites`.

**A narrowed response returns a narrowed version token**, covering only the layers it served.
That is deliberate: escalating later (a `standard` fetch → `include: "all"`) with that token
returns the new content rather than a false `changed: false`. It also means you cannot lease a
wide request against a narrow token — hold the token from a fetch of the same shape.

### Several symbols in one call

`symbols` fetches a list instead of `symbol` fetching one — same `include` applied to every entry:

```
get_symbol(symbols: ["Sample.Lib.Widget", "Sample.Lib.IWidget"])
```

The response's `results` is a plain array with one entry per requested symbol, and each entry is
*exactly* what a single-symbol `get_symbol` call for that symbol would have returned — batching is
an orchestration convenience, not a different response shape, so there is no fixed column set to
learn and nothing gets hoisted out of `content`. A successful entry has `symbolId, contentVersion,
limitedBy, content`; a symbol that did not resolve has `error` instead (`symbol_not_found`,
`ambiguous_symbol`) with no `symbolId`/`contentVersion`/`content` — one failed lookup does not fail
the batch, and the two shapes are told apart by which keys are present.

`knownVersion`/`refetch` do not apply to a batch — leasing needs one token per symbol, which a
single `knownVersion` cannot express — so every batch result carries full content regardless of
what you already hold.

## Questions symbol lookup cannot answer

These tools answer questions that no amount of `get_symbol` will. Every example below is a
real call against this plugin's own repo, with its real response.

### `get_scope` — what is callable *here*

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

### `get_call_slice` — how does X reach Y

The shortest call path between two symbols **you can already name**. Use it for "does X
reach Y, and through what" instead of walking outwards with repeated `get_references` calls,
which costs a round trip per hop and leaves you assembling the chain yourself.

Call it when both endpoints are known — e.g. confirming a proposed removal is safe by
checking whether an entry point still reaches it, or explaining an unexpected side effect by
finding the path between the trigger and the symbol that causes it. It requires `to` as well
as `from`: it cannot answer an open-ended "who (eventually) calls this" with no destination
in mind — that's `get_call_hierarchy`, below.

```
get_call_slice(from: "PatchTools.ValidatePatch", to: "FeatureLogStore.Append")
```

```json
{"found": true, "depth": 2, "nodesExplored": 69,
 "path": [{"displayString": "Task<string> PatchTools.ValidatePatch(...)"},
          {"displayString": "void PatchTools.AppendLog(...)"},
          {"displayString": "string FeatureLogStore.Append(LogEntry entry)"}]}
```

A miss is still informative: it reports the nearest reachable frontier from each end, which
tells you where the chain actually breaks. `found: false` means no path within `maxDepth`
(default 8) — not necessarily no relationship.

### `get_call_hierarchy` — who eventually calls this, up to the entry points

An open-ended multi-level call tree from one symbol — Visual Studio's *View Call Hierarchy*,
which `get_call_slice` structurally cannot answer (it needs a known `to`). `direction:
"callers"` (default) walks upward toward entry points; `"callees"` walks downward into what
the symbol invokes. Every node carries `symbolId` + `displayString`; add `kind`/`file`/`line`
via `fields`.

Call it to answer "if I change this, how much does it ripple" — `includeTree: false` returns
only the `blastRadius` summary (unique nodes reached, per depth) for the cheapest possible
version of that question, without paying for the full tree.

```
get_call_hierarchy(symbol: "FeatureLogStore.Append", direction: "callers", maxDepth: 1)
```

```json
{"root": {"symbolId": "sym_c25d...", "displayString": "string FeatureLogStore.Append(LogEntry entry)"},
 "direction": "callers",
 "tree": {"symbolId": "sym_c25d...", "displayString": "string FeatureLogStore.Append(LogEntry entry)",
   "children": [
     {"symbolId": "sym_0e0e...", "displayString": "int DevlogMigration.Run(...)"},
     {"symbolId": "sym_c3fc...", "displayString": "void FeatureLogStoreTests.ResolveIdChain_SingleHop_ReturnsBothIds()"},
     {"symbolId": "sym_2b15...", "displayString": "void PatchTools.AppendLog(...)"}, "...4 more"]},
 "blastRadius": {"totalUniqueNodes": 8, "perDepth": [1, 7], "depthCapped": true}}
```

`depthCapped: true` here means `Append` has callers beyond `maxDepth: 1` — raise it to see further up the
chain (a real 3-level pull from this same root reaches `PatchTools.ValidatePatch`, the actual MCP tool
entry point, at depth 3).

A symbol reached through two different branches (a diamond) legitimately appears twice in the
tree — that isn't deduped, since collapsing it would hide a real second route in — but counts
once in `blastRadius`. True recursion (a symbol reappearing on its own root-to-node path) stops
as a leaf marked `recursive: true` rather than looping. `maxDepth` defaults to 3 and clamps to
8; a well-connected graph grows fast, so start shallow and increase only if the answer needs
it, or lean on `blastRadius.depthCapped` to see whether a branch was still expanding when the
cap hit.

### `get_type_hierarchy` — a type's full inheritance shape

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

### `get_project_graph` — the solution's project reference graph

Which `.csproj` references which, and the reverse (`referencedBy`) — computed live from the
loaded solution every call, no caching (project counts are small). Pass `project` to scope to
one project's direct references and dependents instead of the whole graph.

```
get_project_graph()
```

```json
{"projects": [
   {"name": "DotnetToolkit.McpServer", "references": [], "referencedBy": ["DotnetToolkit.McpServer.Tests"]},
   {"name": "DotnetToolkit.McpServer.Tests", "references": ["DotnetToolkit.McpServer"], "referencedBy": []}],
 "totalProjects": 2}
```

### `detect_circular_dependencies` — a real dependency loop, not just deep nesting

Cycles in the solution's project reference graph. `scope: "project"` (default, and for now the
only supported value) reports one representative cycle per strongly-connected component found
— not every distinct cycle within it, which can be combinatorial. `scope: "type"` returns
`error: "unsupported_scope"` rather than a partial answer: it would need collapsing
member-level call edges up to their containing type, which this server does not do today.

```
detect_circular_dependencies()
```

```json
{"scope": "project", "cycles": [], "totalCycles": 0}
```

An empty `cycles` array is a checked "found none," not silence — this repo has no known
project cycles today.

### `get_semantic_diff` — what changed, semantically

Symbols added, removed and changed between two git refs, with which version layers moved and
the API impact. Use it instead of reading a textual diff to judge a commit or a branch.

```
get_semantic_diff(fromRef: "9f20936~1", toRef: "9f20936")
```

```json
{"symbolsAdded": ["...Store.SearchText::method ForIndex/1", "..."],
 "symbolsChanged": [{"displayString": "...Store.SymbolStore::method Search/3",
                     "layersChanged": ["body"], "apiImpact": "non-breaking"}],
 "apiImpactSummary": {"breaking": 0, "nonBreaking": 3, "added": 16, "removed": 0}}
```

It is trivia-blind, so a formatting- or comment-only commit correctly reports **no change**.
Read an all-empty result as "nothing semantic moved" — which also covers a commit that only
touched non-C# files. Defaults are `HEAD~1`..`HEAD`.

## Gate expansion on referenceCounts

`get_symbol` returns `referenceCounts: { callers, tests, implementations, overrides }`. Use it to
decide whether an expansion is worth the tokens:

- **0 callers** → usually nothing to find; skip `get_references`. **But not if the symbol can
  be invoked without being named** — see below.
- **1–5 and you plan a signature change** → fetch them.
- **more than 5** → fetch the list without bodies first, then bodies only for the ones you
  will actually edit.

`callers` counts **static call sites in the loaded solution**. Anything a framework invokes
by reflection is invisible to it, so for such a symbol the number measures only who else
happens to call it directly — which is incidental. In this plugin's own code
`HistoryTools.SearchLog` reports 0 callers and `ContextTools.GetSymbol` reports 3, purely
because tests call one by name and not the other; both are live MCP tools reached the same
way. Treat 0 as "no information" rather than "unused" when the symbol is an entry point, has
a registration attribute (its own or on its type), is a DI-registered implementation, a
serialization target, or a test/event handler. Never conclude "dead code" from a 0 alone.

A count is **omitted entirely** when it could not be measured — the workspace is still
loading, or the symbol's project contributed no edges. Absent is not 0: absent means unknown.
`callers` and `tests` are also omitted for named types, where call edges are recorded against
members and a type-level count would structurally always be zero.

Before writing a helper that plausibly already exists, check with `search_index` first —
one cheap call beats a duplicate implementation.

## Addressing a symbol

`get_symbol` and `get_references` accept any of:

- a fully-qualified name — `PandaAI.Core.Training.TrainingService.StartTrainingAsync`
- a unique suffix — `TrainingService.StartTrainingAsync`, or just `StartTrainingAsync`
- a parameter list to pick an overload — `TrainingService.StartTrainingAsync(TrainingRequest)`
- **a `sym_…` id returned by any previous response** — search hits, reference items and
  `suggestedInspection` entries all carry one, and passing it back is unambiguous

Ambiguity is never guessed: you get `error: "ambiguous_symbol"` plus a candidate list.

## Version tokens and leases

Every content response carries a `contentVersion` like `decl:a1b2…|body:84c3…`. It is a
lease: hold it, and pass it back later as `knownVersion`.

- All supplied layers still match → `changed: false` and **no content is sent**. Your copy
  is current; carry on using it.
- Something moved → you get fresh content.
- **Only pass `knownVersion` when you actually still hold the content.** If you never held
  it, you are asking whether something you do not have has changed.
- Escalating is safe: a request needing components your token does not carry returns content
  rather than `changed: false`, so `include:"xmlDoc"` → `include:"xmlDoc,source"` against an
  xmlDoc-only token gives you the source. You still get a wasted round trip if you lease for
  content you never held — the lease just will not silently hand you nothing.

The layers are meaningful: same `decl` with a different `body` means the API is unchanged
and only the implementation moved.

### After context compaction

If your context was summarized and the content is gone but you still have the version
token, call `get_symbol` with **`refetch: true`** *and* `knownVersion`. That returns the
content and correctly records the refetch as compaction-driven rather than waste.

## Workspace readiness

`limitedBy` names what the answer could **not** draw on — the tier it came from, or the content
it was built on.

- **absent** — fully informed. Silence is the healthy case.
- **`index_only`** — answered from the syntax tier, or before the semantic index finished its
  first pass. Reference counts and semantic resolution are unavailable, **not zero**.
- **`stale`** — the file this symbol was served from has changed on disk since the workspace read
  it, so the content is behind what is actually there. Call `reload_workspace`, then re-read: line
  spans will have moved. Do not build a patch on a `stale` response — `validate_patch` refuses it
  with `stale_workspace` anyway, because applying it would revert whatever else changed in that
  file.
- **`degraded`** — the workspace loaded but one or more projects failed (commonly a restore done
  by a different SDK than the server runs on). Results may be silently **wrong**, not just thin:
  symbols from failed projects are missing, and attribute-derived facts like `tests` can be
  false across the board. Call `workspace_status` for the diagnostics, fix the build, then
  `reload_workspace`. Do not report findings from a degraded workspace without saying so.

`search_index` and `get_symbol` answer from the syntax index immediately; while the MSBuild
workspace is still loading they return `limitedBy: "index_only"` (and `referenceCounts` may
be absent — do not read that as "no callers"). `get_references` needs live semantics and
returns `error: "workspace_loading"` until it is ready — wait briefly and retry, or check
`workspace_status`. After a large git operation, call `reload_workspace`.

## Reading responses

Responses are deliberately terse: fields that are absent carry no information.
`limitedBy` appears only when something limited the answer, `changed` only when `false`, `truncated`
only when true. Absence of `tests` in `referenceCounts` means "not computed yet", **not**
"no tests".

By default responses are rendered as **TOON** (Token-Oriented Object Notation) rather than JSON text —
still the same field names and nesting described throughout this skill, just a more compact encoding of
them: every multi-item response is a plain array of objects in either case, there is no separate
columns/rows table shape. Call `set_output_format(format: "compact")` (minified JSON) or `format: "json"`
(pretty-printed) to get JSON text back for the rest of the session; `defaultFormat` in
`.claude/dotnet-toolkit/config.json` only sets what a fresh server starts with.

`tests` is the subset of `callers` whose own declaration carries a test attribute (`[Fact]`,
`[Theory]`, `[Test]`, `[TestCase]`, `[TestMethod]`), so it can never exceed `callers`. A helper
that merely lives in a test project is not counted. `get_references` marks the same callers with
`isTest: true`, emitted only on the ones that are tests — an absent flag means "not a test".

### `get_references`' `items` fields

Each item is a plain object carrying `symbolId`, `contentVersion`, `displayString`, `sites`, and
`dispatchKind` on every call. `isTest` and `content` (the inline body, only present with
`includeBodies: true`) are present only when they apply — absent, not `null`, on a caller that
isn't a test or wasn't fetched with a body.
