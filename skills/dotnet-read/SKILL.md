---
name: dotnet-read
description: Use when reading or navigating C#/.NET code — find or search for a class, interface, method, property or field; read a symbol's source; look up who calls something or what implements it; trace a call tree or a path between two functions; see what is callable at a line; inspect a base chain or project references; find a dependency cycle; see what a commit or branch changed; or find out why existing code looks the way it does. Replaces Grep, Glob, find, cat and Read on .cs files with the plugin's Roslyn-backed MCP tools, and names the one right tool and call shape for each question.
---

# Reading and navigating C# with dotnet-toolkit

Every question below is answered by a Roslyn-backed MCP tool that reads the compiler's own model.
`Grep`, `Glob`, `find`, `cat`, `sed` and bare `Read` on a `.cs` file are wrong here, not just
slower — they cannot see interface, virtual or delegate dispatch, count comment and string matches as
real hits, silently under-report when output is truncated, and hand back one fragment of a partial
class with no signal that the rest exists. `PreToolUse` hooks block them.

Tool names are prefixed `mcp__plugin_dotnet-toolkit_dotnet__`.

## Step 0 — call `workspace_status` before any read

**First MCP call of the session, always.** It is free, takes no arguments, records no telemetry, and
gives you two things nothing else can:

1. **Readiness.** Until the workspace is loaded, semantic tools answer from the syntax tier or refuse
   outright. A zero-hit from `get_references`, `get_call_hierarchy`, `get_call_slice`,
   `get_type_hierarchy` or `get_semantic_diff` against a cold workspace is *workspace state, not
   absence* — never report it as "nothing uses this".
2. **`pluginRoot`.** The plugin's install directory, and the only supported way to reach a file that
   ships with the plugin. Join it yourself:
   - a tool manual → `<pluginRoot>/docs/tools/<tool>.md`
   - a coding standard → `<pluginRoot>/standards/<name>.md`

   **Never write `${CLAUDE_PLUGIN_ROOT}` into a path.** The harness substitutes it into `.mcp.json`
   args, hook commands and skill content, but **not** into a rule or an agent definition, so it stays
   literal and the read fails. `Read` the joined path — the guards only block `.cs`.

Re-call it whenever a response comes back `stale` or `degraded` (see *Workspace readiness* below).

## Step 1 — load the tool by its exact name

Schemas are deferred: a tool must pass through `ToolSearch` once per session before it can be called.

**Pick the tool from the tables below, then load it by exact name:**

```
ToolSearch("select:mcp__plugin_dotnet-toolkit_dotnet__get_references")
```

**Never describe the task to `ToolSearch` and let it rank candidates.** The mechanism is lexical
(regex or BM25 over tool names, descriptions and argument descriptions), not semantic. Measured here:
11 of 18 tools failed to appear in the top 5 for a natural-phrasing query — `"who calls this method"`
never surfaced `get_references` at all. The tables below pick the tool; `ToolSearch` only fetches it.

**The schema is then enough for an ordinary call.** Reach for a manual only for an advanced selector,
an unfamiliar response field, or error recovery.

## The tools

### `search_index` — find symbols when you don't know the exact name

Answers to:

- Where is the class/interface/method/property/field called *X*?
- What symbols exist matching these several terms at once?
- Which types implement *IFoo*, or live under this folder?
- Which symbols are `public`, `static`, `abstract`, `async`, extension methods, `IDisposable`?
- Which symbols have (or lack) an XML `<summary>`, `<returns>`, `<remarks>`?
- How is the codebase laid out by namespace or by file?
- Roughly what will fetching this symbol cost me?
- Did any of my search terms find nothing at all?

**One call, every term.** Terms are OR-ed and ranked together:
`search_index(query: "fee ledger TryBuy TrySell")` answers for all four in one round trip. Each term
gets only a shallow floor share of `limit`, so **always read `termsWithNoHits`** — a term the result
never covered is named there, and an absent term is never evidence of an absent symbol.

**`query` matches identifier text, not structure.** `query: "class"`, `"partial class"` or `"nested"`
searches for a symbol literally named that — it is never how you ask "list every class" or "list every
partial/nested type", and returns nothing when this repo has no symbol named `class`. "Is a class" is
the `kinds` filter; "is partial/static/public/…" is `modifiers`; "is nested" has no filter at all — read
it off `shape`'s `N` count. If you don't yet know a real identifier or domain noun to search for, `Read`
this project's `README.md` (or `CLAUDE.md` if there is no `README.md`) before the first `search_index`
call — both are plain Markdown, not `.cs`, so this skill's tools don't apply to them.

