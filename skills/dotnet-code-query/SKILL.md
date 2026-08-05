---
name: dotnet-code-query
description: Use when exploring, searching, inspecting or analyzing C# code in a .NET repo - orienting in the codebase, finding a class/method/symbol, callers or references, interface implementations, or type signatures/APIs. Grep and Read give WRONG ANSWERS on C# - grep cannot see interface, virtual or delegate dispatch, counts comment and string matches as hits, and silently under-reports when output is truncated. Use the dotnet-toolkit MCP tools instead; they answer from a Roslyn semantic model, are complete, and cost a fraction of the tokens.
---

# Retrieving C# code without reading files

This repo has the dotnet-toolkit MCP server. For C# questions, retrieve **symbols**, not files. The
server answers from a live Roslyn semantic model, so it sees calls through interfaces, virtual
dispatch and delegates that a text search cannot.

Tool names below are prefixed `mcp__plugin_dotnet-toolkit_dotnet__`.

**How to call each tool lives in `${CLAUDE_PLUGIN_ROOT}/docs/tools/<tool>.md`, one file per tool —
except the four server/meta tools (`workspace_status`, `reload_workspace`, `ping`,
`set_output_format`), which share `docs/tools/server.md`. Read the one you are about to use — not the
whole directory.** This skill carries only what applies
across all of them: when to reach for which tool, and the rules that hold on every call.

## Never fall back to grep

If you find yourself about to run Grep, Glob or Read against a `.cs` file, stop — that is the
mistake this skill exists to prevent. Reach for the MCP tool instead, even when it costs an extra
step to load the tool schema first. Measured on a real repo, `grep -rn` for a method name found
**3 of 5** call sites (truncation dropped two) and would have returned **58** comment/XML-doc
matches to hand-filter; `get_references` returned all 5, no false positives, fewer tokens. A wrong
caller list produces a wrong answer, not a slower one.

The only legitimate reasons to read a file directly: non-C# files (csproj, json, md), or lines you
are about to edit that `get_symbol` did not return.

## Decision table

| You want | Call | Do NOT |
|---|---|---|
| Find symbols when you don't know the exact names | `search_index`, **all terms in one call** | Grep/Glob over .cs files; one call per word |
| A type or member's shape, docs, location | `get_symbol` | Read the .cs file |
| A type's member list | `get_symbol` with `include: "members"` | Read the file |
| One region of a long member | `get_symbol` with `include: "source:code@120-160"` | `Read`/`sed` on the file |
| Who calls it (just the caller list, one hop) — **when fan-in is high** | `get_call_hierarchy` (`maxDepth: 1`) | `get_references` — at 105 callers it cost 5,266 tokens against 637, since it carries file/line/snippet/dispatchKind per site that a bare "who calls it" doesn't need. Below roughly a dozen callers the ladder **inverts** (139 vs 100 at one caller) — take `get_references` there and get the sites for free |
| Where exactly it's called — file, line, snippet per call site | `get_references` (`direction: "callers"`) | Grep the name — it misses interface dispatch and returns comment hits |
| Implementations, derived types, overrides | `get_references` (`direction: "implementations"` or `"overrides"`) | Grep for `: IFoo` |
| Where a **type** is used — a class, record or delegate appearing as a field, parameter, return or event type | `get_references` (`direction: "callers"`) on the type; a type has no call sites of its own, so this reports the members that reference it | Grep the type name — hits comments and misses using-aliases |
| What is callable at a cursor position — locals, inherited members, extension methods, not just a type's own declared list (that's `get_symbol` with `include:"members"`, no position involved) | `get_scope` | Guess, or grep for a helper that may not apply here |
| Whether a *known* symbol X reaches a *known* symbol Y, and through what path | `get_call_slice` | Walk the graph with repeated `get_references` calls and assemble the chain yourself |
| Who eventually calls/is eventually called by a symbol, several levels deep — an open-ended tree, not one known destination | `get_call_hierarchy` | Chain `get_references` by hand, one level at a time, and assemble the tree yourself |
| A type's full base chain, transitive interfaces, and every derived/implementing type | `get_type_hierarchy` | Guess from `get_symbol`'s one-hop `containingType`, or chain `get_references` |
| The solution's project reference graph | `get_project_graph` | Open every `.csproj` and read `<ProjectReference>` by hand |
| Circular project references | `detect_circular_dependencies` | Manually trace references looking for a loop |
| What a commit or branch actually changed | `get_semantic_diff` | Read `git diff` and infer |
| Why past code looks the way it does | `search_log` | Guess from the code |
| Whether a change is safe | `validate_patch` (see the dotnet-change skill) | `dotnet build` |

