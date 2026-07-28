# dotnet-toolkit self-evaluation — 2026-07-28

Specimen: `/mnt/c/Users/atte9/source/repos/dotnet-toolkit` · `dotnet-toolkit.slnx` · 2 projects · 99 files, 164 types · workspace loaded in 2.8s, no diagnostics
Census:   partial ✓ (generator-only) · nested ✓ · overloads ✓ · generics ✓ · long members ✓ · records ✓ · enums ✓ · multi-project ✓ · **interfaces ✗** · delegates ✗
Task ids: `eval_inst` (instrument), `eval_a` (orientation), `eval_census` (census), `eval_b`/`eval_b2` (discovery), `eval_c` (retrieval), `eval_d` (relations), `eval_e` (history), `eval_f` (write path), `eval_g`/`eval_g2` (formats) — all suffixed `_20260728`
Run cost: 53 attributed tool calls, 17,742 tokens returned (plus ~30 unrecorded `get_retrieval_metrics`/`set_output_format`/`workspace_status` control calls)

Instrument verified: two identical `search_index` calls returned identical 79-token deltas; the `taskIds` filter isolated 2 calls against 959 unfiltered.

## Findings

### [bug] `tokensReturned` is measured before format rendering, so every output format reports the same cost

The tool that exists to prove `toon` is cheaper cannot see the difference between `toon` and pretty-printed JSON.

```
Call:      identical get_symbol(symbol:"sym_3d4704415b257e89", taskId:"eval_g2_20260728")
           run three times, once under each set_output_format value
Observed:  toon    → 135 tokens   (rendered payload 530 chars)
           compact → 135 tokens   (rendered payload 539 chars)
           json    → 135 tokens   (rendered payload 680 chars, +28%)
Expected:  three different numbers tracking the bytes actually returned to the caller;
           json should measure ~28% above toon on this payload
Condition: none — reproduces on any symbol
Fix in:    Tools/ToolTelemetry.cs (the single place a response becomes a RetrievalEvent) —
           the token count is being taken from the pre-render object model, not from the
           string Formats.Render produced
```

This is the highest-priority finding because every other number in this report, and every number
`get_retrieval_metrics` has ever produced, is format-blind. The `defaultFormat: toon` decision recorded in
`Contracts/Contract.cs` §3.9 has never been measurable from the server's own telemetry. Measured by
character count, `toon` *is* the right default here (530 vs 539 vs 680 chars) — but that is an eyeball
result, not something the instrument can confirm.

### [bug] Batch `get_symbol(symbols: [...])` records roughly 1/n of its real token cost

```
Call:      get_symbol(symbols:["sym_d93197f92bed1aae","sym_3adb67fcc2f2c5f0","sym_3d4704415b257e89"])
Observed:  152 tokens recorded
           The same three symbols fetched as three single get_symbol calls, same include: 454 tokens
           152 ≈ 454 / 3 (151.3)
Expected:  the batch to record at least as much as the three singles — its response contains the same
           three payloads plus a results[3] wrapper and an extra indent level
Condition: any symbols[] batch with n > 1
Fix in:    Tools/ToolTelemetry.cs / ContextTools.GetSymbol — the batch path appears to record one
           result's tokens rather than the rendered whole
```

Consequence: batching is the route the skills recommend, and telemetry systematically flatters it. Any
future efficiency decision made from these numbers is biased toward batching by a factor of n.

### [bug] `tokensSavedByLeases` reports the previous fetch's size, not the size of the call that was leased

```
Call:      get_symbol(symbol:"sym_9c6ead28fa9d2b2c", knownVersion:"decl:a478beecbf45|refs:b9ecb187f68d")
           — default include, immediately after an unrelated get_symbol(include:"source:code@545-575")
           on the same symbol which cost 820 tokens
Observed:  leaseHits: 1, tokensSavedByLeases: 820
           The lease-hit response itself cost 61 tokens; the same call without the lease
           (default include) measured 282 tokens earlier in the run
Expected:  tokensSavedByLeases ≈ 282 − 61 = 221
Condition: any lease hit whose include differs from the caller's previous fetch of that symbol
Fix in:    Telemetry/MetricsReader.cs — the saving is being read from a per-symbol "last full response
           size" rather than from what this request's own include would have returned
```

Overstated by 3.7× here. It will always overstate when the prior fetch used a heavier `include`.