A hit's `line`/`endLine` mark the **signature line only** and exclude a leading `///` doc comment.
They are a navigation aid, never an edit span.

**Read the `read` column before deciding the next call.** Each hit carries `shape` (what fetching it
costs) and, whenever the default `get_symbol` fetch is *not* the right next call, `read` — the
include to pass instead: `mem`, `out`, `code` or `all`, legend stated once per response. Absent means
the default fetch is already right, which is why the column costs nothing on an ordinary result.
Pass **`intent: "edit" | "logic" | "surface"`** to aim it at what you are about to do; your intent is
a fact the hit's shape cannot contain, and stating it beats re-deriving it per row.

Manual: `<pluginRoot>/docs/tools/search_index.md`

### `get_symbol` — read a symbol you can name

Answers to:

- What is this symbol, and where exactly is it defined?
- What is its full source, including every partial-class fragment?
- What XML docs, attributes, members, interfaces and base type does it have?
- How many callers, tests, implementations and overrides does it have?
- What is its control-flow structure, without paying for the whole body?
- Can I fetch only the lines or components I actually need?
- What must I hold before editing it safely?
- Has it changed since I last looked?
- Is the name I used ambiguous, or misspelled?
- Is expanding to its references worth it?

**This replaces `Read` on a `.cs` file.** It returns the whole symbol across partials for a fraction
of the file's tokens, plus `declarationSites` (file + `startLine`/`endLine`, *including* a leading
`///` doc comment) and a `contentVersion` — exactly what the write path needs.

`include` picks the components and replaces the default set: `members` for a type's surface,
`source:code` to read source without doc comments, `source:code@120-160` for one region of a long
member, `bodyOutline` to map a member before slicing it, `all` when about to edit.

**An unsliced `source` on a 500+ line declaration is warned about once** rather than served: the
response carries `members`/`bodyOutline` and a `guard` block naming the size and the cheaper route.
**Repeating the call verbatim gets the source** — that is the whole override, and taking it is a
correct decision when you genuinely want the whole thing. Do not work around it by fetching the
symbol in pieces you did not want; either follow the advice or repeat the call.

Manual: `<pluginRoot>/docs/tools/get_symbol.md`

### `get_references` — who calls it, and from where

Answers to:

- Who calls this method, and at which file and line?
- What uses this type — as a field, parameter, return type, or construction site?
- What implements this interface, or overrides this virtual?
- How many text-only matches would a grep have given me falsely?
- Are any of these use sites tests?
- Are there more references than came back, and how do I page to them?

`direction` is `callers` (default) | `implementations` | `overrides`. Each item carries a
`symbolId`, a `displayString`, and `sites` — `{file, line, snippet}`, one row per file+line. On a
named **type** there are no call sites, so `callers` returns the members that *reference* it.

An item's `symbolId` is a fetch target, **not an edit lease**.

Manual: `<pluginRoot>/docs/tools/get_references.md`

### `get_call_hierarchy` — the multi-level call tree, and blast radius

Answers to:

- Who *eventually* calls this, up to the entry points?
- What does this eventually call, several hops down?
- If I change this, how far does the change ripple?
- How many distinct symbols does it reach, per depth?
- Was the tree truncated, and where?
- Is there recursion in this path?

`direction: "callers"|"callees"`, `maxDepth` (default 3). `includeTree: false` returns **only**
`blastRadius` — the cheapest possible answer to "how much does changing this ripple".

Manual: `<pluginRoot>/docs/tools/get_call_hierarchy.md`

### `get_call_slice` — is there a path from X to Y

Answers to:

- Does control flow reach from this function to that one?
- What is the shortest path between them, and through what?
- If there is no path, how close does each end get?

Both endpoints must already be named. Use it instead of walking the graph with repeated
`get_references` calls.

Manual: `<pluginRoot>/docs/tools/get_call_slice.md`

### `get_scope` — what is callable at this exact line

Answers to:

- What can I call at this file/line/column?
- Which extension methods apply to this receiver?
- Does a helper for this already exist before I write one?
- What is in scope when I don't yet know the receiver's type?
- Which members are inherited rather than declared here?

Grep cannot answer this at all: an extension method shares no text with its call site. Different from
`get_symbol(include: "members")`, which is a type's static declared list with no position involved.

Manual: `<pluginRoot>/docs/tools/get_scope.md`