Read a .cs file only when you are about to edit lines that `get_symbol` did not give you, or for
non-C# files (csproj, json, md) where Read/Grep are the right tools.

`get_call_slice` needs both `from` and `to` already known — it is point-to-point pathfinding, not an
open-ended walk. For "who calls this, and who calls those, up to the entry points" (Visual Studio's
*View Call Hierarchy*), use `get_call_hierarchy`.

`.claude/rules/index.md` carries the router table, workspace readiness and response conventions, and
each `docs/tools/<tool>.md` ends with a **Next steps** section naming what to call with what it just
returned.

## One call, not several

`search_index` OR-es its terms and ranks the results, so one call answers for many names:

```
search_index(query: "fee ledger TryBuy TrySell")     ← one call, all four
search_index(query: "fee"); search_index(query: "ledger"); ...   ← four round trips for one answer
```

The win is **round trips, not always tokens**. Each term gets a floor share of `limit`
(`limit / terms`) before the globally ranked union spends the rest, so a term with far rarer
name-matches than its neighbours still reaches the response — but that floor is shallow (four terms at
`limit: 10` is two deep each), so it guarantees presence, not coverage. Any term the hits never covered
comes back under `termsWithNoHits` — raise `limit` (cap 50) or re-ask for that term alone.
**Never read an absent term as an absent symbol.** The field also appears on a result that came back
**empty**: every term listed means the terms missed, none listed means a filter removed what they found.

`get_symbol` takes `symbols: [...]` to fetch a list with one `include`. Batch by default; split only
when you genuinely need different filters per call. The batch's win is **round trips** — on tokens it
is roughly a wash with the same fetches made singly, since what every entry shares (`components`,
`origin`, a common `containingType`) is lifted into one `shared` block only when that actually renders
smaller. Details in `search_index.md` / `get_symbol.md`.

## Let the hit tell you how to fetch it

Every `search_index` hit — and every `get_symbol` `members` row — carries a `shape` describing what the
symbol is and what fetching it costs, with the legend stated once per response: `P` params, `M` members,
`N` nested types, `L` lines, `O` body-outline landmarks, `D` doc lines, `C` comment lines,
`A` attributes.

- big `L` + big `O` → `include: "bodyOutline"` to map it, then `source:code@from-to` for the one part
  you want. Big `L` + small `O` is one long linear block: fetch it.
- `M…` → `include: "members"`; each row states its own `line` and `shape`, so the next hop is one call.
  A row's `contentVersion` is narrowed to `decl` — enough to lease a signature or doc edit, never a body
  one, since a row never showed you a body.
- a big `D…` → `include: "source:code"` skips the doc the default fetch would carry.
- a big `C…` → `include: "source:code-comments"` when inspecting behavior, not rationale.
- `A…` → `include: "attributes"` reads them without a `source` fetch.
- small `L` and nothing else → `get_symbol(symbol: id)` is already right.
- About to **edit** it → `include: "all"` whatever the shape says, for the body-carrying
  `contentVersion`. The shape is about reading cost; it never overrides the write path.

**Nothing is threshold-gated.** A letter is absent only when its count is zero or the fact cannot apply
to that kind — a method has no `M`, a field has no `P`, an absent `O` means no body at all. On a type,
`C` **and `D`** both total its members' counts as well as its own.

## Gate expansion on referenceCounts

`get_symbol` returns `referenceCounts: { callers, tests, implementations, overrides }`. Use it to
decide whether an expansion is worth the tokens:

