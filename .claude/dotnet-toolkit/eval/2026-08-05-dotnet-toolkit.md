# dotnet-toolkit self-evaluation — 2026-08-05

Specimen: /mnt/c/Users/atte9/source/repos/dotnet-toolkit · dotnet-toolkit.slnx · 2 projects (DotnetToolkit.McpServer, DotnetToolkit.McpServer.Tests) · 138 files, 227 types · workspace loaded in 2.2–2.6s, no diagnostics
Census:   partial ✓ (SymbolStore split across SymbolStore.cs/SymbolStore.Update.cs) · nested ✓ (SymbolStore N5, ContextTools N4, TelemetryRecorder N1) · overloads ✓ (ValidationLevelExtensions.Wire(ValidationLevel)/Wire(ChangeKind)) · generics ✓ (WorkspaceHost.RunExclusiveApplyAsync<T>) · long members ✓ (ContextTools 2242 lines; PatchTools.ValidatePatch 270 lines; RenameTools.RenameSymbol 297 lines) · records ✓ (HookPayload + many `Result`/row records) · enums ✓ (ValidationLevel, ChangeKind, WorkspaceState, OutputFormat) · delegates ✗ (none found in this repo) · multi-project ✓ but minimal (2 projects, one-directional reference, no cycles)
Task ids: `p_instrument_check`, `p_census_*` (census), `p_A_*` (orientation), `p_B_*` (discovery), `p_C_*` (retrieval), `p_D_*` (relations), `p_E_*` (history), `p_F_*` (write path, dry-run only), `p_G_*` (output format) — all suffixed `_20260805`
Run cost: 78 tool calls, 58,278 tokens returned (this evaluation's own taskId totals, `get_retrieval_metrics` excluded as it records no telemetry)

## Findings

[bug] `search_index` returns empty `file`/`line`/`endLine`/`shape` for an overloaded extension method, while `get_symbol` resolves the same symbol correctly
  Call:      `search_index(query: "Wire", kinds: "method")`
  Observed:  both `ValidationLevelExtensions.Wire(ValidationLevel)` and `Wire(ChangeKind)` hits return `file:"", line:"", endLine:"", shape:""`
  Expected:  the real location (`src/DotnetToolkit.McpServer/Validation/ValidationLevel.cs`, lines 43–53, confirmed via `get_symbol(symbol: "sym_65921a961568a3f5")`)
  Condition: an overloaded extension method — reproduced identically on two separate calls, not a one-off
  Fix in:    `search_index`'s indexing/location-lookup path in `SymbolIndexBuilder`/`ProjectIndex` — the same overload resolves fine through `get_symbol`, so the gap is specific to how `search_index` populates location fields for this shape

[bug] `get_symbol(include: "all")` silently drops the `xmlDoc` component even when the symbol has real XML doc content
  Call:      `get_symbol(symbol: "DotnetToolkit.McpServer.Store.SymbolStore", include: "all")`
  Observed:  `components[3]: source,members,usings` — no `xmlDoc` anywhere in the response
  Expected:  `xmlDoc` present, since the default (`standard`) include on the same symbol returns `xmlDoc.summary`, and an explicit `include: "xmlDoc,attributes,referenceCounts,baseType,interfaces,mechanicalFacts,recentLog"` on the same symbol returns it in full
  Condition: reproduced on a second, unrelated symbol (`ValidationLevel` enum) — systemic, not symbol-specific
  Fix in:    `get_symbol`'s `include: "all"` expansion in `ContextTools.GetSymbol` — it should be the union of every component `standard`/an explicit list can return, and today it is missing at least `xmlDoc`

[warning] `get_call_hierarchy` renders an identical subtree twice, verbatim, when two branches converge on the same symbol (a diamond)
  Call:      `get_call_hierarchy(symbol: "DotnetToolkit.McpServer.Store.SymbolStore.FqNameFor", direction: "callers", maxDepth: 3)`
  Cheap route:  the six children under `FlowTools.GetCallHierarchy` counted once → ~989 tokens total minus one duplicated block
  Route taken:  the same six-child list is rendered in full under both `FlowTools.ResolveToIdAsync → FlowTools.GetCallSlice → FlowTools.GetCallHierarchy` and `FlowTools.TypeSeedIdsAsync → FlowTools.GetCallHierarchy` — ~989 tokens for the whole depth-3 tree, with that one node's list duplicated in full
  Frequency:    `get_call_hierarchy` is one of the more expensive tools per the tool table's own framing ("a high-fan-in symbol's full page is the most expensive response"); any well-connected graph produces diamonds, so this recurs on every call-hierarchy tree with shared dependencies
  Fix in:       `CallHierarchy.Build` (`src/DotnetToolkit.McpServer/Indexing/CallHierarchy.cs`) — a node already visited elsewhere in the tree could reference its symbolId rather than re-expanding its full child list, since `blastRadius` already dedupes by symbolId and the docstring calls this out as a "diamond" case

[message] `symbol_not_found`'s `didYouMean` suggestions for a fully bogus name are token-related but not name-related
  Call:      `get_symbol(symbol: "DotnetToolkit.McpServer.NoSuchSymbolAtAllXyz")`
  Observed:  candidates include `SyntaxFingerprint.AllTokens`, `GuardCsEditTests.Evaluate_NonCsFile_Allows`, `TestAttributeTests.DoesNotMarkNonMethods` — none within a small edit distance of the input, but several share a camelCase token (`All`, `Not`) with it
  Expected:  unclear whether this is intentional (a token-overlap fallback beyond `SymbolStore.NearNames`'s edit-distance path) or an artifact of a broader fallback firing on a name with no real near miss
  Condition: only visible on a genuinely nonsense symbol name — a realistic typo wouldn't reach this path
  Fix in:    worth a maintainer look at whichever code produces `didYouMean` for `symbol_not_found` (not `SymbolStore.NearNames` itself, whose length-filtered edit-distance logic would have rejected all of these candidates outright)

## Route table (3a)

| Outcome | Cheap route | Expensive route |
| --- | --- | --- |
| 4 known type names, one query | `search_index(query: "SymbolStore HookPayload TelemetryRecorder ValidationLevel")` — 1 call, 415 tokens | 4 separate `search_index` calls — 4 calls, 1,477 tokens |
| One region of a 690-line type | `get_symbol(include: "source:code@630-638;646-660")` — 400 tokens | `get_symbol(include: "source:code")` whole type — 7,289 tokens |
| Who calls this, one hop | `get_call_hierarchy(maxDepth: 1)` — 105 tokens | `get_references(direction: "callers")` — 180 tokens |
| Same `get_symbol` call, output format | `toon` — 351 tokens | `json` — 712 tokens (`compact` — 482 tokens) |
| `search_index` grouping on scattered hits | auto (chose ungrouped) — 436 tokens | explicit `groupBy: "file"` — 526 tokens |

All five rows confirm existing guidance (the tool table, and `search_index`'s own auto-groupBy chooser) rather than contradicting it.

## Family results

- **A · Orientation** — clean. `get_project_graph` whole vs. scoped, `detect_circular_dependencies` default and unsupported `scope: "type"`, `reload_workspace` + `workspace_status` all behaved as documented.
- **B · Discovery** — one bug (`Wire` location gap, above). Multi-term-in-one-call confirmed cheaper; `kinds`/`modifiers` AND/exclude, `pathPrefix`, `implements`, `summary: has/full`, `origin: external`, and all three `groupBy` values behaved correctly. `xmlDoc: "remarks"` returned 0 hits at negligible cost (3 tokens) — this repo's own code has few/no `<remarks>` tags outside a couple of hand-picked examples, so this wasn't a meaningful test of the filter itself.
- **C · Retrieval** — one bug (`include: "all"` drops `xmlDoc`, above). The full include ladder otherwise behaved correctly: partial-class unification (declarationSites + members spanning both files), subtractive `source:full-remarks-attributes` correctly strips just the `<remarks>` block (verified against `HookPayload`, which has a real one), `-lineNumbers` suppresses the gutter, `ambiguous_symbol` and `symbol_not_found` payloads are compact, and batch `symbols:[...]` hoists shared fields (`origin: source`) into one `shared` block.
- **D · Relations** — clean. `get_references` (callers + implementations), `get_type_hierarchy` on an interface, `get_scope` with/without `receiver` (correctly empty-celling own-type origin), and `get_call_hierarchy` at rising depth/cap all matched their documented contracts — notably `blastRadius.totalUniqueNodes` did not move between default and `maxChildrenPerNode: 1` at the same `maxDepth`, confirming the cap-vs-blast-radius independence the docs claim.
- **E · History** — clean. `search_log` correctly returns empty for a term genuinely absent from the log (verified the log isn't itself empty via an unfiltered listing first) and hits for a real term; `get_semantic_diff` over a 3-commit range and an unresolvable ref both behaved correctly.
- **F · Write path** — clean, all `applyOnSuccess: false`. `validate_patch`: identity edit (sufficient, `project_compile`), a deliberately non-compiling edit (precise `CS0103` diagnostic, one root cause), a stale `baseVersions` (`stale_base` error), and a body edit built on a declaration-only version (`unleased_body` with actionable next-step guidance). `rename_symbol`: dry run (correct `files`/`occurrences`/`detectedChanges`), a colliding name (`CS0102`, clean diagnostic), and a stale `baseVersion` (`stale_base`).
- **G · Meta** — clean, confirms `toon` is cheapest of the three formats on this specimen (351 < 482 < 712 tokens for the same call). Format restored to `toon` before finishing.

## Not exercised

- **Delegates** — this repo declares none; the delegate-kind response shape is untested here.
- **3d (advice) full crossover measurement** — the `shape` column's `O`/`D`/`C`/`A` labels were exercised as data (and the `L` label was spot-checked against `endLine - line + 1`, which matched), but the paid-vs-cost-more-vs-owed-but-absent comparison for each label was not run as its own dedicated probe set within this pass's budget.
- **Five tools recording no telemetry** — `ping`, `workspace_status`, `set_output_format`, `reload_workspace`, `get_retrieval_metrics` — judged by eye only, as designed: all returned small, well-formed responses.
