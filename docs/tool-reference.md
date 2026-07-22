# dotnet-toolkit tool reference

The complete per-tool catalog: what each MCP tool does, its arguments, a real call against this
plugin's own repo with its real response, and what it replaces. This is the doc `dotnet-toolkit-init`
points a consuming repo's `CLAUDE.md` at, and the doc to update whenever a tool's name, arguments,
defaults or response shape changes (see CLAUDE.md's "Changing the tool surface" section — this file is
one of the surfaces that has to move with the code).

Tool names below are prefixed `mcp__plugin_dotnet-toolkit_dotnet__` when called. No tool takes a
`sessionId`/`taskId` argument — every call in a server process shares one ambient session id
automatically; see `skills/dotnet-code-query/SKILL.md` and `get_retrieval_metrics` below.

Responses are deliberately terse: a field that is absent carries no information and costs no tokens.
`limitedBy` appears only when something limited the answer (`index_only`, `stale`, `degraded`) — see
"Workspace readiness" at the end of this file. This applies to plain objects too, not just top-level
envelopes: a `null` field is dropped from JSON entirely rather than written as `"field":null`, so check
for the key's absence, not its value.

By default every response is rendered as **TOON** (Token-Oriented Object Notation), a compact
JSON-equivalent text format — not JSON text. Call `set_output_format(format: "compact")` for minified
JSON or `format: "json"` for pretty-printed JSON if something in your workflow needs to parse responses
as JSON directly; it takes effect immediately and persists for the rest of the session. `defaultFormat`
in `.claude/dotnet-toolkit/config.json` sets the format a fresh server starts with, before anything calls
`set_output_format`. The field names and nesting documented below are identical in all three formats,
only the wire encoding changes (see `search_log(query: "contract")` for the 3.10 rationale) — every multi-item response
below is a plain array of objects, whether read as TOON or JSON; there is no separate columns/rows table
representation to learn.

## Retrieval

### `get_symbol` — a symbol's shape, docs, location, source

Replaces: `Read` on a `.cs` file. Read gives you one fragment of a symbol split across partial-class
files with no signal the rest exists, and costs the whole file's tokens for the part you wanted.

| Arg | Meaning |
|---|---|
| `symbol` / `symbols` | Fully-qualified name, unique suffix, `Name(ParamType)` to pick an overload, or a `sym_…` id from any earlier response. Exactly one of the two. `symbols` batches several under one `include`. |
| `include` | Omitted/`"standard"` (default: `xmlDoc, referenceCounts, recentLog`) \| `"all"` (every component) \| a comma list that REPLACES the default, e.g. `"source,members"`. |
| `knownVersion` | A held `contentVersion` to lease against — single-symbol only. |
| `refetch` | Force content even if the lease would otherwise say `changed:false`. |

Component names are exactly the response fields they control:

| Component | Returns |
|---|---|
| `source` | Full declaration source text |
| `xmlDoc` | `{summary, returns, remarks, value, inheritdoc, params, typeParams, exceptions}`, each XML-stripped to plain text; a field is absent when that tag isn't in the doc comment. `params`/`typeParams` are `[{name, text}]` from `<param>`/`<typeparam>`; `exceptions` is `[{type, text}]` from `<exception>`; `inheritdoc` is `true` when `<inheritdoc/>` is present. `xmlDoc` itself is absent only when none of these tags are present at all |
| `mechanicalFacts` | Server-computed structural facts as opaque JSON; `null` if the body changed since computed |
| `referenceCounts` | `{implementations, overrides}` always; adds `{callers, tests}` for a member (never for a type) |
| `recentLog` | Recent dev-log entries touching this symbol, each flagged `current:true/false` against the live body |
| `members` | For a type only: `[{symbolId, displayString, kind, contentVersion}]` per member |
| `attributes` | This symbol's own (non-inherited) C# attributes as `[{name, arguments}]`; `name` strips a trailing `Attribute` suffix (e.g. `[Obsolete]` → `"Obsolete"`); `arguments` is a compact rendering of constructor/named arguments, truncated rather than reproduced in full for a long string. Absent when the symbol has no attributes |
| `modifiers` | The literal C# modifier phrase in declaration order — `"public static readonly"`, `"public sealed"`, `"public override"`, etc. Declaration-only: no semantic-model body walk, same cost tier as `xmlDoc` |
| `baseType` | For a type only: `{symbolId, displayString}` for its direct base type (not `object` filtered out, not the transitive chain — `get_type_hierarchy` owns that). Absent for anything else, including when a type has no explicit base |
| `interfaces` | For a type only: `[{symbolId, displayString}]` for its direct interfaces (not `AllInterfaces`). Absent for anything else |

