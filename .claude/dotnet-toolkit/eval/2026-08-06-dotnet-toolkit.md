# dotnet-toolkit self-evaluation — 2026-08-06

Specimen: /mnt/c/Users/atte9/source/repos/dotnet-toolkit · dotnet-toolkit.slnx · 2 projects (DotnetToolkit.McpServer, DotnetToolkit.McpServer.Tests) · 138 files, 228 types · workspace loaded in 2.9s, no diagnostics
Census:   partial ✓ (SymbolStore across SymbolStore.cs/SymbolStore.Update.cs) · nested ✓ (SymbolStore N5, ContextTools N4, FeatureLogStore N3) · overloads ✓ (`Error` ambiguous across 4 types; `ReferenceCounts` 2 overloads) · generics ✓ (`Dictionary<string,string?>`, `IReadOnlyCollection<T>` throughout) · long members ✓ (`WorkspaceHost.LoadAsync` 108 lines, `ContextTools` type 2337 lines) · records ✓ (11+ found) · enums ✓ (5 found) · multi-project ✓ (2, no cycles) · interfaces: only **one** in the whole repo (`IKnowledgeStore`, single implementer `KnowledgeStore`) · no multi-targeted `.csproj`, `.slnx` entry point
Task ids: prefix `p_<family>_<name>_20260806`, families A–G per the fixed matrix, plus `p_census_*` and `p_instrument_check`
Run cost: ~84 tool calls, ~61,500 tokens returned (this evaluation's own taskId totals, families A–G + census + instrument check)

## Findings

[message] search_index's own "put every term in one call" guidance is confirmed with real numbers on this specimen: one call with 4 terms (`"SymbolStore WorkspaceHost PatchTools ContextTools"`, limit 10) cost 496 tokens, vs. 1,095 tokens across 4 separate single-term calls — 2.2x cheaper in tokens and 4x fewer calls for the same coverage.
  Cheap route: 1 call → 496 tokens (`p_B_multi_20260806`)
  Route taken: 4 calls → 1,095 tokens (`p_B_separate_20260806`)
  No fix needed — this validates existing guidance rather than contradicting it.

[message] For a large TYPE (not a method), fetching full source is the expensive route and `include:"members"` + targeted per-member `get_symbol` calls is confirmed cheaper, matching the tool's own documented advice. `SymbolStore` (492+205 combined lines across its two partial files) cost 9,288 tokens for `include:"source"` and 9,311 for `include:"all"` (`all` adds almost nothing over `source` alone once source's built-in suppression of members/xmlDoc/bodyOutline kicks in — confirms the dedup shipped in commit 442ccdf is working). `include:"members"` alone cost 2,144 tokens and, combined with fetching 3 needed methods individually (993 tokens total), reaches the same information for ~3,137 tokens — a 3x saving over the whole-type fetch.
  Cheap route: members (2,144) + 3 targeted fetches (993) = 3,137 tokens
  Route taken: whole-type `source`/`all` = 9,288–9,311 tokens
  No fix needed — confirms existing guidance with numbers; worth keeping as a baseline for future comparison.

[message] `get_symbol`'s `symbols:[…]` batch is token-neutral versus N individual calls, not token-cheaper — its win is purely round-trips. Fetching 3 small `PatchTools` methods (`Verdict`, `BodySpanOf`, `TryParseLines`) with `source:code` as one batched call cost 1,003 tokens; the same 3 methods as separate calls cost 993 tokens combined (1 call vs. 3, but +10 tokens, not a saving). The tool's description already frames batching as saving round-trips, not tokens, so this is confirmatory rather than a correction — but worth knowing precisely, since a caller optimizing purely for token count has no reason to prefer batch over individual here.

[message] Automatic source line format correctly picks the cheaper rendering, confirmed on a real method. On `WorkspaceHost.LoadAsync` (108 lines, heavily indented, many short lines), Automatic cost 1,357 tokens — within 7 tokens of the compact-forced rendering (1,350) and 125 tokens (9.3%) under the exact/gutter-forced rendering (1,482). Matches the intent of the Automatic redesign (commit b2b1c72) and the `sourceLineFormat` reporting field (commit e4188ce); no drift found.

