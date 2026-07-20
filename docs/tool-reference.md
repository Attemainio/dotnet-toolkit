# dotnet-toolkit tool reference

The complete per-tool catalog: what each MCP tool does, its arguments, a real call against this
plugin's own repo with its real response, and what it replaces. This is the doc `dotnet-toolkit-init`
points a consuming repo's `CLAUDE.md` at, and the doc to update whenever a tool's name, arguments,
defaults or response shape changes (see CLAUDE.md's "Changing the tool surface" section — this file is
one of the surfaces that has to move with the code).

Tool names below are prefixed `mcp__plugin_dotnet-toolkit_dotnet__` when called. Every tool accepts
optional `sessionId`/`taskId` for telemetry grouping (omitted below for brevity) — see
`skills/dotnet-code-query/SKILL.md` for the convention.

Responses are deliberately terse: a field that is absent carries no information and costs no tokens.
`limitedBy` appears only when something limited the answer (`index_only`, `stale`, `degraded`) — see
"Workspace readiness" at the end of this file. By default every response is rendered as **TOON**
(Token-Oriented Object Notation), a compact JSON-equivalent text format — not JSON text. Set
`defaultFormat` in `.claude/dotnet-toolkit/config.json` to `"compact"` for minified JSON or `"json"` for
pretty-printed JSON if something in your workflow needs to parse responses as JSON directly; the field
names and shapes documented below are identical in all three, only the wire encoding changes (see
`Contracts/Contract.cs`'s 3.9 entry).

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
| `xmlDoc` | The `<summary>` only, XML-stripped to plain text |
| `mechanicalFacts` | Server-computed structural facts as opaque JSON; `null` if the body changed since computed |
| `referenceCounts` | `{implementations, overrides}` always; adds `{callers, tests}` for a member (never for a type) |
| `recentLog` | Recent dev-log entries touching this symbol, each flagged `current:true/false` against the live body |
| `members` | For a type only: `[{symbolId, displayString, kind, contentVersion}]` per member |

The skeleton — `kind`, `displayString`, `accessibility`, `containingType`, `declarationSites` (`file`,
`startLine`, `endLine`) — is unconditional: every call gets it regardless of `include`, and those line
spans are exactly what a `validate_patch` edit takes.

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

The response becomes `{"results": {"columns": [...], "rows": [[...], ...]}}` — a
`CompactTable` like `search_index`'s `items`, with fixed columns `symbolId, contentVersion, limitedBy,
error` plus whatever fields this call's symbols' own content happen to share (call-dependent, always
reported in `columns` rather than something to memorize). A row whose symbol did not resolve has
`error` set (`symbol_not_found`, `ambiguous_symbol`) and null `symbolId`/`contentVersion` — one bad
lookup does not fail the batch. `knownVersion`/`refetch` do not apply to a batch: every row carries full
content.

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
 "items":{"columns":["symbolId","contentVersion","displayString","sites","dispatchKind","rest"],
   "rows":[
     ["sym_0e0e...","decl:66cc...|body:badd...",
      "int DevlogMigration.Run(DevlogStore devlog, FeatureLogStore log, ILogger logger)",
      [{"file":"src/DotnetToolkit.McpServer/Devlog/DevlogMigration.cs","line":29,
        "snippet":"log.Append(new FeatureLogStore.LogEntry("}],
      "direct", null],
     ["sym_2b15...","decl:8c14...|body:785d...",
      "void PatchTools.AppendLog(FeatureLogStore featureLog, ...)",
      [{"file":"src/DotnetToolkit.McpServer/Tools/PatchTools.cs","line":190,
        "snippet":"featureLog.Append(new FeatureLogStore.LogEntry("}],
      "direct", null]]},
 "totalItems":2,"excludedTextMatches":1}
```

`items` is a **table**: read `items.rows[i][items.columns.indexOf("displayString")]`, not
`items[i].displayString`. `columns` is whatever this call's items actually share — `symbolId,
contentVersion, displayString, sites, dispatchKind` land there on nearly every call; `isTest` (emitted
only when true) and `content` (with `includeBodies:true`) are the fields most likely to end up in the
trailing `rest` column instead, since neither is present on every item. `excludedTextMatches` is the
count of comment/string matches a grep would have wrongly included — 1 here, correctly excluded.

### `search_index` — find symbols when you don't know exact names

Replaces: `grep`/`Glob` over `.cs` files. Returns ranked symbols with ids and locations, not raw text
lines — nothing to hand-filter, no truncation to silently lose hits.

| Arg | Meaning |
|---|---|
| `query` | Free-text, OR-ed and ranked. **Put every term you want in one call**: `"fee ledger TryBuy TrySell"` returns all four in one ranked response — not four separate calls. |
| `kinds` | Optional comma filter: `class`/`type`, `interface`, `struct`, `record`, `enum`, `delegate`, `method`, `property`, `field`, `event`. |
| `limit` | Default 10, cap 50. |

Real call and response:

```
search_index(query: "validate_patch FeatureLogStore", limit: 5)
```

```json
{"items":{"columns":["symbolId","name","kind","file","line"],
 "rows":[
   ["sym_dd78...","DotnetToolkit.McpServer.Tools.PatchTools.ValidatePatch(...)","Method",
    "src/DotnetToolkit.McpServer/Tools/PatchTools.cs",29],
   ["sym_17cd...","DotnetToolkit.McpServer.Store.FeatureLogStore.LogEntry","Record",
    "src/DotnetToolkit.McpServer/Store/FeatureLogStore.cs",22],
   ["sym_fc34...","DotnetToolkit.McpServer.Store.FeatureLogStore","Type",
    "src/DotnetToolkit.McpServer/Store/FeatureLogStore.cs",10]]}}
```

`name` is directly usable as `get_symbol`'s `symbol` argument. A hit whose name maps to several
overloads has `file`/`line` as `null` rather than pointing at the wrong one — resolve it through
`get_symbol` instead, which separates overloads by parameter list.

## Relationships & flow

### `get_scope` — what is callable *here*

Replaces: guessing a helper name, or grepping for one that may not apply at this position. Grep cannot
answer this at all — an extension method shares no text with its call site.

| Arg | Meaning |
|---|---|
| `file`, `line`, `column` | Required position (column defaults to 1). |
| `receiver` | Optional variable/expression — narrows to what's callable *on it*, including applicable extension methods. |
| `filter` | `all` (default) \| `methods` \| `properties` \| `locals` \| `types`. |
| `nameContains`, `limit` | Narrow a large result. |

Real call and response (trimmed):

```
get_scope(file: "src/DotnetToolkit.McpServer/Tools/PatchTools.cs", line: 190,
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
want to know. Drop `receiver` to see what's in scope at that line generally.

### `get_call_slice` — how does X reach Y

Replaces: walking the graph with repeated `get_references` calls — one round trip per hop, and you
assemble the chain yourself.

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
{"items":{"columns":["logId","date","intent","tags"],
 "rows":[
   ["log_01KY07FZ...","2026-07-20",
    "Fix get_symbol's [Description]: the batch-mode response was documented as an array, but it's actually a CompactTable {columns,rows} like search_index/get_references",
    []],
   ["log_01KY07F8...","2026-07-20",
    "Remove unused toolCallId/patchId/validationAttemptId parameters from Error/StaleBase/BuildResponse, ...",
    []]]}}
```

`items` is a **table** like `search_index`/`get_references`, not an array of objects — read `rows[i][3]` for
`tags` (a real JSON array), not `rows[i].tags`.

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

`detectedChanges` and, on failure, `diagnostics.rootCauses` are both `CompactTable`s (`{columns, rows}`),
the same convention as `search_index`/`get_references` — read `rows[i][columns.indexOf("symbolId")]`, not
`rows[i].symbolId`. Each root cause is pre-distilled — one entry per root cause, not one per compiler
error — carrying `suggestedInspection` (symbol ids to fetch before revising, a nested array of
`{symbolId, displayString}` objects within that row, not further hoisted), `suppressedDiagnostics`
(downstream errors that vanish once the root cause is fixed, so don't chase them), and `fixHint`. Fetch
everything suggested and submit one revised patch; never resubmit unchanged or fix causes one at a time.

Real call and response — an intentionally broken addition, `applyOnSuccess: false`:

```
validate_patch(baseVersions: {"sym_7a9d...": "decl:7c76e9eba9da"},
  edits: [{file: "src/DotnetToolkit.McpServer/Tools/ServerTools.cs", startLine: 15, endLine: 15,
           newText: "    public static string Ping() => ThisTypeDoesNotExist.Value;"}])
```

```json
{"detectedChanges":{"columns":["symbolId","changeKinds","oldVersion","newVersion","apiImpact"],
 "rows":[["sym_7a9d...",["body"],"decl:7c76e9eba9da|body:2bac28c29969",null,"non-breaking"]]},
 "ladder":{"completedLevel":"semantic_bind","requiredLevel":"project_compile","isSufficient":false,
   "reason":"Validation failed at semantic_bind.",
   "nextAction":"Fetch the suggested symbols, revise the patch, and resubmit."},
 "succeeded":false,"applied":false,
 "diagnostics":{"rootCauses":{
   "columns":["diagnostic","summary","affectedSymbolId","fixHint","suggestedInspection","suppressedDiagnostics"],
   "rows":[["CS0103","CS0103: 1 occurrence(s) — The name 'ThisTypeDoesNotExist' does not exist in the current context",
            "sym_7a9d...","A name is not in scope here; check the identifier or add the missing member.",
            [{"symbolId":"sym_7a9d...","displayString":"string ServerTools.Ping()"}],0]]},
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
| `scope` | `session` \| `task` \| `global` (default). |
| `groupBy` | `tool` \| `symbol` \| `level` \| `none` (default `tool`). |

Real call and response (trimmed):

```
get_retrieval_metrics(scope: "global", groupBy: "tool")
```

```json
{"totals":{"toolCalls":71,"tokensReturned":29855,"leaseHits":1,"tokensSavedByLeases":351,
           "refetches":0,"validationAttempts":6,"insufficientValidations":0,"failedValidations":0},
 "groups":[
   {"key":"get_symbol","calls":49,"tokensReturned":21004},
   {"key":"search_index","calls":15,"tokensReturned":5718},
   {"key":"get_references","calls":7,"tokensReturned":3133}],
 "flags":[
   {"kind":"repeat_fetch_without_lease","symbolId":"sym_21b0...","count":6,
    "hint":"Supply knownVersion for this symbol."}]}
```

`flags` calls out exactly what to fix: a symbol fetched repeatedly without ever passing `knownVersion`
back is paying for the same content over and over. `leaseHits`/`tokensSavedByLeases` being low relative
to `toolCalls` is itself a signal to start leasing.

## Server

### `ping`

Health check. `Ping()` → `"pong dotnet-toolkit/0.1.0"`. No arguments.

### `workspace_status` — is the index/workspace warm

Call this when a semantic tool reports the workspace isn't ready, or before trusting a `0` reference
count.

Real response, this repo:

```
root: /mnt/c/Users/atte9/source/repos/dotnet-toolkit
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
