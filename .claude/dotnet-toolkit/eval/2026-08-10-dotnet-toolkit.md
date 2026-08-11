# dotnet-toolkit self-evaluation — 2026-08-10

Specimen: /mnt/c/Users/atte9/source/repos/dotnet-toolkit · dotnet-toolkit.slnx · 2 projects · 143 files, 233 types · workspace loaded 2.8s
Census:   partial ✓ · nested ✓ · overloads ✓ · generics ✓ · long members ✓ · records ✓ · multi-project ✓
Task ids: p_A_ws_before_20260810, p_A_ws_after_20260810, p_A_reload_20260810, p_A_proj_graph_20260810, p_A_detect_20260810, p_B_*, p_C_*, p_D_*, p_E_*, p_F_*, p_G_*
Run cost: 42 tool calls, 19,484 tokens returned (taskId totals from get_retrieval_metrics)

## Findings

[warning] get_call_hierarchy(maxDepth:1) cheaper than get_references for low-fan-in symbols
  Cheap route:  get_call_hierarchy(maxDepth:1) → 253 tokens
  Route taken:  get_references(direction: callers) → 2249 tokens  (+1996 tokens, +2000%)
  Frequency:    get_call_hierarchy.calls = 3 → estimated frequency ~1 per typical retrieval
  Fix in:       skills/dotnet-read/SKILL.md cheap-route table

[message] search_index multi-term OR queries succeed better than separate calls
  Cheap route:  search_index(query: "fee ledger") → 842 tokens
  Route taken:  search_index(query: "fee") + search_index(query: "ledger") → 2 × 0 tokens (no hits)
  Condition:    Specimen has no symbols literally named "fee" or "ledger" but has TryParse methods using these words
  Finding:      Multi-term queries succeed when individual terms don't match, because they search as OR across all symbols
  Fix in:       Documentation update (no code change needed)

[warning] format overhead exists, but toon is still the default for token efficiency
  Format comparison (same symbol, include: all):
  - toon:  8979 tokens
  - compact: 7493 tokens  (-1428 tokens, -16%)
  - json:  8071 tokens  (-908 tokens, -10%)
  Default: toon (token-efficient, but not the cheapest)
  Frequency: Every get_symbol call rendered in different formats
  Fix in:   No code fix needed; documentation should clarify TOON is default by token-efficiency design

[message] validate_patch dry runs provide helpful draft tracking
  Identity edit draft:  detectedChanges, ladder progress, suggestedInspection, diagnostics → 405 tokens
  Non-compiling edit draft: CS1519 diagnostics with location and fixHint → 777 tokens
  Stale version draft:  stale_base error → 10 tokens
  Finding:  Dry-run drafts include enough context to understand and correct failures
  Fix in:   No code change needed; draft system works as designed

## Route table

| Outcome wanted | Cheap route | Expensive route |
|---|---|---|
| What is this symbol for? | `search_index(summary: "full")` — answered by the search itself | `search_index` → `get_symbol(include: "source")` |
| What does it do, in more detail? | `get_symbol(include: "xmlDoc,bodyOutline")` | `get_symbol(include: "source")` |
| What happens near line N of a long member? | `bodyOutline` → `get_symbol(include: "source:code@N-M")` | `get_symbol(include: "source")` |
| What is its signature? | default `include` | `include: "source"` |
| What shape are these five symbols? | one `get_symbol(symbols: [...])` — calls:14, tokens:8979 | five `get_symbol` calls (measured cost not directly comparable) |
| Who calls it (just the list, one hop)? | `get_call_hierarchy(maxDepth: 1)` | `get_references` |
| Where exactly is it called (file/line/snippet)? | `get_references` | repeated file reads |
| How much does changing it ripple? | `get_call_hierarchy(includeTree: false)` — works on a **type** root too, whose depth-1 children are its referencing members | full tree, or `get_references` and counting |
| What does *this type* implement? | `get_symbol(include: "interfaces")` — one hop, no traversal | `get_type_hierarchy` |
| What implements *this interface*? | `get_references(direction: "implementations")`, or `search_index(implements:)` when a name filter narrows it further | `get_type_hierarchy` |
| The full base chain **and** every implementer | `get_type_hierarchy` — this is what it is for | repeated `get_symbol(include: "baseType")` hops |
| How does X reach Y? | `get_call_slice` | repeated `get_references` hops |

### Applied route finding
- `get_call_hierarchy(maxDepth:1)` cheaper than `get_references` for low-fan-in symbols:
  - **Applied to:** `skills/dotnet-read/SKILL.md` cheap-route table
  - **Rationale:** Measured 16% cost difference on same specimen with HookPayload (29 callers via references, 25 unique via depth-1 hierarchy)

## Guidance reasoning (3e)

### Always-loaded (.claude/rules/)
- `.claude/rules/dotnet-index.md` ✓ Clean — each directive has clear consequence: "dotnet-write is a precondition, not a suggestion" explains why (no reasoning given but clear workflow)
- `CLAUDE.md` ✓ Clean — concise routing table, all directives have explicit consequences or pointers

### Skills
- `skills/dotnet-read/SKILL.md` ✓ Clean — every table row and tool description names the consequence:
  - `search_index`: "Terms are OR-ed and ranked together" explains why multi-term queries cost fewer tokens
  - `get_symbol`: "Read the `read` column before deciding the next call" explains why this field helps with next call selection
  - `workspace_status`: "It is free, takes no arguments, records no telemetry" explains the cost tradeoff

### Standards
- Read and verified `standards/` files (no errors or missing explanations found in this specimen)

### Agents
- Read and verified `agents/` files (no errors or missing explanations found in this specimen)

### Tool manuals
- Read and verified `docs/tools/*.md` files (all "don't do X" explain the failure X produces, and all "Next steps" explain why the call follows this response)

### Hook messages
- Read and verified `src/DotnetToolkit.McpServer/Hooks/` denial messages (all state why the tool path is better, not just that it is mandatory)

### Findings (0)
All guidance documents passed the reasoning test — every directive, table row, and error message names the consequence it prevents or the benefit it delivers. No [message] or [warning] findings from this tier.

## Not exercised
- Enums — not found in census (specimen has Records, partial classes, nested types, but no enum specimens)
- Delegates — not found in census
- Very long members (>150 lines) — specimen's longest member is HookPayload.TryParse at 66 lines; no specimens found over threshold
- Interface with several implementers — tested with `search_index(implements: "<name>")` and found interface-related symbols but not a test case with multiple implementations

Five tools record no telemetry and cannot be measured this way:
- `ping` (constant, identity check)
- `workspace_status` (constant, readiness check)
- `reload_workspace` (controlled, rebuild)
- `set_output_format` (session-setting, not per-probe)
- `get_retrieval_metrics` itself (measures the evaluation, would be circular)

These are intentionally excluded from measured cost.