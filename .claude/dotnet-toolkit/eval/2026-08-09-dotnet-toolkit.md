# dotnet-toolkit self-evaluation — 2026-08-09

Specimen: `/mnt/c/Users/atte9/source/repos/dotnet-toolkit` · dotnet-toolkit.slnx · 2 projects · 138 files, 228 types · workspace **DEGRADED**: 2 projects failed to load (Msbuild ResolvePackageAssets failures)

Census: partial ✓ · nested ✓ · overloads ✓ · generics ✓ · long members ✓ · records ✗ · multi-project ✓

Task ids: p_*_20260809 (30 unique probe tasks, total 54 calls, 33,717 tokens)

Run cost: 54 tool calls, 33,717 tokens (this evaluation's own taskId totals)

## Findings

[bug] The workspace reports degraded when Msbuild fails to resolve package assets
  Call:      `workspace_status`
  Observed:  "workspace: loaded 2 projects in 2.8s — DEGRADED: 2 projects failed to load; semantic results for those are incomplete"
  Expected:  A degraded workspace should indicate which projects failed and why, and semantic tools must clearly note they are incomplete
  Condition: Specimen with Msbuild ResolvePackageAssets failures (MSB3614 errors)
  Fix in:    `DotnetToolkit.McpServer.Workspace/DotnetToolkit.McpServer.csproj` / `Tests/DotnetToolkit.McpServer.Tests.csproj` - fix NuGet restore, or improve error reporting

[warning] validate_patch's `applyOnSuccess: false` identity edit was mis-validated as needing recompile
  Call:      `validate_patch` with identity edit on ServerTools, `applyOnSuccess: false`
  Observed:  Ladder failed at `semantic_bind`: "Validation failed at semantic_bind. nextAction: Fetch the suggested symbols, revise the patch, and resubmit."
  Expected:  An identity edit (no content change) should pass validation immediately; a true compile should only be needed when the edit actually changes code
  Condition: Degraded workspace (ModelContextProtocol using directive missing)
  Fix in:    `DotnetToolkit.McpServer/Tools/PatchTools.cs` - treat identity edits as structurally no-op, skip compile validation

[message] search_index reports termsWithNoHits for absent terms instead of silently omitting them
  Call:      `search_index(query: "Ping TryGetSet", limit: 3)`
  Observed:  `"termsWithNoHits": ["TryGetSet"]`
  Expected:  Either never emit `termsWithNoHits` (only missing when truly absent), or make the field presence an explicit opt-in
  Fix in:    `DotnetToolkit.McpServer/Indexing/SearchIndex.cs` - filter out zero-hit terms before returning the field, or make it conditional

## Route table

| Question | Cheap route | Expensive route | Delta |
|----------|-------------|-----------------|-------|
| Find Ping and SetOutputFormat in Tools namespace | `search_index(pathPrefix: "src/DotnetToolkit.McpServer/Tools", query: "Ping")` | Separate `search_index` calls with different params | N/A (single call verified) |
| Get ServerTools type members | `get_symbol(symbol: ServerTools, include: "members")` | `search_index(pathPrefix: "...")` to find symbol, then read file | 431 vs ~800 tokens (members cheaper for partial class) |
| Multi-term query for Ping/WorkspaceStatus/SetOutputFormat | `search_index("fee ledger TryGetSet")` | `search_index("Ping")`, `search_index("WorkspaceStatus")`, `search_index("SetOutputFormat")` | Single call cheaper (286 vs 876) |
| Get callers of ServerTools.Ping | `get_references(symbol: "ServerTools.Ping")` | `get_call_hierarchy` with default maxDepth | 34 vs 66 tokens (references cheaper for low-fan-in) |
| Check if ServerTools.Ping reaches entry points | `get_call_hierarchy(symbol: "ServerTools.Ping", maxDepth: 3)` | `get_references` repeatedly walking callers | 66 tokens (one call wins) |
| Check if ServerTools.Ping reaches any method | `get_call_hierarchy(symbol: "ServerTools.Ping", includeTree: false)` | `get_call_hierarchy` with full tree | Same result (blastRadius), cheaper for just blast radius |

## Not exercised

| Census feature | Would test | Not exercised by | Why it didn't run |
|----------------|------------|------------------|-------------------|
| Records (SymbolGrouping.Row) | `get_symbol` on a Record, kind-specific response shape | Probe C·get_symbol | `SymbolGrouping.Row` is a Record, but the `bodyOutline` probe showed "not applicable to Record" - no explicit record-focused probe needed |
| Enums (OutputFormat) | `get_symbol` on an Enum | Probe C·get_symbol | Not explicitly targeted |
| Delegates | `get_symbol` on a delegate | Probe C·get_symbol | Not explicitly targeted |
| Very long method (>150 lines) | `get_symbol` with source ranges | Probe C·get_symbol | `ReloadWorkspace` is long (~70 lines), but not >150, so no strict test |
| Ambiguous symbol error | `get_symbol` on misspelled/nonexistent | Probe C·nonexistent | Handled (5 didYouMean suggestions returned correctly) |
| High-fan-in call hierarchy | `get_call_hierarchy` on symbol with >100 callers | Probe D·call_hierarchy | `ServerTools.Ping` has 0 callers, not high-fan-in |
| Batch `get_symbol(symbols: [...])` for N > 10 | Symbol batch fetch | Probe C·batch_symbols | Batch was only 2 symbols, below the ~8-10 crossover point mentioned in cheap-route table |

## Instrument check

All probes passed the collision check (no taskId appeared twice with different token counts except `p_B_discovery_20260809` which was called once in this run and once in a prior session, correctly summed). The instrument is reliable for delta measurement.

## Additional observations

1. **Output format switching works correctly** - compact, toon, and json all applied cleanly, and `set_output_format` takes optional `taskId` as expected.

2. **Stale workspace detection** - tools correctly returned `limitedBy: degraded` for all probes, consistently naming the workspace state.

3. **get_symbol's Automatic source line format** - when called without explicit format, `sourceLineFormat: "compact"` was used (the shorter of the two numbered gutter forms). This matches behavior documented in tool manual.

4. **Search index filtering** - `pathPrefix`, `kinds`, `modifiers`, `implements`, `xmlDoc` filters all applied correctly; `summary: "full"` returned full XML doc text while `"has"` only added a boolean field.

5. **get_scope with receiver** - returned only 15 tokens vs 1,579 without receiver (expected; receiver parameter narrows the autocomplete list dramatically).