### `get_type_hierarchy` — the full base chain and every implementer

Answers to:

- What is this type's complete base chain, up to `object`?
- Which interfaces does it implement, directly and inherited?
- Which types derive from it or implement it?

One hop further than `get_symbol`'s single `containingType`/`baseType`. Don't guess a hierarchy from
a one-hop fetch.

Manual: `<pluginRoot>/docs/tools/get_type_hierarchy.md`

### `get_project_graph` — which project references which

Answers to:

- What does this project reference, and what references it?
- What is the solution's whole project dependency graph?

Use it instead of opening every `.csproj`.

Manual: `<pluginRoot>/docs/tools/get_project_graph.md`

### `detect_circular_dependencies` — reference loops

Answers to:

- Is there a real dependency cycle between projects?
- Which projects form it?

Manual: `<pluginRoot>/docs/tools/detect_circular_dependencies.md`

### `get_semantic_diff` — what a commit or branch actually changed

Answers to:

- Which symbols were added, removed or changed between two refs?
- Which version layers moved — declaration, body, or both?
- What is the API impact of this change?
- Was this commit formatting-only?

Use it instead of reading a textual diff and inferring. Formatting- and comment-only commits
correctly report no change.

Manual: `<pluginRoot>/docs/tools/get_semantic_diff.md`

### `search_log` — why the code looks the way it does

Answers to:

- Why was this change made, and by what reasoning?
- Was this approach already tried and rejected?
- Which symbols did a past change touch?

Search it **before** proposing a design, not after. Matching is AND over `intent` text — every term
must appear, in any order, so adding a term narrows rather than widens. An empty result means nothing
was *recorded*, never that nothing was decided; the log only covers changes applied through
`validate_patch`.

Manual: `<pluginRoot>/docs/tools/search_log.md`

### `get_retrieval_metrics` — where the tokens went

Answers to:

- How many tokens has this session spent on retrieval?
- Which tool, symbol or task cost the most?
- What did one specific call cost? (snapshot `groupBy: "tool"` either side and subtract)
- Which past sessions exist in a date range?

Pass a `taskId` on the calls you want to isolate — it is the only thing separating concurrent callers,
since every agent talking to this server process shares one ambient session id.

Manual: `<pluginRoot>/docs/tools/server.md`

### `workspace_status`, `reload_workspace`, `ping`, `set_output_format`

Answers to:

- Is the workspace ready, is indexing done, where is `pluginRoot`? → `workspace_status`
- Which projects failed to load, and why? → `workspace_status`
- I pulled/checked out/rebased — how do I refresh? → `reload_workspace(scope: "all")`
- Is the server answering at all? → `ping`
- How do I get plain JSON instead of TOON? → `set_output_format(format: "compact"|"json")`

Manual (all four): `<pluginRoot>/docs/tools/server.md`

## The cheap-route table

Each row is a route actually observed being taken, and the cheaper route that answers the identical
question. **This table is why this skill exists**: these findings used to live only inside individual
tool manuals, which are not loaded unless something already went looking — so the anti-pattern was
committed before the note that prevents it was ever read.

| Anti-pattern (route taken) | Cheap route |
|---|---|
| `search_index(pathPrefix: "<one exact .cs file>")` to browse a known file's symbols | `get_symbol(symbol: "TypeName", include: "members")` — no ranking needed, and you get signatures and docs for the same tokens |
| `search_index("fee")`, `search_index("ledger")`, `search_index("TryBuy")` … one call per term | `search_index("fee ledger TryBuy TrySell")` — one round trip, and cross-term ranking you otherwise lose |
| `search_index(query: "class")`, `query: "partial class"`, `query: "nested"` to enumerate a structural shape | `kinds`/`modifiers` for "is a class"/"is partial"; `shape`'s `N` count for "is nested" — `query` still needs a real identifier or domain term, from the README/CLAUDE.md if you don't have one yet |
| Re-fetching a symbol with `get_symbol` that this session already fetched and that hasn't changed | Reuse the held `contentVersion`/`declarationSites`; after an edit, use the applied response's `newVersion` and refreshed `declarationSites` directly |
| `get_references` for a caller list you only need as names | `get_call_hierarchy(maxDepth: 1)` — measured ~1/3 the tokens at 8 callers; reach for `get_references` when you need the `{file, line, snippet}` sites, and pay for it deliberately |
| `get_references` for an open-ended multi-level tree on a high-fan-in symbol | `get_call_hierarchy(maxDepth: 1)` — at 105 callers it measured ~1/8 the tokens |
| Repeated `get_references` walking outward to see whether X reaches Y | `get_call_slice(from: "X", to: "Y")` |
| Chaining `get_references` by hand three levels up | `get_call_hierarchy` |
| `get_call_hierarchy` with the full tree just to size a change | `get_call_hierarchy(includeTree: false)` — `blastRadius` alone |
| Guessing a base chain from `get_symbol`'s one-hop `containingType` | `get_type_hierarchy` |
| Grepping for a helper to find out whether one already exists | `get_scope` at the line you are standing on — it includes inherited and extension methods |
| Batch `get_symbol(symbols: [...])` used reflexively as "obviously cheaper" at any size | Below roughly n=8–10 it is a wash or a slight loss; the `shared` hoisting block only pays off at scale |
| Fetching a whole long member to read one region | `get_symbol(include: "bodyOutline")` to map it, then `source:code@120-160` for the region |
| `get_symbol(include: "all")` on a read-only pass | The default include, or a named component list — `all` is a write-path lease you have no use for |
| Opening every `.csproj` to trace references | `get_project_graph` |
| Reading `git diff` and inferring what changed | `get_semantic_diff` |
| Guessing why code looks deliberate | `search_log` |

