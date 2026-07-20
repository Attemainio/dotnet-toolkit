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

## Session and task ids (optional)

Every tool accepts `sessionId` and `taskId`. They are **optional** — omit them and the
tools still work; the server auto-attributes the call. Supply them when you can, because
they are what make the usage metrics meaningful, but never let a missing id stop you from
using a tool.

- **`sessionId`** — generate once at the start of the conversation, e.g. `ses_<8 random
  chars>`, and reuse it for **every** call thereafter.
- **`taskId`** — generate one per user-level task, e.g. `tsk_<8 random chars>`, and reuse
  it for every call belonging to that task. Start a new one when the user moves on.

Do not invent a fresh id per call; that destroys the grouping.

## Decision table

| You want | Call | Do NOT |
|---|---|---|
| Find symbols when you don't know the exact names | `search_index`, **all terms in one call** | Grep/Glob over .cs files; one call per word |
| A type or member's shape, docs, location | `get_symbol` | Read the .cs file |
| A type's member list | `get_symbol` with `include: "members"` | Read the file |
| Callers / usages | `get_references` (`direction: "callers"`) | Grep the name — it misses interface dispatch and returns comment hits |
| Implementations, derived types, overrides | `get_references` (`direction: "implementations"` or `"overrides"`) | Grep for `: IFoo` |
| What is callable at a point in a file | `get_scope` | Guess, or grep for a helper that may not apply here |
| How one symbol reaches another | `get_call_slice` | Walk the graph with repeated `get_references` |
| What a commit or branch actually changed | `get_semantic_diff` | Read `git diff` and infer |
| Why past code looks the way it does | `search_log` | Guess from the code |
| Whether a change is safe | `validate_patch` (see the dotnet-change skill) | `dotnet build` |

Read a .cs file only when you are about to edit lines that `get_symbol` did not give you,
or for non-C# files (csproj, json, md) where Read/Grep are the right tools.

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
table — `columns` once, then one array per hit in that order — not one object per hit, so the
field names aren't repeated on every row:

```json
{"items":{"columns":["symbolId","name","kind","file","line"],
 "rows":[["sym_...","Sample.Lib.WidgetExtensions.SpinTwice(IWidget,int)","Method","Lib/Pipeline.cs",6]]}}
```

`file`/`line` are resolved from the syntax index at response time — swept for staleness on the
way — so they point at where the declaration is *now*, not where it was when the row was
written. **A name that maps to several declarations (overloads) omits both** (`null` in the row)
rather than pointing at the wrong one; that hit still resolves through `get_symbol`, which
separates overloads by parameter list and always returns exact spans.

Only split into separate calls when you need different `kinds` filters.

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
| `xmlDoc` | The `<summary>` only, XML-stripped to plain text (not `<remarks>`/`<param>`/etc.) |
| `mechanicalFacts` | Server-computed structural facts as opaque JSON; `null` if the body changed since computed |
| `referenceCounts` | `{implementations, overrides}` always; adds `{callers, tests}` for a member (never for a type) |
| `recentLog` | Last few dev-log entries touching this symbol, each flagged `current:true/false` against the live body |
| `members` | For a type only: `[{symbolId, displayString, kind, contentVersion}]` per member; `null` otherwise |

Examples:

- `include: "all"` minus the body — spell out the rest instead of subtracting:
  `include: "xmlDoc,mechanicalFacts,referenceCounts,recentLog"`. Use when you want facts and
  history but are not going to edit the symbol.
- `include: "members"` — a type's API surface with no bodies and none of the standard extras.
- `include: "xmlDoc"` — the leanest non-default fetch: just the skeleton plus the doc summary,
  no `referenceCounts` latency cost (it waits on the semantic model) and no history lookup.

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

The response's `results` is a table (`columns`/`rows`, same shape as `search_index`'s `items`).
Fixed columns are `symbolId, contentVersion, limitedBy, error` — that outer envelope genuinely is
uniform across every entry, unlike a symbol's own `content` (see the note on `search_index` above
about when a table is and isn't the right call). A successful row has `error: null`. A row whose
symbol did not resolve has `symbolId`/`contentVersion` null and `error` holding the short error
string (`symbol_not_found`, `ambiguous_symbol`) — one failed lookup does not fail the batch.

Between those fixed columns and `content` sit whatever fields the requested symbols' own content
turned out to share **this call**: batch three methods and `kind`, `displayString`,
`accessibility`, `declarationSites`, `referenceCounts`, and even `xmlDoc` (if every one happens to
have a doc comment) all get pulled out as their own columns, since every row has them. Mix in a
type alongside a method and `containingType` — which only the method has — stays nested in
`content` instead, because it is no longer common to every row. This set is genuinely
call-dependent, not fixed, which is exactly why it always shows up in `columns` rather than being
something to memorize: check `columns`, not assume a shape. `content` holds whatever was not
hoisted — for a symbol whose entire content got pulled into columns, that's `null`. A row with an
error hoists nothing and keeps its whole error envelope (including `detail`/`candidates`) in
`content`, since it shares no keys with a real symbol's content.

`knownVersion`/`refetch` do not apply to a batch — leasing needs one token per symbol, which a
single `knownVersion` cannot express — so every batch result carries full content regardless of
what you already hold.

## Questions symbol lookup cannot answer

Three tools answer questions that no amount of `get_symbol` will. Every example below is a
real call against this plugin's own repo, with its real response.

### `get_scope` — what is callable *here*

Members, inherited members, locals, parameters and **applicable extension methods** at a
file/line, filtered to what is actually accessible from that position. Grep cannot answer
this: an extension method shares no text with its call site.

Use it before writing a helper that may already exist, and to find out what a variable
actually offers instead of guessing at a method name.

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

The shortest call path between two symbols. Use it for "how does this value get there"
instead of walking outwards with repeated `get_references` calls, which costs a round trip
per hop and leaves you assembling the chain yourself.

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
- Escalating is safe: a request needing layers your token does not carry returns content
  rather than `changed: false`, so `signature` → `full` against a signature token gives you
  the source. You still get a wasted round trip if you lease for content you never held —
  the lease just will not silently hand you nothing.

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

Responses are JSON and deliberately terse: fields that are absent carry no information.
`limitedBy` appears only when something limited the answer, `changed` only when `false`, `truncated`
only when true. Absence of `tests` in `referenceCounts` means "not computed yet", **not**
"no tests".

`tests` is the subset of `callers` whose own declaration carries a test attribute (`[Fact]`,
`[Theory]`, `[Test]`, `[TestCase]`, `[TestMethod]`), so it can never exceed `callers`. A helper
that merely lives in a test project is not counted. `get_references` marks the same callers with
`isTest: true`, emitted only on the ones that are tests — an absent flag means "not a test".

### `get_references`' `items` is a table too

Same convention as `search_index`'s `items` and `get_symbol`'s batch `results`: `columns`/`rows`
instead of one object per item. `symbolId`, `contentVersion`, `displayString`, `sites`, and
`dispatchKind` are hoisted into their own columns whenever this call's actual items all have
them (the ordinary case). `isTest` and `content` (the inline body, only present with
`includeBodies: true`) are the fields most likely to end up in the trailing `rest` column
instead — neither is present on every caller, so a call mixing test and non-test callers keeps
`isTest` nested in `rest` rather than forcing a column that would be null half the time. Check
`columns`, don't assume a fixed shape.
