---
name: dotnet-read
description: Use when reading or navigating C#/.NET code — find or search for a class, interface, method, property or field; locate a symbol's declaration and read it; look up who calls something or what implements it; trace a call tree or a path between two functions; see what is callable at a line; inspect a base chain or project references; find a dependency cycle; or see what a commit or branch changed. Replaces Grep, Glob, find, cat and whole-file Read on .cs files with the plugin's Roslyn-backed MCP tools plus a ranged Read, and names the one right tool and call shape for each question.
---

# Reading and navigating C# with dotnet-toolkit

Every question below is answered by a Roslyn-backed MCP tool that reads the compiler's own model.
`Grep`, `Glob`, `find`, `cat`, `sed` and whole-file `Read` on a `.cs` file are wrong here for
**finding** things, not just slower — they cannot see interface, virtual or delegate dispatch, count
comment and string matches as real hits, silently under-report when output is truncated, and hand
back one fragment of a partial class with no signal that the rest exists. **Reading the text once it
is located is a different matter**: no tool here returns source. `get_symbol` answers *where*
(`declarationSites`, doc comment included), and a **ranged `Read`** over that range fetches the text —
that is the intended pair, and a ranged read draws no hook note. A `PreToolUse` hook adds an advisory
when `Read` or a shell read takes a *whole* compiled `.cs` file, but does not block it —
`Grep`/`Glob` are watched by no hook at all. The router below, not hook enforcement, is what keeps
you on the right tool.

**Tool names carry a host-dependent prefix.** In Claude Code they are
`mcp__plugin_dotnet-toolkit_dotnet__<tool>`, because the plugin qualifies the server it ships. Codex
registers the same server under the bare name `dotnet` and namespaces by that instead, so the prefix
differs there. Every fully-qualified name below is written in the Claude Code form.

**Resolve the prefix once per session rather than assuming it.** Run
`ToolSearch("select:mcp__plugin_dotnet-toolkit_dotnet__workspace_status")`; if that returns nothing,
search for the bare tool name (`ToolSearch("workspace_status")`) and read the prefix off whatever
comes back, then apply that same prefix to every name in this skill. One probe settles it for the
whole session. Do not guess a prefix and do not fall back to `Grep`/`Read` because a qualified name
missed — the tools are there under a different label, and the fallback is the failure this skill
exists to prevent.

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
   literal and the read fails. `Read` the joined path — the read guards only ever note a `.cs` file.

Re-call it whenever a response comes back `stale` or `degraded` (see *Workspace readiness* below).

## Step 1 — load the tool by its exact name

Schemas are deferred: a tool must pass through `ToolSearch` once per session before it can be called.

**Pick the tool from the tables below, then load it by exact name:**

```
ToolSearch("select:mcp__plugin_dotnet-toolkit_dotnet__get_references")
```

**Never describe the task to `ToolSearch` and let it rank candidates.** The mechanism is lexical
(regex or BM25 over tool names, descriptions and argument descriptions), not semantic. Measured here:
11 of the then 18 tools failed to appear in the top 5 for a natural-phrasing query — `"who calls this method"`
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
- Does anything actually use this — is it dead code?
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

**A hit is one symbol, not one declaration.** A type declared `partial` across several files collapses
to a single ranked row, whose `file` names only the fragment that ranked. `parts: N` (with a
`partsHint`) appears when N > 1 and says so; absent means one file. **Never read a ranked page as an
enumeration of declaration sites** — `get_symbol`'s `declarationSites` is the call that enumerates
them, and on a 13-file partial a benchmarked MCP route read the hits instead and answered 5.

A hit's `lines` (`@from-to`) is the **signature span only** — it excludes a leading `///` doc comment,
so it is the line to quote for "where is this", not the range to `Read` for the whole declaration.
`get_symbol`'s `declarationSites` is the range to read.

**Names only.** The index holds identifiers and doc summaries, not bodies: a string literal, a log
message, a comment or a local variable is not findable here. That one question is a `Grep`, and the
only one on C# where it is the right tool.