### [bug] `repeat_fetch_without_lease` fires on tools that have no `knownVersion` parameter

```
Call:      get_references(symbol:"Formats.Render", taskId:"eval_d_20260728")
           get_call_hierarchy(symbol:"sym_da4be0fc3a32dadc", includeTree:false, taskId:"eval_d_20260728")
           — two calls, neither of them get_symbol, no get_symbol call on this symbol at all
Observed:  flags: repeat_fetch_without_lease, sym_da4be0fc3a32dadc, count: 2,
           hint: "Supply knownVersion for this symbol."
Expected:  either no flag (these are not content fetches), or a hint naming an argument the tool
           actually accepts — get_references, get_call_hierarchy, get_call_slice, get_type_hierarchy
           and validate_patch have no knownVersion parameter
Condition: any repeated relations-tool call against one symbol
Fix in:    Telemetry/MetricsReader.cs — restrict the flag to get_symbol events, or make the hint
           conditional on the recorded tool
```

The advice is unactionable, which is a plausible contributor to the next finding.

### [bug] `get_symbol.declarationSites` lists generated `obj/**` files as declaration sites

```
Call:      get_symbol(symbol:"DevlogParser")
Observed:  declarationSites[3]:
             src/DotnetToolkit.McpServer/Devlog/DevlogParser.cs,7,81
             src/DotnetToolkit.McpServer/obj/Debug/net10.0/System.Text.RegularExpressions.Generator/
               System.Text.RegularExpressions.Generator.RegexGenerator/RegexGenerator.g.cs,7,23
             src/DotnetToolkit.McpServer/obj/Debug/net10.0/.../RegexGenerator.g.cs,28,49
Expected:  one site — the hand-written partial. Source-generated documents are not editable and are
           regenerated on every build.
Condition: any partial class completed by a source generator ([GeneratedRegex], [JsonSerializable],
           records with generated members, ASP.NET/EF generators — very common in real consuming repos)
Fix in:    Tools/ContextTools.cs BuildContent / Workspace/SymbolKey.cs — filter declaration sites to
           documents the solution owns, the same way the syntax index already does
```

The syntax index does *not* have this problem: `search_index(pathPrefix: "src/DotnetToolkit.McpServer/obj")`
returns zero hits. Only the workspace-backed path leaks. The concrete harm is that `CLAUDE.md`'s worked
`validate_patch` procedure says to take the line span from `declarationSites` — following it here offers
two spans in a file that will be overwritten by the next build.

### [bug] `search_index` and `get_symbol` report different `startLine` for the same symbol

```
Call:      search_index(query:"fingerprint lease escalation distiller")
             → sym_d93197f92bed1aae, EscalationTable.LevelFor, line 11, endLine 26
           get_symbol(symbol:"sym_d93197f92bed1aae")
             → declarationSites: EscalationTable.cs, startLine 10, endLine 26
           Same discrepancy on the containing type: search_index 8, get_symbol 3.
Observed:  line 10 is `/// <summary>Minimum level for a single change kind...`; line 11 is the
           `public static ValidationLevel LevelFor(...)` declaration. get_symbol's span includes the
           doc comment, search_index's excludes it.
Expected:  one convention, or the difference documented in both tools' [Description]
Condition: any symbol carrying an XML doc comment — i.e. most of a well-documented codebase
Fix in:    Indexing/ProjectIndex.cs vs Workspace/SymbolKey.cs — pick one span convention
```

The documented write path (`get_symbol` → `declarationSites` → `validate_patch`) is internally
consistent, so this does not break the recommended flow. It bites a caller who anchors an edit on a
`search_index` line number, which nothing currently forbids.

### [bug] `get_semantic_diff` returns uncapped symbol lists with no `limit` and no truncation marker

```
Call:      get_semantic_diff(fromRef:"HEAD~1", toRef:"HEAD")
Observed:  symbolsAdded[53] — 53 fully-qualified entries inline, 1,658 tokens for a single-commit range.
           The tool exposes no limit parameter and the response carries no "there is more" field.
Expected:  a cap with an explicit truncation signal, as get_call_hierarchy (maxChildrenPerNode,
           truncated/omittedChildren) and get_references (truncated) already do