The skeleton — `kind`, `displayString`, `accessibility`, `containingType`, `declarationSites` (`file`,
`startLine`, `endLine`) — is unconditional: every call gets it regardless of `include`, and those line
spans are exactly what a `validate_patch` edit takes. When the symbol has a leading `///` XML doc
comment, `startLine` (and `source`) begin at the comment, not at the attribute/signature line after
it — so an edit built from `declarationSites` can rewrite the doc comment along with the declaration.

Default call, real response:

```
get_symbol(symbol: "FeatureLogStore.Append")
```

```json
{"symbolId":"sym_c25d7c88b0e916b0","contentVersion":"decl:ddca3badaba1|refs:532f4bebd9ac",
 "content":{"kind":"Method","displayString":"string FeatureLogStore.Append(LogEntry entry)",
   "accessibility":"public",
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

`results` becomes an array with one entry per requested symbol, and each entry is exactly what a
single-symbol call for that symbol would have returned — batching is an orchestration convenience, not a
different response shape. A symbol that did not resolve has `error` set (`symbol_not_found`,
`ambiguous_symbol`) instead of `symbolId`/`contentVersion`/`content` — one bad lookup does not fail the
batch, and the two shapes are told apart by which keys are present, not by a shared fixed column set.
`knownVersion`/`refetch` do not apply to a batch: every entry carries full content.

### `get_references` — callers, implementations, overrides

Replaces: grep for a name. Grep cannot see interface, virtual or delegate dispatch, counts comment and
string matches as hits, and silently drops sites when output is truncated.

| Arg | Meaning |
|---|---|
| `symbol` | Required. Same addressing as `get_symbol`. |
| `direction` | `callers` (default) \| `implementations` \| `overrides`. |
| `includeBodies` | Inline each caller's source (default `false` — fetch bodies only for the ones you'll actually edit). |

Real call and response (trimmed):

```
get_references(symbol: "FeatureLogStore.Append")
```

```json
{"targetSymbolId":"sym_c25d7c88b0e916b0",
 "items":[
   {"symbolId":"sym_0e0e...","contentVersion":"decl:66cc...|body:badd...",
    "displayString":"int DevlogMigration.Run(DevlogStore devlog, FeatureLogStore log, ILogger logger)",
    "sites":[{"file":"src/DotnetToolkit.McpServer/Devlog/DevlogMigration.cs","line":29,
              "snippet":"log.Append(new FeatureLogStore.LogEntry("}],
    "dispatchKind":"direct"},
   {"symbolId":"sym_2b15...","contentVersion":"decl:8c14...|body:785d...",
    "displayString":"void PatchTools.AppendLog(FeatureLogStore featureLog, ...)",
    "sites":[{"file":"src/DotnetToolkit.McpServer/Tools/PatchTools.cs","line":190,
              "snippet":"featureLog.Append(new FeatureLogStore.LogEntry("}],
    "dispatchKind":"direct"}],
 "totalItems":2,"excludedTextMatches":1}
