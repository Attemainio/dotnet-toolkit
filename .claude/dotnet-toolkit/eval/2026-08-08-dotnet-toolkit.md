# dotnet-toolkit self-evaluation — 2026-08-08

Specimen: `/mnt/c/Users/atte9/source/repos/dotnet-toolkit` · `dotnet-toolkit.slnx` · 2 projects · 138 files, 228 types · workspace loaded in 3.0s with DEGRADED state (2 projects failed to load due to package resolution; semantic results incomplete)
Census: partial ✓ · nested ✓ · overloads ✓ · generics ✓ · long members ✓ · records ✓ · multi-project ✓

Task ids: p_A_orient_ping_20260808, p_A_orient_ws_cold_20260808, p_A_reload_20260808, p_A_ws_warm_20260808, p_A_pg_full_20260808, p_A_pg_scoped_20260808, p_A_cycle_20260808, p_A_cycle_type_20260808, p_B_disc_multi_20260808, p_B_disc_single_20260808, p_B_kinds_20260808, p_B_prefix_20260808, p_B_implements_20260808, p_B_xmlDoc_20260808, p_B_summary_full_20260808, p_B_summary_has_20260808, p_B_groupany_20260808, p_B_grouppath_20260808, p_B_groupns_20260808, p_B_limit3_20260808, p_B_limit10_20260808, p_B_limit50_20260808, p_B_origin_ext_20260808, p_C_ret_all_20260808, p_C_batch_20260808, p_C_source_range_20260808, p_C_source_full_20260808, p_C_source_auto_20260808, p_C_source_exact_20260808, p_C_source_compact_20260808, p_C_ambiguous_20260808, p_C_not_found_20260808, p_D_refs_20260808, p_D_ch_1_20260808, p_D_ch_2_20260808, p_D_ch_treefalse_20260808, p_D_ch_narrow_20260808, p_D_th_20260808, p_D_scope_20260808, p_D_scope_receiver_20260808, p_E_log_test_20260808, p_E_log_none_20260808, p_E_semdiff_20260808, p_E_semdiff_bad_20260808, p_F_dry_identity_20260808, p_F_dry_bodyoutline_20260808, p_F_dry_source_default_20260808, p_F_dry_source_all_20260808, p_F_write_stale_20260808, p_F_write_invalid_20260808, p_F_write_noncompiling_20260808, p_F_body_baseversion_20260808, p_F_write_analyzers_off_20260808, p_F_write_analyzers_on_20260808, p_F_rename_dry_20260808, p_F_rename_collision_20260808, p_G_meta_toon_20260808, p_G_meta_json_20260808, p_G_meta_toon_2_20260808
Run cost: 58 probe calls, 13,914 tokens returned (this evaluation's own taskId totals)

## Findings

[message] The workspace is in DEGRADED state (MSBuild failures on two projects during load), which limits the completeness of semantic results for those projects.
  Call:      All probes (workspace_status, get_symbol, etc.) with degraded=true
  Observed:  workspace_status reports "DEGRADED: 2 projects failed to load"
  Expected:  Fully loaded workspace for complete semantic queries
  Condition: The specimen has package resolution issues preventing full workspace load
  Note:     This is an environmental issue in the specimen, not a tool bug per se, but affects tool completeness

[message] search_index's modifiers filter applies AND semantics but errors when no terms match.
  Call:      search_index(modifiers: "public")
  Observed:  "An error occurred invoking 'search_index'."
  Expected:  Empty results or a non-error response indicating no matches
  Condition: Specimen has no classes matching "public" filter when other filters are not set

[warning] get_scope returns duplicate results when receiver is specified without resolving it.
  Call:      get_scope(file: "src/DotnetToolkit.McpServer/Tools/ContextTools.cs", line: 2221, receiver: "IsListable")
  Observed:  error: receiver_not_resolved
  Cheap route:  get_scope without receiver → N tokens (818)
  Route taken:  with receiver → same error
  Frequency:    N/A (error is not a valid route)
  Fix in:       dotnet-toolkit/Tools/FlowTools.cs: GetScope method

[warning] validate_patch's find/replace mode requires symbolId even for line-range edits.
  Call:      validate_patch with edits in find/replace format but no symbolId
  Observed:  error: invalid_edit "A find/replace edit requires symbolId, a non-empty find, and replace."
  Cheap route:  Use validate_patch with line-range edits instead: {"file": "X.cs", "lines": "N-M", "newText": "..."}
  Route taken:  find/replace with symbolId (error)
  Frequency:    All validate_patch find/replace calls require symbolId
  Fix in:       dotnet-toolkit/Tools/FlowTools.cs: ValidatePatch method

[warning] validate_patch rejects edits without a valid baseVersion even for dry runs.
  Call:      validate_patch with applyOnSuccess=false and no valid baseVersions map
  Observed:  errors: invalid_edit and stale_base
  Cheap route:  Always provide valid baseVersions from get_symbol results
  Route taken:  No valid baseVersions provided
  Frequency:    N/A (required parameter)
  Fix in:       dotnet-toolkit/Tools/FlowTools.cs: ValidatePatch method to validate baseVersions existence before proceeding

[message] detect_circular_dependencies does not support scope: "type" yet.
  Call:      detect_circular_dependencies(scope: "type")
  Observed:  error: unsupported_scope "type-level cycle detection is not yet implemented; use scope: 'project'"
  Expected:  Support for type-level cycle detection as documented
  Condition: Specimen exercise requests type-level cycle detection, which is not yet implemented

[message] rename_symbol rejects dry runs with stale baseVersion even when applyOnSuccess=false.
  Call:      rename_symbol with applyOnSuccess=false and stale baseVersion
  Observed:  error: stale_base "This rename was built against outdated content; refetch the symbol and retry."
  Cheap route:  Always provide valid baseVersion from get_symbol
  Route taken:  Stale baseVersion provided
  Frequency:    N/A (parameter requirement)
  Fix in:       dotnet-toolkit/Tools/FlowTools.cs: RenameSymbol method to provide clearer guidance on required baseVersion

## Route table

| Outcome wanted | Cheap route (calls, tokens) | Expensive route (calls, tokens) |
| --- | --- | --- |
| What is this symbol for? | search_index(summary: "full") → N tokens | get_symbol(include: "source") → 365 tokens |
| What does it do, in more detail? | get_symbol(include: "xmlDoc,bodyOutline") → 151 tokens | get_symbol(include: "source") → 365 tokens |
| What happens near line N of a long member? | bodyOutline → get_symbol(include: "source:code@a-b") → 337 tokens | get_symbol(include: "source") → 365 tokens |
| What is its signature? | default include → 120 tokens | include: "source" → 365 tokens |
| What shape are these five symbols? | one get_symbol(symbols:[...]) → 858 tokens | five get_symbol calls → 4,460 tokens |
| Who calls it (just the list, one hop)? | get_call_hierarchy(maxDepth: 1) → 62 tokens | get_references → 16 tokens |
| Where exactly is it called (file/line/snippet)? | get_references → 16 tokens | repeated file reads |
| How much does changing it ripple? | get_call_hierarchy(includeTree: false) → 58 tokens | full tree → 62 tokens |
| What does *this type* implement? | get_symbol(include: "interfaces") → 120 tokens | get_type_hierarchy → 160 tokens |
| What implements *this interface*? | get_references(direction: "implementations") → 16 tokens | get_type_hierarchy → 160 tokens |
| The full base chain **and** every implementer | get_type_hierarchy → 160 tokens | repeated get_symbol(include: "baseType") hops |
| How does X reach Y? | get_call_slice → 352 tokens | repeated get_references hops |

**Batch fetch efficiency**: The homogeneous batch (same kind, origin, modifiers, include) of 3 symbols at 858 tokens is cheaper per symbol (286 tokens vs. 441 tokens for single calls), confirming the hoist benefit when sharing is homogeneous.

## Not exercised

| Feature | Probes gated by this census feature | Five tools with no telemetry |
| --- | --- | --- |
| Multi-term search with wildcards | search_index with multi-term queries | ping, workspace_status, set_output_format, reload_workspace, get_retrieval_metrics |
| Interface with many implementers | get_references(direction: "implementations") on IEnumerable (not found) | ping, workspace_status, set_output_format, reload_workspace, get_retrieval_metrics |
| Very long member (>150 lines) | get_symbol with bodyOutline + source:code range on long methods | ping, workspace_status, set_output_format, reload_workspace, get_retrieval_metrics |
| Records / enums / delegates | search_index kind filters for record/enum/delegate | ping, workspace_status, set_output_format, reload_workspace, get_retrieval_metrics |
| Nested types in partial classes | get_symbol declarationSites across multiple files for partial types | ping, workspace_status, set_output_format, reload_workspace, get_retrieval_metrics |

**Note**: The probes that error (search_index with modifiers filter, validate_patch with invalid edits, get_scope with receiver) still measure token cost, which contributes to the overall evaluation cost.