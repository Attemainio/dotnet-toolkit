---
name: dotnet-code-query
description: Use when exploring, searching, inspecting or analyzing C# code in a .NET repo - orienting in the codebase, finding a class/method/symbol, callers or references, interface implementations, or type signatures/APIs. Prefer these MCP tools over Read/Grep/ls for .cs files; they answer from a Roslyn semantic model and return a fraction of the tokens.
---

# Retrieving C# code without reading files

This repo has the dotnet-toolkit MCP server. For C# questions, retrieve **symbols**, not
files. The server answers from a live Roslyn semantic model, so it sees calls through
interfaces, virtual dispatch and delegates that a text search cannot.

Tool names below are prefixed `mcp__plugin_dotnet-toolkit_dotnet__`.

## Session and task ids (required on every call)

Every tool takes `sessionId` and `taskId`. The server never invents them, and they are
what make the usage metrics meaningful.

- **`sessionId`** — generate once at the start of the conversation, e.g. `ses_<8 random
  chars>`, and reuse it for **every** call thereafter.
- **`taskId`** — generate one per user-level task, e.g. `tsk_<8 random chars>`, and reuse
  it for every call belonging to that task. Start a new one when the user moves on.

Do not invent a fresh id per call; that destroys the grouping.

## Decision table

| You want | Call | Do NOT |
|---|---|---|
| Find a symbol when you don't know the exact name | `search_index` | Grep/Glob over .cs files |
| A type or member's shape, docs, location | `get_symbol` | Read the .cs file |
| A type's member list | `get_symbol` with `resolution: "outline"` | Read the file |
| Callers / usages | `get_references` (`direction: "callers"`) | Grep the name — it misses interface dispatch and returns comment hits |
| Implementations, derived types, overrides | `get_references` (`direction: "implementations"` or `"overrides"`) | Grep for `: IFoo` |
| Whether a change is safe | `validate_patch` (see the dotnet-change skill) | `dotnet build` |

Read a .cs file only when you are about to edit lines that `get_symbol` did not give you,
or for non-C# files (csproj, json, md) where Read/Grep are the right tools.

## Escalate resolution, don't over-fetch

`get_symbol` has three resolutions. Start cheap:

1. **`signature`** (default) — shape, accessibility, declaration sites, `referenceCounts`.
2. **`outline`** (types only) — the member list, each with its own id and version.
3. **`full`** — adds `source`. Request this directly **only** when you already intend to
   edit that symbol.

## Gate expansion on referenceCounts

`get_symbol` returns `referenceCounts: { callers, implementations, overrides }`. Use it to
decide whether an expansion is worth the tokens:

- **0 callers** → do not call `get_references`; there is nothing to find.
- **1–5 and you plan a signature change** → fetch them.
- **more than 5** → fetch the list without bodies first, then bodies only for the ones you
  will actually edit.

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
- **Only pass `knownVersion` when you actually still hold the content.** If you need a body
  you never fetched, just request it — leasing against a `decl`-only token returns
  `changed:false` with no body and costs you a second round trip.

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