```

Each item carries `symbolId, contentVersion, displayString, sites, dispatchKind` on every call;
`isTest` (emitted only when `true`) and `content` (with `includeBodies:true`) are present only when they
apply — absent, not `null`, otherwise. `excludedTextMatches` is the count of comment/string matches a
grep would have wrongly included — 1 here, correctly excluded.

### `search_index` — find symbols when you don't know exact names

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

Real call and response:

```
search_index(query: "validate_patch FeatureLogStore", limit: 5)
```

```json
{"items":[
   {"symbolId":"sym_dd78...","name":"DotnetToolkit.McpServer.Tools.PatchTools.ValidatePatch(...)",
    "kind":"Method","file":"src/DotnetToolkit.McpServer/Tools/PatchTools.cs","line":29},
   {"symbolId":"sym_17cd...","name":"DotnetToolkit.McpServer.Store.FeatureLogStore.LogEntry",
    "kind":"Record","file":"src/DotnetToolkit.McpServer/Store/FeatureLogStore.cs","line":22},
   {"symbolId":"sym_fc34...","name":"DotnetToolkit.McpServer.Store.FeatureLogStore",
    "kind":"Type","file":"src/DotnetToolkit.McpServer/Store/FeatureLogStore.cs","line":10}]}
```

`name` is directly usable as `get_symbol`'s `symbol` argument. A hit whose name maps to several
overloads has `file`/`line` absent rather than pointing at the wrong one — resolve it through
`get_symbol` instead, which separates overloads by parameter list.

Scoped to one folder — `pathPrefix` narrows the same ranked search to a subsystem instead of the
whole repo:

```
search_index(query: "Search", kinds: "method", pathPrefix: "src/DotnetToolkit.McpServer/Store", limit: 5)
```

```json
{"items":[
   {"symbolId":"sym_6c0b...","name":"DotnetToolkit.McpServer.Store.SearchText.Segments(string)",
    "kind":"Method","file":"src/DotnetToolkit.McpServer/Store/SearchText.cs","line":63},
   {"symbolId":"sym_a487...","name":"DotnetToolkit.McpServer.Store.SearchText.ForIndex(string)",
    "kind":"Method","file":"src/DotnetToolkit.McpServer/Store/SearchText.cs","line":18}]}
```

Filtering by modifier and by interface — `"Widget"` matches both `Widget` and `TurboWidget`;
`modifiers: "public sealed"` (AND, not OR) narrows to the one that is both:

```
search_index(query: "Widget", kinds: "class", modifiers: "public sealed", limit: 5)
```

```json
{"items":[
   {"symbolId":"sym_...","name":"Sample.Lib.TurboWidget","kind":"Type",
    "file":"Lib/Widget.cs","line":16}]}
```

`implements` narrows to direct implementers of a named interface instead — both widgets implement
`IWidget`, so both come back:

```
search_index(query: "Widget", kinds: "class", implements: "IWidget", limit: 5)
```

```json
{"items":[
   {"symbolId":"sym_...","name":"Sample.Lib.Widget","kind":"Type","file":"Lib/Widget.cs","line":9},
   {"symbolId":"sym_...","name":"Sample.Lib.TurboWidget","kind":"Type","file":"Lib/Widget.cs","line":16}]}
```

Checking documentation coverage before spending a `get_symbol` call — `summary: "has"` is the cheap
presence check, no text sent:

```
search_index(query: "Spin", kinds: "method", summary: "has")
```

```json
{"items":[
   {"symbolId":"sym_...","name":"Sample.Lib.Widget.Spin(int)","kind":"Method",
    "file":"Lib/Widget.cs","line":12,"hasSummary":true},
   {"symbolId":"sym_...","name":"Sample.Lib.WidgetExtensions.SpinTwice(IWidget,int)","kind":"Method",
    "file":"Lib/Pipeline.cs","line":6}]}