## Workspace readiness — `limitedBy`

`limitedBy` names what the answer could **not** draw on. It applies to every retrieval tool.

- **absent** — fully informed. Silence is the healthy case.
- **`index_only`** — the syntax tier answered. Reference counts and semantic resolution are
  unavailable, **not zero**.
- **`stale`** — the file changed on disk since the workspace read it. `reload_workspace`, then
  re-read: line spans will have moved. Never build a patch on a `stale` response.
- **`degraded`** — projects failed to load. Results may be silently **wrong**, not merely thin. Call
  `workspace_status`, fix the build, then `reload_workspace`. Never report a finding from a degraded
  workspace without saying so.

`get_references` needs live semantics and returns `error: "workspace_loading"` until ready. After a
large git operation, call `reload_workspace`.

## Reading responses

Responses are **TOON** by default — the same field names as the JSON documented in each tool manual,
more compact. `set_output_format(format: "compact"|"json")` switches for the session.

**Switch formats the moment TOON is costing you accuracy.** TOON is the default because it is the
cheapest encoding of the identical data, not because reading it is a requirement — every field name
is the one the manual documents either way. A response you parse tentatively is the expensive
outcome, because the next call is built on a field you guessed at:

| Symptom | Call | Why this and not a retry |
|---|---|---|
| The response's structure is not unambiguous to you — nesting, array headers, or where one row ends | `set_output_format(format: "compact")` | Compact JSON is explicit about structure and still drops whitespace, so it costs far less than full `json`. One call, holds for the session |
| Compact JSON is still hard to follow | `set_output_format(format: "json")` | Full indentation. The most expensive format, and worth it against guessing |
| The shape is clear but a field's meaning is not | `Read` the tool's manual at `<pluginRoot>/docs/tools/<tool>.md` | A format switch cannot answer a semantics question — the same field comes back under the same name |

Re-issuing an identical call in the same format returns identical bytes. If a response disappointed
you, change the question, the arguments, or the format — never repeat it unchanged.

**Absent is not zero.** An absent field carries no information: an absent `tests` means "not
computed", not "no tests". A `null` is dropped rather than written as `"field":null`, so check for the
key's absence, not its value. The one deliberate exception is `search_index`'s `shape` column, whose
legend says so: an absent letter means zero or not-applicable, never "not computed".

Every tool that records telemetry takes an optional **`taskId`**. No tool takes a `sessionId`.

## When to hand off

- **The set of symbols the task touches isn't known yet** → invoke `dotnet-explore` instead of fanning
  out `search_index`/`get_references` here. It spends the wide responses in its own context and hands
  back `symbolId`s, use sites and blast radius. Skip it when the symbol is already known, or when the
  next step needs a `contentVersion` — the agent relays none.
- **About to change a `.cs` file** → invoke `dotnet-write`. It owns the fetch-to-patch loop, and the
  `include` you need on the read pass differs from the one you'd use here.
- **Judging code quality** → invoke `dotnet-review`. Never review C# inline.

Non-C# files (`.csproj`, `.json`, `.md`, `.cmd`) are ordinary `Read`/`Grep` territory — this skill
does not apply to them.