**`include` picks each hit's columns**, comma-separated, and **replaces** the default set exactly as
`get_symbol`'s `include` does. Default: `shape,refs,modifiers`. Also available: `summary`
(a `hasSummary` bool) and `summary:full` (the text, capped at 160 chars). Commas, never hyphens.

All three columns are terse codes, each with its legend stated once per response:

| Column | Reads | Letters |
|---|---|---|
| `shape` | what fetching it costs | `P`arams `M`embers `N`ested `L`ines `O`utline `D`oclines `C`ommentlines `A`ttributes |
| `refs` | what already uses it | `R`callers `E`callees `I`mplementations `V`overrides `T`ests |
| `modifiers` | what it is | lowercase: `p`=public `i`=internal `t`=protected `x`=private `s`=static `a`=abstract `v`=virtual `o`=override `y`=async … |

Uppercase-with-digits is `shape`/`refs`; lowercase-without is `modifiers`, so the columns never blur
together even though both reuse letters.

**`refs` answers "is this used anywhere" without a second call**, in one batched lookup for the whole
page. Read its zeroes and its silences precisely:

- **A digit is a measured fact.** `R0` means *nothing calls this* — the dead-code answer the column
  exists to give. A named type gets `I` instead of `R`, because call edges bind to members.
- **A missing letter** means that fact cannot apply to this symbol's kind, or is zero and not worth
  three bytes. Only `R` (on a member) and `I` (on a type) are emitted at zero.
- **A missing whole column is "not measured", never "zero".** The project's edges were never indexed
  — typically because it failed to load in MSBuild, which once hid a method that had 5 callers.
  `refsHint` says so when it happens, and reading that silence as "unused" is how live code gets
  deleted.
- **`R` counts calls written against the symbol itself.** One made through an interface is recorded
  against the *interface* member, so a method reached only by dispatch can legitimately read `R0`.
  When dispatch is the question, ask `get_references` or `get_symbol`'s `referenceCounts` — both
  expand to the interface members a symbol implements.

**Read `shape` before deciding the next call.** A type with a large `M` wants
`get_symbol(include: "members")`, never a whole-type `Read`. A member with a large `L` wants
`get_symbol(include: "bodyOutline")` to map it, then a `Read` of the one region. Anything small wants
`get_symbol` for `declarationSites` and one `Read` of that range.

Manual: `<pluginRoot>/docs/tools/search_index.md`

### `get_symbol` — locate a symbol you can name

Answers to:

- Where exactly is this symbol declared — every file and line range, partials included?
- What XML docs, attributes, members, interfaces and base type does it have?
- How many callers, tests, implementations and overrides does it have?
- What is its control-flow structure, without reading the whole body?
- Is the name I used ambiguous, or misspelled?
- Is expanding to its references worth it?

**It returns no source text.** `declarationSites` carries `file`, `startLine` (where a leading `///`
doc comment begins), `signatureLine` (where the declaration itself begins; present only when it
differs) and `endLine` (the closing brace), one entry per part of a partial. **The text is one
ranged read away:**

```
Read(file, offset: startLine, limit: endLine - startLine + 1)
```

That pair — `get_symbol` to locate, `Read` of that range to fetch — is the whole reading path. It
costs less than a whole-file `Read` on anything but a tiny file, misses no part of a partial, and the
ranged read draws no hook note. Do not `Read` a whole file to find a symbol you could have named.

**Report a location from `signatureLine`, not `startLine`.** `startLine` is the read offset and takes
in the doc comment; `signatureLine` is where the declaration begins, and matches what `search_index`
reports. Answering "declared at line 3" when the `class` keyword is on line 7 is the mistake it
exists to prevent.

**`include` is a plain comma list of component names** that replaces the default (`xmlDoc,
referenceCounts`):

```
include: "members"                      a type's surface — navigate by member list, not by reading the type
include: "bodyOutline"                  landmarks in a long member, then Read one region
include: "members,baseType,interfaces"  a type's shape from outside
include: "mechanicalFacts"              does it throw / await / lock / write, without reading it
```