Condition: any commit range that adds many symbols — a merge, a rename sweep, a new project
Fix in:    Tools/HistoryTools.cs GetSemanticDiff — add limit + a truncation marker
```

53 entries on a 2-project repo across *one* commit. A branch-vs-main diff in a real consuming repo will
be an order of magnitude larger with nothing to stop it.

### [warning] `get_references` spends most of its response on fields a caller of "who calls this" never reads

```
Cheap route:  get_call_hierarchy(symbol, maxDepth:1)   1 call → 1,138 tokens (18 callers)
Route taken:  get_references(symbol)                   1 call → 2,850 tokens (22 sites)
Frequency:    get_references.calls = 131 unfiltered, 43,386 tokens total (avg 331/call)
```

Field-by-field on the 22-item response:

| Field | Class | Note |
| --- | --- | --- |
| `dispatchKind` | **constant** | `direct` on all 22 of 22 items, and on both other `get_references` probes in this run. Hoist to a response-level field, emit per-item only when it differs. ~3 tokens × 22 |
| `contentVersion` | **unconsulted** | ~10 tokens × 22 ≈ 220 tokens/call. Load-bearing only for a caller that intends to lease each *caller* symbol — with `leaseHits: 1` in 1,010 real calls, nobody does |
| `displayString` | **verbose scalar** | full parameter lists with default values: `ContextTools.GetSymbol(...)` alone is 13 parameters ≈ 90 tokens, to answer "who calls this". A name + arity would do |
| `targetSymbolId` | restates-input | justifiable — it disambiguates which overload answered when the caller passed a bare name; suppress when the caller passed a `sym_…` id |

Estimated saving from `dispatchKind` hoisting + dropping `contentVersion` by default: ~280 tokens/call
× 131 calls ≈ **37k tokens** of this server's 711k lifetime total.

### [warning] `get_call_hierarchy` tree nodes carry full signatures, making the tree 23× its summary

```
Cheap route:  get_call_hierarchy(sym_da4be0fc3a32dadc, includeTree:false)   1 call →    50 tokens
Route taken:  get_call_hierarchy(sym_da4be0fc3a32dadc, maxDepth:1)          1 call → 1,138 tokens
```

The 1,088-token difference is almost entirely parameter lists on 18 nodes. `fields` already gates
`kind`/`file`/`line` as opt-in; `displayString` is not gated and is the expensive one. A tree exists to
show *shape* — the recommendation is to emit a short name (`ContextTools.GetSymbol`) by default and put
the full signature behind `fields`. Estimated ~60% of every tree response.

### [warning] The route table's "who calls it" row is inverted on this specimen

`skills/dotnet-code-query/SKILL.md` and the `dotnet-toolkit-selfeval` route table both list
`get_references` as the cheap route for "who calls it" and `get_call_hierarchy` as the expensive one.
Measured, for the same symbol: `get_call_hierarchy(maxDepth:1)` = 1,138 tokens, `get_references` = 2,850.

The guidance is not simply wrong — `get_references` also returns file/line/snippet per site, which the
hierarchy does not, and it reports `excludedTextMatches`. But *the outcome the row names* ("who calls
it") is reachable for 60% fewer tokens by the route documented as expensive. Fix in
`skills/dotnet-code-query/SKILL.md`: split the row into "who calls it" (→ `get_call_hierarchy`
`maxDepth:1`) and "where exactly are the call sites" (→ `get_references`).

### [warning] `groupBy: "namespace"` — the default — costs more than `groupBy: "none"` on scattered results

Identical query `"fingerprint lease escalation distiller"`, `limit: 10`:

| groupBy | Tokens |
| --- | --- |
| `none` | **429** |
| `namespace` (default) | 472 (+10%) |
| `file` | 506 (+18%) |

10 hits spread over 4 namespaces and 6 files — the nesting overhead exceeds what hoisting saves. Grouping
pays only when hits concentrate (the `pathPrefix`-scoped probe, 5 hits in 1 namespace, grouped cleanly).
`search_index` is 150 calls / 82.5k tokens unfiltered; if scattered queries are typical, the default is
costing ~10%. Recommendation is not to change the default blindly but to **choose per response**: emit
grouped only when it is actually smaller, since the server renders both from the same data and can
compare.

### [warning] `get_scope` repeats the receiver type on every row

```
Call:      get_scope(file:"src/.../ContextTools.cs", line:557, receiver:"symbolStore")
Observed:  755 tokens, 32 items. The response already carries `receiverType: SymbolStore` as a header,
           then prefixes 20 of 32 displayStrings with `SymbolStore.` and repeats `definedIn: SymbolStore`
           on each. `origin: member` and `definedIn: SymbolStore` are mutually derivable — `member`
           appears iff `definedIn` equals the receiver type.
