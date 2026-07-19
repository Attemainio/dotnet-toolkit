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
| A type's member list | `get_symbol` with `resolution: "outline"` | Read the file |
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

Only split into separate calls when you need different `kinds` filters.

## Escalate resolution, don't over-fetch

`get_symbol` has three resolutions. Start cheap:

1. **`signature`** (default) — shape, accessibility, declaration sites, `referenceCounts`.
2. **`outline`** (types only) — the member list, each with its own id and version.
3. **`full`** — adds `source`. Request this directly **only** when you already intend to
   edit that symbol.

### Narrowing with include/exclude

When a resolution is close but not right, adjust it instead of picking a wider one. Component
names are exactly the response fields they control: `source`, `xmlDoc`, `mechanicalFacts`,
`referenceCounts`, `recentLog`, `members`.

- `resolution: "full", exclude: "source"` — everything known about a symbol except its text.
  Use when you want facts and history but are not going to edit it.
- `resolution: "signature", include: "members"` — a type's API surface with no bodies.
- `exclude: "referenceCounts"` — the one component with a real latency cost (it waits on the
  semantic model). Drop it when you already know you are not expanding.

An excluded component is absent from the JSON, not null, so it costs nothing. A misspelled
name is an `invalid_component` error rather than being silently ignored.

**A narrowed response returns a narrowed version token**, covering only the layers it served.
That is deliberate: escalating later (`signature` → `full`) with that token returns the new
content rather than a false `changed: false`. It also means you cannot lease a wide request
against a narrow token — hold the token from a fetch of the same shape.

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

`get_symbol` returns `referenceCounts: { callers, implementations, overrides }`. Use it to
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

`search_index` and `get_symbol` answer from the syntax index immediately; while the MSBuild
workspace is still loading they return `staleness: "index_only"` (and `referenceCounts` may
be absent — do not read that as "no callers"). `get_references` needs live semantics and
returns `error: "workspace_loading"` until it is ready — wait briefly and retry, or check
`workspace_status`. After a large git operation, call `reload_workspace`.

## Reading responses

Responses are JSON and deliberately terse: fields that are absent carry no information.
`staleness` appears only when it is not `live`, `changed` only when `false`, `truncated`
only when true. Absence of `tests` in `referenceCounts` means "not computed yet", **not**
"no tests".