- **0 callers** → usually nothing to find; skip `get_references`. **But not if the symbol can be
  invoked without being named** — see below.
- **1–5 and you plan a signature change** → fetch them.
- **more than 5** → fetch the list without bodies first, then bodies only for the ones you will
  actually edit.

`callers` counts **static call sites in the loaded solution**. Anything a framework invokes by
reflection is invisible to it. In this plugin's own code `HistoryTools.SearchLog` reports 0 callers
and `ContextTools.GetSymbol` reports 3, purely because tests call one by name and not the other;
both are live MCP tools reached the same way. Treat 0 as "no information" rather than "unused" when
the symbol is an entry point, has a registration attribute, is a DI-registered implementation, a
serialization target, or a test/event handler. **Never conclude "dead code" from a 0 alone.**

A count is **omitted entirely** when it could not be measured. Absent is not 0: absent means
unknown. `callers`/`tests` are also omitted for named types, where call edges are recorded against
members — and `implementations`/`overrides` are omitted wherever the symbol's kind makes them
structurally impossible (an enum or static class has no implementers, a non-virtual member no
overriders), which is a *known* zero rather than an unmeasured one. When nothing is left to say, the
whole `referenceCounts` block is absent.

Before writing a helper that plausibly already exists, check with `search_index` first — one cheap
call beats a duplicate implementation.

## Addressing a symbol

`get_symbol` and `get_references` accept any of:

- a fully-qualified name — `PandaAI.Core.Training.TrainingService.StartTrainingAsync`
- a unique suffix — `TrainingService.StartTrainingAsync`, or just `StartTrainingAsync`
- a parameter list to pick an overload — `TrainingService.StartTrainingAsync(TrainingRequest)`
- **a `sym_…` id returned by any previous response** — search hits, reference items and
  `suggestedInspection` entries all carry one, and passing it back is unambiguous

Ambiguity is never guessed: you get `error: "ambiguous_symbol"` plus a candidate list. The prefix every
candidate shares — namespace, and usually the containing type — is hoisted out into `sharedPrefix`, so a
row's `displayString` is only the part that actually differs; prepend `sharedPrefix` to get a name you
can pass back (or just pass the row's `symbolId`).

## Version tokens

Every content response carries a `contentVersion` like `decl:a1b2…|body:84c3…` — a hash of the
symbol's declaration and (when fetched) body layers, for your own "has this changed since I last
looked" comparison. Same `decl` with a different `body` means the API is unchanged and only the
implementation moved.

**The token is narrowed to the layers the call actually served**, so two tokens for the same symbol
from different `include`s are not directly comparable — and the default `standard` fetch carries
`decl` (+`refs`) and no `body`. That matters the moment retrieval turns into an edit: `validate_patch`
takes these tokens as `baseVersions`, and rejects a **body**-changing patch built on a token that
never held the body layer (`error: "unleased_body"`). Fetch with `include: "all"` — or any include
serving `source`/`bodyOutline`/`mechanicalFacts` — when you are about to rewrite a body, which is
also when you wanted the text anyway. Full write loop: the `dotnet-change` skill.

## Workspace readiness and response conventions

`limitedBy` names what the answer could **not** draw on: **absent** = fully informed;
**`index_only`** = syntax tier only, reference counts unavailable **not zero**; **`stale`** = the
file changed on disk, call `reload_workspace` and re-read before patching; **`degraded`** = projects
failed to load, results may be silently **wrong**, call `workspace_status` and fix the build. Never
report findings from a degraded workspace without saying so.

Responses are TOON by default and deliberately terse — an absent field carries no information.
**Absent is not zero.** Full detail on both in the always-loaded `.claude/rules/index.md`.

## Attribution

No tool takes a `sessionId`; every call in a server process shares one ambient session id
automatically. Pass an optional `taskId` on any recording tool only when you are **measuring your
own token cost or working alongside other agents on the same server** — parallel agents share the
ambient id and cannot otherwise be told apart. It is instrumentation, never a precondition for
retrieval. Recipe in `docs/tools/get_retrieval_metrics.md`.