`members` is capped at 40, with `totalMembers`/`truncated` past the cap.

Manual: `<pluginRoot>/docs/tools/get_symbol.md`

### `get_references` — who calls it, and from where

Answers to:

- Who calls this method, and at which file and line?
- What uses this type — as a field, parameter, return type, or construction site?
- What implements this interface, or overrides this virtual?
- How many text-only matches would a grep have given me falsely?
- Are any of these use sites tests?
- Are there more references than came back, and how do I page to them?
- Is a 0-caller result actually safe to treat as dead code?

`direction` is `callers` (default) | `implementations` | `overrides`. Each item carries a
`symbolId`, a `displayString`, and `sites` — `{file, line, snippet}`, one row per file+line. On a
named **type** there are no call sites, so `callers` returns the members that *reference* it.

**`totalItems` counts referencing symbols; `totalSites` counts the sites themselves** and is present
only when the two differ. Asked "how many call sites", report `totalSites` — hand-tallying the rows
across items is how a benchmarked route delivered 18 of the 19 sites its own response contained.

**A 0-caller result on a known reflection entry point is not evidence it is unused.** `entryPointHint`
(present only in that exact shape) says so directly — a `[Fact]`/`[Theory]`/`[Test]`/`[TestMethod]`
method, an `[McpServerTool]`/`[McpServerPrompt]`/`[McpServerResource]` handler, an ASP.NET routing
attribute (`[HttpGet]`, `[Route]`, …), `[JsonConverter]`, `[ModuleInitializer]`, or a static `Main` is
invoked by its own framework's reflection scan or routing table, which leaves no call-site edge for
this or any static reference search to find. Two separate blind benchmarks of this tool caught this
exact shape producing a confidently wrong "safe to delete" verdict — first on a `[Fact]` method, later
on an `[McpServerTool]` one on an unrelated repo — before the hint generalized past tests; don't
re-derive that caveat from memory when the field already states it.

**Do not read a large `dispatchKind: virtual` count as a large real fan-in.** Roslyn cascades: a call
written against a base or interface declaration is reported as a caller of everything that overrides
or implements it. Those sites are marked `indirect: true`, and the envelope splits `directItems` from
`indirectItems` whenever any are present. `indirectItems` means "could land here", not "does" — check
`directItems` first.

**Check `targetDisplayString` when a count surprises you.** A bare-name or suffix `symbol` handle can
bind to something you never meant, and the wrong answer looks exactly as authoritative as the right
one. That field names what actually resolved; `targetSymbolId` is a hash and confirms nothing on
sight.

An item's `symbolId` is a fetch target; the text at a site is a `Read` around `sites[].line`, or a
`get_symbol` on the item for its whole declaration span — and only for the sites you will change.

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

A derived entry carries **`isAbstract`/`isSealed` when true**, so *"which of these are concrete"* is
answered from the same call — the derived list mixes abstract intermediates in with concrete leaves,
and reporting all of them as implementers is a measured wrong answer, not a rounding error.

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

### `get_retrieval_metrics` — where the tokens went

Answers to:

- How many tokens has this session spent on retrieval?
- Which tool, symbol or task cost the most?
- What did one specific call cost? (snapshot `groupBy: "tool"` either side and subtract)

**It reports this server process and nothing else**, always — the raw telemetry is cleared when the
server starts and again when it stops, and there is no argument that reads another session. A number
here is this session's, never a month of accumulated history.

Pass a `taskId` on the calls you want to isolate — it is the only thing separating concurrent callers,
since every agent talking to this server process shares one ambient session id.

Manual: `<pluginRoot>/docs/tools/get_retrieval_metrics.md`

### `workspace_status`, `reload_workspace`, `ping`, `set_output_format`

Answers to:

- Is the workspace ready, is indexing done, where is `pluginRoot`? → `workspace_status`
- Which projects failed to load, and why? → `workspace_status`
- **A tool just failed, or results look frozen at an older revision?** → `workspace_status`. It prints
  `knowledgeCache:` when the SQLite cache is damaged (symbol search and reference counts are then
  disabled for the session, and the cache is rebuilt on the next server start), and `msbuild:` when no
  installed SDK satisfies the repo's `global.json` — the state that otherwise reaches you as an
  unresolved `Microsoft.Build.Framework`. Both lines are absent when nothing is wrong.
- I pulled/checked out/rebased — how do I refresh? → `reload_workspace(scope: "all")`. An
  `Edit`/`Write` needs nothing: the `refresh-cs-files` hook re-indexes what was written, and the
  `index:` line says `symbols refreshed Ns ago` and how many files are still queued
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
| `search_index`, then `get_references` (or `get_symbol`) only to learn whether anything uses it | Nothing — the `refs` column is already there by default. `R0` is the dead-code answer, `I`/`V` the dispatch one. Measured: that route took 3 calls where a single `grep` took 1. The one case still worth a second call is a symbol reached *only* through an interface, which reads `R0` — `get_symbol(include: "referenceCounts")` is dispatch-aware |
| `search_index` for a symbol you found by describing it, then `get_symbol` just to read its `<summary>` and location | `search_index(include: "shape,refs,modifiers,summary:full")` — the location was already in the search hit, and the doc comes back with it. Measured: 2 calls where 1 answers |
| `get_symbol` on a hit just to learn whether it is public, static, or overridable | The `modifiers` column, already on the hit — it comes from the index the search already read, so it costs nothing extra |
| Whole-file `Read` of a `.cs` file to see one method | `get_symbol` for `declarationSites`, then `Read(file, offset: startLine, limit: endLine - startLine + 1)` — the same text, none of the rest of the file |
| `get_symbol(include: "all")` to "see everything" about a member | The default include, or `bodyOutline` for a long body (measured on a 117-line method: `all` 2,133 tokens vs **192**); `all` adds `members`/`mechanicalFacts` costs a navigation question never needs |
| `search_index("fee")`, `search_index("ledger")`, `search_index("TryBuy")` … one call per term | `search_index("fee ledger TryBuy TrySell")` — one round trip, and cross-term ranking you otherwise lose |
| `search_index(query: "class")`, `query: "partial class"`, `query: "nested"` to enumerate a structural shape | `kinds`/`modifiers` for "is a class"/"is partial"; `shape`'s `N` count for "is nested" — `query` still needs a real identifier or domain term, from the README/CLAUDE.md if you don't have one yet |
| Re-fetching a symbol with `get_symbol` that this session already fetched and that hasn't changed | Reuse the held `declarationSites`; after an edit of your own the lines have moved by exactly your edit, and a `Read` anchored on text you know is cheaper than a locate |
| `get_references` for a caller list you only need as names | `get_call_hierarchy(maxDepth: 1)` — measured ~1/3 the tokens at 8 callers, ~1/6 at 25 (395 vs 2,255), ~1/8 at 105: the gap widens with fan-in because `get_references` pays per *site* and the hierarchy pays per *caller*. Reach for `get_references` when you need the `{file, line, snippet}` sites, and pay for it deliberately |
| `get_references` for an open-ended multi-level tree on a high-fan-in symbol | `get_call_hierarchy(maxDepth: 1)` — at 105 callers it measured ~1/8 the tokens |
| Repeated `get_references` walking outward to see whether X reaches Y | `get_call_slice(from: "X", to: "Y")` |
| Chaining `get_references` by hand three levels up | `get_call_hierarchy` |
| `get_call_hierarchy` with the full tree just to size a change | `get_call_hierarchy(includeTree: false)` — `blastRadius` alone, and it is byte-identical to the one the full tree carries (verified: same `totalUniqueNodes`, same `perDepth`). Measured at `maxDepth: 3` on an 11-caller root: **46 vs 1,020 tokens, 22×**; at `maxDepth: 1` on a 147-caller root, 44 vs 397. The tree is the expensive part and you were not going to read it |
| Guessing a base chain from `get_symbol`'s one-hop `containingType` | `get_type_hierarchy` |
| Grepping for a helper to find out whether one already exists | `get_scope` at the line you are standing on — it includes inherited and extension methods |
| Batch `get_symbol(symbols: [...])` used reflexively as "obviously cheaper" at any size | What decides it is whether `shared` can **hoist**, not n alone. A *homogeneous* batch (same `kind`, one `include`) hoists those fields out of every entry and is a wash-to-slight-win from about n=5 — measured 1,291 tokens against ~1,310 for the five separate fetches, so take it for the 4 saved calls. A *mixed-kind* batch hoists almost nothing and the per-entry `results[i]` nesting costs more than it saves until roughly n=8–10 |
| `search_index` (at any `limit`) to enumerate the files a partial type is declared in, reading the returned hits as the list | `get_symbol` — its `declarationSites` returns every fragment, and is the only call that does. A partial type is **one** hit; `parts: N` on it says how many files, never which. Measured on a 13-file partial: two ranked searches reported 5 files and rated themselves "fairly sure", while one `get_symbol` had all 13 |
| Hand-tallying `sites` rows across `get_references` items to answer "how many call sites" | `totalSites` — the tool's own count over the whole result, present whenever it differs from `totalItems`. The hand tally lost one site of 19 on a response that carried all 19 |
| `get_symbol(include: "members")` on a very large or many-fragment partial type | Check the hit's `shape` first: `members` is **capped at 40** — a `M245` type reports the first 40 plus `totalMembers: 245, truncated: true`, not all 245 (measured 11,367 tokens for the old uncapped response on a 13-file partial). Past the cap, reach for `search_index(pathPrefix: "<the one fragment file>", kinds: "method property field")` |
| `search_index(implements: "IFoo")` returns zero when you expected hits | Cheap and correct once `IFoo` resolves — direct implementers only, same edges `search_index`'s `I` column counts. Check `implementsHint` first: **present** means the name never resolved to an indexed interface (a spelling problem, not a filter problem); **absent** means a real zero — cross-check with `get_type_hierarchy` or `get_references(direction:"implementations")`, which count transitively and so can still show something further down the chain |
| Reading a whole long member to see one region | `get_symbol(include: "bodyOutline")` to map it, then `Read` over the one landmark's `startLine`–`endLine` |
| Opening every `.csproj` to trace references | `get_project_graph` |
| Reading `git diff` and inferring what changed | `get_semantic_diff` |
| Guessing why code looks deliberate | `git log -p` / `git blame` on the file — the toolkit keeps no change log; the repository's history is the record |