```

`SpinTwice` has no `hasSummary` key at all (not `false`) — it has no `<summary>` doc comment. Ask for
the actual text with `summary: "full"` instead of a follow-up `get_symbol`:

```
search_index(query: "Widget.Spin", kinds: "method", summary: "full")
```

```json
{"items":[{"symbolId":"sym_...","name":"Sample.Lib.Widget.Spin(int)","kind":"Method",
   "file":"Lib/Widget.cs","line":12,"summary":"Spins the widget."}]}
```

## Relationships & flow

### `get_scope` — what is callable *here*

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
   {"displayString":"string FeatureLogStore.Append(LogEntry entry)","kind":"Method",
    "origin":"member","definedIn":"FeatureLogStore"},
   {"displayString":"int FeatureLogStore.EntryCount()","kind":"Method",
    "origin":"member","definedIn":"FeatureLogStore"},
   {"displayString":"bool object.Equals(object? obj)","kind":"Method",
    "origin":"inherited","definedIn":"object"}]}
```

`origin` separates what the type itself declares from what it inherits — usually the first thing you
want to know. Drop `receiver` to see what's in scope at that line generally. (The line number above
tracks a real call site in this file; if a future refactor moves it, re-find the receiver with
`search_index`/`get_symbol` rather than assuming this line still resolves.)

### `get_call_slice` — how does X reach Y

Replaces: walking the graph with repeated `get_references` calls — one round trip per hop, and you
assemble the chain yourself.

**Point-to-point only — both `from` and `to` must already be known.** It cannot answer an
open-ended "who (eventually) calls this" with no destination in mind; that's `get_call_hierarchy`,
below. Reach for `get_call_slice` when you can name both ends — e.g. confirming a proposed removal is
safe by checking whether a known entry point still reaches it, or explaining an unexpected side effect
by finding the path between a known trigger and the symbol that causes it.

| Arg | Meaning |
|---|---|
| `from`, `to` | Origin/destination symbols, same addressing as `get_symbol`. |
| `maxDepth` | Default 8. |

Real call and response:

```
get_call_slice(from: "PatchTools.ValidatePatch", to: "FeatureLogStore.Append")
```

```json
{"found":true,"depth":2,"nodesExplored":71,
 "path":[
   {"symbolId":"sym_dd78...","displayString":"Task<string> PatchTools.ValidatePatch(...)"},
   {"symbolId":"sym_2b15...","displayString":"void PatchTools.AppendLog(...)"},
   {"symbolId":"sym_c25d...","displayString":"string FeatureLogStore.Append(LogEntry entry)"}]}
```

`found: false` means no path within `maxDepth` — not necessarily no relationship. It still reports the
nearest reachable frontier from each end, so you know where the chain actually breaks.

### `get_call_hierarchy` — who eventually calls this, up to the entry points