Expected:  short names under the existing header; drop `origin` as derivable from `definedIn`
Frequency: get_scope.calls = 1 unfiltered — low traffic, so this ranks last among the warnings
Fix in:    Tools/FlowTools.cs GetScope
```

Estimated ~90 tokens of 755 (12%).

### [warning] The lease-hit response echoes the version token twice

```
Call:      get_symbol(symbol:"sym_9c6ead28fa9d2b2c", knownVersion:"decl:a478beecbf45|refs:b9ecb187f68d")
Observed:  contentVersion: "decl:a478beecbf45|refs:b9ecb187f68d"
           heldVersion:    "decl:a478beecbf45|refs:b9ecb187f68d"
```

`heldVersion` **restates-input** and, on a hit, is by definition equal to `contentVersion`. ~15 of the
61-token response. Keep it on a *miss*, where the two genuinely differ; drop it on a hit.

## [message] observations

- **The lease protocol is documented but essentially unused.** Unfiltered lifetime totals for this server
  process: 1,010 calls, 710,982 tokens, **`leaseHits: 1`** — and that one hit is mine, from this
  evaluation. Against that, 20 distinct symbols carry `repeat_fetch_without_lease` counts of 6–14 (the
  top three at 14 each). `get_symbol` alone is 536 calls / 549k tokens = **77% of all tokens this server
  has ever returned**. The mechanism the whole `Contracts/Lease.cs` layer exists to provide has never
  fired in real use. This is an ergonomics finding, not a correctness one: `knownVersion` requires the
  caller to have retained a token across turns, which is exactly what context compaction destroys. Worth
  considering a server-side alternative (e.g. an opt-in per-session "I already sent you this version"
  cache) rather than more documentation.

- **A stale `baseVersions` token is accepted when the edit is a no-op.** `validate_patch` with
  `baseVersions: {sym_d93197f92bed1aae: "decl:0000deadbeef|body:0000deadbeef"}` and an identity edit
  returned `succeeded: true, isSufficient: true` — no `stale_base`. The same bogus token with a real
  one-line change correctly returned `error: stale_base`. So the check is gated on a change being
  detected. Impact is low (nothing incorrect is written, and `CLAUDE.md` deliberately recommends identity
  edits to backfill the dev log), but a caller holding stale context is told "succeeded" rather than
  "refetch".

- **A source symbol was reported as `(unresolved)`.** `search_index(query:"Tools Store Index Workspace
  Validation Telemetry", limit:50)` returned
  `DotnetToolkit.McpServer.Validation.ValidationLevelExtensions.Wire(ValidationLevel)` under
  `namespace: "(unresolved)"`, `path: "(unresolved)"`, `line: null`. The documented cause is an
  overloaded name having no single file — `Wire` does not look overloaded. Worth a look at
  `ProjectIndex.LocateWithDocs`; a hit the caller cannot navigate to is a hit that cost tokens for nothing.

- **`origin: "external"` hits carry three dead columns.** Every external row returns
  `file: "(unresolved)"`, `line: null`, `endLine: null` — **constant** across the whole result set, since
  BCL/NuGet symbols have no source in this repo by definition. Hoist to one header line.

- **`bodyOutline` covered only lines 545–619 of a 444–624 method.** Seven entries, none before line 545,
  on `ContextTools.SearchIndex`. Probably correct (the first 100 lines are linear argument parsing with
  no control flow), but a caller reading the outline as a map of the member gets no signal that the first
  55% produced nothing. A `covers:` span or an explicit "no control flow before line N" would make the
  gap legible. Not verified as a defect.

- **Error payloads are consistently well-proportioned** — a genuine strength. `ambiguous_symbol` on
  `Render` returned 2 candidates, ~30 tokens. `unresolved_ref` = 17 tokens. `unsupported_scope` = 2 lines.
  `search_log` with no match = 3 tokens. `stale_base` returns exactly the current version needed to
  retry. Nothing here needs changing.

- **`validate_patch` distillation is the best-value response in the matrix.** The deliberately
  non-compiling edit returned 2 root causes, each with a `fixHint` and a `suggestedInspection` naming the
  exact symbol to fetch next, `totalRaw: 2 / totalSuppressed: 0` — all for ~300 tokens. The identity-edit
  verdict cost 32 tokens. Family F is clean.

- **Family A is clean and cheap**: `get_project_graph` 60 tokens whole-graph / 34 scoped,
  `detect_circular_dependencies` 10 tokens, `scope:"type"` returns the documented `unsupported_scope`,
  `ping`/`workspace_status`/`reload_workspace` behave as described. `reload_workspace(scope:"index")`
  re-scanned 99 files and `workspace_status` was unchanged afterward.

## Route table

Measured on this specimen. `(calls, tokens)`.

| Outcome wanted | Cheap route | Expensive route | Verdict |
| --- | --- | --- | --- |
| What is this symbol for? | `search_index(summary:"full")` (1, 616) | `search_index` + `get_symbol` (2, 472+178=650) | cheap wins, and answers for **all 10 hits** not one — the real ratio is 10× better |
| What does it do, in more detail? | `get_symbol(include:"bodyOutline")` (1, 345) | `get_symbol(include:"source")` (1, 474) | cheap wins (−27%) |
| What happens near line N of a long member? | `bodyOutline` → `source:code@545-575` (2, 345+820=1,165) | `get_symbol(include:"source")` on the 180-line member (1, ~2,400 est.) | cheap wins on tokens, costs +1 call |
| What is its signature? | default `include` (1, 282) | `include:"source"` (1, 474) | cheap wins |
| What shape are these five symbols? | `get_symbol(symbols:[…])` (1, **152 recorded / ~470 real**) | 3 × `get_symbol` (3, 454) | **cannot be scored from telemetry** — see the batch under-recording bug. Saves 2 round-trips; token saving is small |
| Has it changed since I looked? | `knownVersion` lease (1, 61) | plain refetch (1, 282) | cheap wins (−78%) |
| Who calls it? | `get_call_hierarchy(maxDepth:1)` (1, 1,138) | `get_references` (1, 2,850) | **inverted vs the docs** — see warning above |
| How much does changing it ripple? | `get_call_hierarchy(includeTree:false)` (1, **50**) | full tree (1, 1,138) | cheap wins by 23× — the single best-value option in the toolkit |
| What does it implement? | `search_index(implements:)` | `get_type_hierarchy` (1, 45) | **not exercised** — no interfaces in this specimen |
| How does X reach Y? | `get_call_slice` (1, 137) | repeated `get_references` hops (≥2, ≥5,700) | cheap wins by >40× |

No route in this table failed to answer the question it was scored on.

## Not exercised

**Absent from this specimen** (untested coverage, not a pass):

- **Interfaces — the largest gap.** `search_index(kinds:"interface")` returns zero across every query
  tried. That leaves unexercised: `search_index(implements:)`, `get_references(direction:
  "implementations")`, `get_references` on an interface member (the dispatch-coverage claim that is the
  headline argument for the tool over grep), `get_type_hierarchy`'s `interfaces` and `derived` arrays,
  and `referenceCounts.implementations` — which was `0` on every symbol retrieved in this run and is
  therefore an untested **constant** here. `get_type_hierarchy` was measurable only against a static
  class whose base chain is `object` (45 tokens, `derived: []`).
- **Delegates** — `kinds:"delegate"` returned nothing.
- **Genuine hand-written multi-file partial classes** — both `partial` hits (`DevlogParser`,
  `OutlineBuilder`) are single-file partials completed by a source generator. The
  "`get_symbol` returns the whole symbol across fragments" claim was therefore tested only in its
  generator form, which is what surfaced the `obj/` leak but not the ordinary two-hand-written-files case.
- **Deep project graphs / cycles** — 2 projects, one edge, zero cycles.
  `detect_circular_dependencies` was verified only on an acyclic graph.
- **Multi-targeted `.csproj`, `.sln` (vs this `.slnx`), unconventional layouts** — not present.

**Tools that record no telemetry and so carry no measured cost** — judged by eye only, all correct and
constant-sized: `ping` (1 line), `workspace_status` (5 lines), `set_output_format` (1 line),
`reload_workspace` (1 line), and `get_retrieval_metrics` itself (deliberately, so it does not perturb the
deltas it computes).