## Workspace readiness — `limitedBy`

`limitedBy` names what the answer could **not** draw on. It applies to every retrieval tool.

- **absent** — fully informed. Silence is the healthy case.
- **`index_only`** — the syntax tier answered. Reference counts and semantic resolution are
  unavailable, **not zero**.
- **`stale`** — the file changed on disk since the workspace read it. The next sweep (within a
  couple of seconds) clears it by itself; re-call after that, since line spans will have moved. Never
  anchor an edit on a `stale` response's lines.
- **`degraded`** — projects failed to load. Results may be silently **wrong**, not merely thin. Call
  `workspace_status`, fix the build, then `reload_workspace`. Never report a finding from a degraded
  workspace without saying so.

`get_references` needs live semantics and returns `error: "workspace_loading"` until ready. After a
large git operation, call `reload_workspace`; after an ordinary edit, nothing — the refresh hook and
the sweep keep the index current.

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
  back `symbolId`s, use sites and blast radius. Skip it when the symbol is already known.
- **About to change a `.cs` file** → invoke `dotnet-write`. It owns the locate-read-edit-build loop,
  the standards step, and how the index follows your edits.
- **Judging code quality** → invoke `dotnet-review`. Never review C# inline.

Non-C# files (`.csproj`, `.json`, `.md`, `.cmd`) are ordinary `Read`/`Grep` territory — this skill
does not apply to them.