Replaces: chaining `get_references(direction: "callers")` by hand, one level at a time, and assembling
the tree yourself. This is what `get_call_slice` cannot do — it needs a known destination; this tool
needs only a root. `direction: "callers"` (default, Visual Studio's *View Call Hierarchy*) walks
upward toward entry points; `"callees"` walks downward into what the symbol invokes.

| Arg | Meaning |
|---|---|
| `symbol` | Required. Same addressing as `get_symbol`. |
| `direction` | `callers` (default) \| `callees`. |
| `maxDepth` | Default 3, clamped 1-8 — a well-connected graph grows fast past that. |
| `maxChildrenPerNode` | Default 25, clamped 1-200. A node past the cap keeps its own entry but stops expanding, marked `truncated:true` with `omittedChildren`. |
| `includeTree` | Default `true`. Set `false` for just `blastRadius` — the cheapest possible answer to "how much does changing this ripple." |
| `fields` | Comma list adding `kind`, `file`, `line` to every node beyond the always-present `symbolId`/`displayString`. |

Real call and response (trimmed to 4 of 7 children):

```
get_call_hierarchy(symbol: "FeatureLogStore.Append", direction: "callers", maxDepth: 1)
```

```json
{"root":{"symbolId":"sym_c25d...","displayString":"string FeatureLogStore.Append(LogEntry entry)"},
 "direction":"callers",
 "tree":{"symbolId":"sym_c25d...","displayString":"string FeatureLogStore.Append(LogEntry entry)",
   "children":[
     {"symbolId":"sym_0e0e...","displayString":"int DevlogMigration.Run(DevlogStore devlog, FeatureLogStore log, ILogger logger)"},
     {"symbolId":"sym_c3fc...","displayString":"void FeatureLogStoreTests.ResolveIdChain_SingleHop_ReturnsBothIds()"},
     {"symbolId":"sym_2b15...","displayString":"void PatchTools.AppendLog(...)"}]},
 "blastRadius":{"totalUniqueNodes":8,"perDepth":[1,7],"depthCapped":true}}
```

`depthCapped:true` means `Append` has callers beyond `maxDepth:1` — raising `maxDepth` reaches
`PatchTools.ValidatePatch` (the actual MCP tool entry point) three levels up from this root.

A caller resolved through the edge cache but absent from the `symbols` table — a synthesized entry
point like C#'s top-level-statements `Main`, which `get_references` renders as
`<top-level-statements-entry-point>` via live Roslyn but the cache never stored a row for — falls back
to its bare `symbolId` as `displayString` rather than failing the whole call. Rare in practice (this
repo hits it once, at `DevlogMigration.Run`'s own caller), but worth recognizing if a leaf's
`displayString` looks like a `sym_...` id instead of a real signature.

A symbol reached through two different branches (a diamond) legitimately appears twice in the tree —
not deduped, since collapsing it would hide a real second route in — but counts once in `blastRadius`.
True recursion (a symbol reappearing on its own root-to-node path) stops as a leaf marked
`recursive:true` rather than looping. Internally capped at a few thousand total nodes as a safety net
against pathological fan-out, independent of `maxChildrenPerNode`.

### `get_type_hierarchy` — a type's full inheritance shape

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

## Solution structure

### `get_project_graph` — which project references which

Replaces: opening every `.csproj` and reading `<ProjectReference>` entries by hand. Computed live from
the loaded solution on every call, no caching — project counts are always small (tens, not thousands).

| Arg | Meaning |
|---|---|
| `project` | Optional project name to scope to one project's direct references + dependents. Omit for the whole graph. |

Real call and response:

```
get_project_graph()
```

```json
{"projects":[
   {"name":"DotnetToolkit.McpServer","references":[],"referencedBy":["DotnetToolkit.McpServer.Tests"]},
   {"name":"DotnetToolkit.McpServer.Tests","references":["DotnetToolkit.McpServer"],"referencedBy":[]}],
 "totalProjects":2}
```

A project named in `workspace_status`'s load diagnostics carries `degraded:true` on its own entry, in
addition to the envelope-level `limitedBy:"degraded"`.

### `detect_circular_dependencies` — a real dependency loop, not just deep nesting

Replaces: manually tracing project references looking for a loop. Cycles in the solution's project
reference graph via Tarjan's SCC.

| Arg | Meaning |
|---|---|
| `scope` | `project` (default, and for now the only supported value) \| `type` — returns `error:"unsupported_scope"` rather than a partial answer; type-level cycle detection would need collapsing member-level call edges up to their containing type, which this server does not do today. |

Reports one representative cycle per strongly-connected component found — not every distinct cycle
within it, which can be combinatorial.

```
detect_circular_dependencies()
```

```json
{"scope":"project","cycles":[],"totalCycles":0}
```

An empty `cycles` array is a checked "found none," not silence — this repo has no known project
cycles today.

## History

### `get_semantic_diff` — what changed, semantically

Replaces: reading a textual `git diff` and inferring. Trivia-blind, so a formatting- or comment-only
commit correctly reports no change.

| Arg | Meaning |
|---|---|
| `fromRef` | Default `HEAD~1`. |
| `toRef` | Default `HEAD`. |

Real call and response (trimmed):

```
get_semantic_diff(fromRef: "HEAD~1", toRef: "HEAD")
```

```json
{"range":{"from":"HEAD~1","to":"HEAD","commits":1},
 "symbolsAdded":["...Output::type JsonHoist","...Output.JsonHoist::method Split/1", "..."],
 "symbolsRemoved":["DotnetToolkit.McpServer.Tools.ContextTools::method GetSymbol/15"],
 "symbolsChanged":[
   {"displayString":"...Contract::field Id","layersChanged":["body"],"apiImpact":"non-breaking"},
   {"displayString":"...ContextTools::method GetReferences/9",
    "layersChanged":["decl","body"],"apiImpact":"breaking-public"}],
 "apiImpactSummary":{"breaking":1,"nonBreaking":5,"added":11,"removed":1}}
```

`apiImpactSummary` is the fastest way to judge whether a commit or branch is safe to build on top of
without reading a single line of diff.

### `search_log` — why past changes were made

Replaces: guessing from the code, or re-proposing a design that was already tried and rejected. Only
covers changes applied through `validate_patch` — an empty result is not proof nothing relevant
happened, just that nothing relevant went through this tool.

| Arg | Meaning |
|---|---|
| `query` | Free-text over recorded intents; omit to list the most recent entries. |
| `limit` | Default 10. |

Real call and response (trimmed to the fields that matter):

```
search_log(limit: 3)
```

```json
{"items":[
   {"logId":"log_01KY07FZ...","date":"2026-07-20",
    "intent":"Fix get_symbol's [Description]: the batch-mode response was documented as an array, but it's actually column-shaped like search_index/get_references",
    "tags":[]},
   {"logId":"log_01KY07F8...","date":"2026-07-20",
    "intent":"Remove unused toolCallId/patchId/validationAttemptId parameters from Error/StaleBase/BuildResponse, ...",
    "tags":[]}]}
```

Each entry carries `logId, date, intent, tags` (`tags` a real JSON array).

## Write path

### `validate_patch` — the only way `.cs` edits should reach disk

Replaces: `Edit`/`Write` on a `.cs` file, followed by `dotnet build` and hoping. Runs your edit against a
**forked in-memory solution** and reports honestly whether the result compiles at the level the change
actually needs — writes to disk only when it does, and only when you ask it to.

| Arg | Meaning |
|---|---|
| `baseVersions` | Required. `{symbolId: contentVersion}` for every symbol you're changing, from a `get_symbol` you actually hold. A mismatch is `error: "stale_base"` with current versions — refetch and rebuild. |
| `edits` | `[{file, startLine, endLine, newText}]` — the line span comes straight from `get_symbol`'s `declarationSites`. |
| `requestedLevel` | Optional floor: `parse` \| `semantic_bind` \| `project_compile` \| `dependent_compile` \| `targeted_tests` \| `solution_validate`. Raises, never lowers, the level the ladder runs to. |
| `applyOnSuccess` | Commit to disk when sufficient and successful (default `false`). Safe to send `true` from the start — nothing is written unless both hold. |
| `intent` | **Required when `applyOnSuccess: true`.** One sentence of *why*, in user terms — this is the only thing that writes to the development log. |

The response carries `completedLevel`, `requiredLevel`, `isSufficient`, `succeeded`, `applied`. Done
means all of: `isSufficient: true`, `succeeded: true`, `applied: true` (or a deliberate choice not to
apply). `succeeded: true` with `isSufficient: false` is a **partial** green — the code compiles only up
to `completedLevel`, and `nextAction` says what to do next (usually resubmit with `requestedLevel`
raised). Never report a partial as done.

`detectedChanges` and, on failure, `diagnostics.rootCauses` are both plain arrays of objects. Each root
cause is pre-distilled — one entry per root cause, not one per compiler error — carrying
`suggestedInspection` (symbol ids to fetch before revising, a nested array of `{symbolId, displayString}`
objects), `suppressedDiagnostics` (downstream errors that vanish once the root cause is fixed, so don't
chase them), and `fixHint`. Fetch everything suggested and submit one revised patch; never resubmit
unchanged or fix causes one at a time.

Real call and response — an intentionally broken addition, `applyOnSuccess: false`:

```
validate_patch(baseVersions: {"sym_7a9d...": "decl:7c76e9eba9da"},
  edits: [{file: "src/DotnetToolkit.McpServer/Tools/ServerTools.cs", startLine: 15, endLine: 15,
           newText: "    public static string Ping() => ThisTypeDoesNotExist.Value;"}])
```

```json
{"detectedChanges":[
   {"symbolId":"sym_7a9d...","changeKinds":["body"],
    "oldVersion":"decl:7c76e9eba9da|body:2bac28c29969","apiImpact":"non-breaking"}],
 "ladder":{"completedLevel":"semantic_bind","requiredLevel":"project_compile","isSufficient":false,
   "reason":"Validation failed at semantic_bind.",
   "nextAction":"Fetch the suggested symbols, revise the patch, and resubmit."},
 "succeeded":false,"applied":false,
 "diagnostics":{"rootCauses":[
   {"diagnostic":"CS0103",
    "summary":"CS0103: 1 occurrence(s) — The name 'ThisTypeDoesNotExist' does not exist in the current context",
    "affectedSymbolId":"sym_7a9d...",
    "fixHint":"A name is not in scope here; check the identifier or add the missing member.",
    "suggestedInspection":[{"symbolId":"sym_7a9d...","displayString":"string ServerTools.Ping()"}]}],
   "totalRaw":1,"totalSuppressed":0}}
```

`newVersion` is `null` here because nothing was applied — it only describes reality once the patch is
actually on disk.

See `skills/dotnet-change/SKILL.md` for the full write loop.

## Self-observation

### `get_retrieval_metrics` — where the tokens actually went

Replaces: guessing. Computed from this server's own telemetry.

| Arg | Meaning |
|---|---|
| `scope` | `session` \| `global` (default). No more `task` — nothing ever read task-level metrics back through a tool, so it was retired along with the `taskId` argument on every other tool. |
| `sessionIds` | One or more session ids to merge together. Required for `scope: "session"`. Every tool call in this process already shares one ambient session id automatically (no argument needed to set it) — `sessionIds` matters only when you want to combine that with sessions from *other* (past) server processes. |
| `since` / `until` | Optional ISO date bounds (`yyyy-MM-dd` only) on `created_at`, inclusive on both ends, usable with either `scope`. |
| `groupBy` | `tool` \| `symbol` \| `level` \| `session` \| `none` (default `tool`). `session` groups by `session_id` with `firstSeen`/`lastSeen` — since there's no directory of past sessions, this plus `since`/`until` is how you discover which session ids existed in a date range, before feeding them back into `sessionIds`. |

Real call and response (trimmed):

```
get_retrieval_metrics(scope: "global", groupBy: "tool")
```

```json
{"totals":{"toolCalls":77,"tokensReturned":31450,"leaseHits":1,"tokensSavedByLeases":351,
           "refetches":0,"validationAttempts":6,"insufficientValidations":0,"failedValidations":0},
 "groups":[
   {"key":"get_symbol","calls":49,"tokensReturned":21004},
   {"key":"search_index","calls":15,"tokensReturned":5718},
   {"key":"get_references","calls":7,"tokensReturned":3133},
   {"key":"validate_patch","calls":6,"tokensReturned":1595}],
 "flags":[
   {"kind":"repeat_fetch_without_lease","symbolId":"sym_21b0...","count":6,
    "hint":"Supply knownVersion for this symbol."}]}
```

`flags` calls out exactly what to fix: a symbol fetched repeatedly without ever passing `knownVersion`
back is paying for the same content over and over. `leaseHits`/`tokensSavedByLeases` being low relative
to `toolCalls` is itself a signal to start leasing.

`validate_patch` writes to a separate raw-events table (`patch_events`, not `retrieval_events`) since it
records validation-ladder fields no read tool has (`completedLevel`, `isSufficient`, …). `totals` and the
default `tool` grouping fold its calls/tokens in alongside the read tools; `validationAttempts` above
counts the same six calls from the angle of the validation ladder rather than raw token volume. A
`validate_patch` entry appears in `groups` only when at least one such call falls in scope — it's absent,
not zero, for a scope with no patch activity.

Finding and merging past sessions — since there's no session directory, `groupBy: "session"` combined
with `since`/`until` is the discovery mechanism:

```
get_retrieval_metrics(scope: "global", since: "2026-07-07", until: "2026-07-21", groupBy: "session")
```

```json
{"totals":{...},
 "groups":[
   {"key":"ses_auto01J...","calls":214,"tokensReturned":98213,
    "firstSeen":"2026-07-19T08:03:11...","lastSeen":"2026-07-19T17:42:05..."},
   {"key":"ses_auto01H...","calls":87,"tokensReturned":31005,
    "firstSeen":"2026-07-14T09:11:02...","lastSeen":"2026-07-14T12:20:44..."}],
 "flags":[...]}
```

Feed the ids found this way back into `scope: "session"` to merge them:

```
get_retrieval_metrics(scope: "session", sessionIds: ["ses_auto01J...", "ses_auto01H..."])
```

## Server

### `ping`

Health check. `Ping()` → `"pong dotnet-toolkit/0.1.0"`. No arguments.

### `set_output_format` — change how responses are encoded

| Arg | Meaning |
|---|---|
| `format` | `json` (pretty-printed) \| `compact` (minified JSON) \| `toon` (default). |

Takes effect immediately and persists for the rest of the session (until changed again or the server
restarts). Returns a plain confirmation string, not a JSON/TOON envelope — e.g.
`set_output_format(format: "json")` → `"output format set to json"`. An unrecognized `format` is reported
back rather than silently defaulting: `"unknown format: yaml (use json|compact|toon)"`.

### `workspace_status` — is the index/workspace warm

Call this when a semantic tool reports the workspace isn't ready, or before trusting a `0` reference
count.

Real response, this repo:

```
root: /path/to/dotnet-toolkit
solution: dotnet-toolkit.slnx
index: ready 83 files, 134 types
workspace: loaded 2 projects in 2.6s
  loaded: DotnetToolkit.McpServer, DotnetToolkit.McpServer.Tests
```

A degraded workspace names the failing project — reference edges from a project MSBuild couldn't
evaluate contribute nothing, and semantic results from it are incomplete or wrong, not just thin.

### `reload_workspace` — force a re-scan

| Arg | Meaning |
|---|---|
| `scope` | `index` (re-scan file index) \| `workspace` (re-open the MSBuild solution) \| `all` (default). |

Call after a large external change the mtime-poller might not have caught yet in time — a `git
checkout`, a `git pull`, a rebase, or any `.cs` edit made outside `validate_patch`.

## Workspace readiness

`limitedBy` names what an answer could **not** draw on — never about content freshness, which is
mtime-polled before every query.

- **absent** — fully informed. The healthy case costs no tokens to say so.
- **`index_only`** — served from the syntax tier; the MSBuild workspace wasn't ready yet.
  `referenceCounts` and semantic resolution are unavailable, **not zero**.
- **`stale`** — the file this answer was served from has changed on disk since the workspace read it.
  Call `reload_workspace`, then re-read — line spans will have moved. `validate_patch` refuses a patch
  built on a `stale` response outright (`stale_workspace`), since applying it would revert whatever else
  changed in that file.
- **`degraded`** — the workspace loaded but one or more projects failed. Results may be silently
  **wrong**, not just thin. Call `workspace_status` for diagnostics, fix the build, then
  `reload_workspace`.