[message] `get_scope` without a `receiver`, at a position deep inside a 46-member type (`SymbolStore.cs:100`), reports `totalItems: 607, truncated: true` and the default `limit:40` page mixes the type's own ~30 relevant members with BCL exception types (`AbandonedMutexException`, `AccessViolationException`, …) that alphabetically sort into the same "types" bucket ahead of anything more contextually relevant. This is within documented behavior (default limit 40, cap 200) and not a measured waste — flagging as an observation on ranking within the "types" origin bucket, since alphabetical order gives no preference to symbols the cursor's own file/namespace already touches.

[message] rename_symbol's "colliding name" probe did not actually collide: renaming `PatchTools.TryParseLines` to `Verdict` (an existing method in the same class, but with a different signature) succeeded at `dependent_compile` as a legitimate C# overload rather than erroring — correct compiler behavior, not a tool bug. My probe design didn't produce a genuine collision (same name **and** same signature); see "Not exercised."

[message] `get_call_slice`'s intended "miss" probe (`ContextTools.SearchIndex` → `Ids.ToolCall`) instead found a real 1-hop path (`found: true`, `nodesExplored: 85`) — a probe-design miss, not a tool finding. A genuine unreachable pair wasn't captured this run.

Families A (Orientation), E (History), F (Write path, dry-run only), and G (Meta/output-format) came back clean — no measured waste beyond documented behavior:
- `detect_circular_dependencies(scope:"type")` returns `unsupported_scope` in 28 tokens, matching its documented not-yet-implemented status.
- `get_semantic_diff` correctly reports `unresolved_ref` for a bad ref (17 tokens) and produced a well-structured 3-commit diff (1,957 tokens; 23 added, 2 removed, 28 changed, 1 breaking-public) for a real range.
- `validate_patch`'s five dry-run failure modes (non-compiling edit → CS1525 with `fixHint`/`suggestedInspection`; stale `baseVersions`; `unleased_body` with an actionable recovery message; identity edit; `runAnalyzers` true/false) each produced compact, actionable payloads (50–500 tokens).
- `set_output_format` round-tripped json → compact → toon on an identical `get_symbol` call correctly (288 / 226 / 219 tokens respectively — toon confirmed cheapest) and was restored to its original (`toon`) setting before finishing.

## Route table

| Route | Calls | Tokens |
| --- | --- | --- |
| search_index, 4 terms, 1 call | 1 | 496 |
| search_index, 4 terms, separate calls | 4 | 1,095 |
| get_symbol, large type, `members` + 3 targeted fetches | 4 | 3,137 |
| get_symbol, large type, `source`/`all` (whole fetch) | 1 | 9,288–9,311 |
| get_symbol, 3 small methods, batched (`symbols:[…]`) | 1 | 1,003 |
| get_symbol, 3 small methods, individual calls | 3 | 993 |
| get_symbol, one method, Automatic line format | 1 | 1,357 |
| get_symbol, one method, forced `-compact` | 1 | 1,350 |
| get_symbol, one method, forced `-exact` | 1 | 1,482 |

## Not exercised

- **Interface dispatch coverage** — the repo has exactly one non-trivial interface (`IKnowledgeStore`, one implementer). `get_references(direction:"implementations")` and `get_type_hierarchy`'s `derived` list were exercised but only against this single-implementer case; multi-implementer dispatch fan-out was not tested because the specimen doesn't have one.
- **A genuine `get_call_slice` miss** — the chosen unreachable pair turned out to be reachable; no unreachable-pair probe was captured.
- **A genuine `rename_symbol` name collision** (same name **and** same signature) — the probe collided on name only, which C# allows as an overload.
- **Multi-targeted `.csproj`** and **`.sln` (non-`.slnx`) entry point** — this repo has neither; `workspace_status`/`get_project_graph` were only exercised against a `.slnx`, 2-project, single-target solution.
- **Five tools carry no telemetry** and were judged by eye only: `ping` (trivial "pong" response, correct), `workspace_status` (correct root/solution/load-diagnostics reporting), `set_output_format` (correct, see Findings), `reload_workspace` (correct re-scan count), `get_retrieval_metrics` (the instrument itself — verified in Step 0: two identical `search_index` calls produced identical 112-token deltas, and an unfiltered snapshot showed strictly more calls than the `taskId`-filtered one).
