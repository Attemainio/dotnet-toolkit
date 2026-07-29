# dotnet-toolkit self-evaluation — 2026-07-29

Specimen: /mnt/c/Users/atte9/source/repos/dotnet-toolkit · dotnet-toolkit.slnx · 2 projects (DotnetToolkit.McpServer, DotnetToolkit.McpServer.Tests) · 101 files, 168 types · workspace loaded in 2.6s, no diagnostics
Census:   partial ✓ (SymbolStore, mishandled by search_index — see [bug] below) · nested ✓ (PatchSandbox.Failure, PatchDraftStoreTests.MutableClock) · overloads ✓ (weak: 2-candidate `GetSymbol` only) · generics ✗ (no repo-declared generic type/method found) · long members ✓ (FlowTools.GetCallHierarchy, 139 lines) · records ✓ · enums ✓ (6) · delegates ✓ (1: ValidationLadder.TargetedTestRunner) · multi-project ✓ (weak: 2 projects, one simple reference edge, no cycles)
Task id:  eval_selfeval_20260729T00 (all families)
Run cost: 63 tool calls, 25,632 tokens returned (this evaluation's own attributed total; excludes the 4 validate_patch calls that crashed before telemetry was recorded)

## Findings

[bug] `validate_patch`'s entire `draftId` amend path is broken — every amend crashes
  Call:      `validate_patch(draftId: "draft_...", edits: [...])` — tried with a fix-up edit, a
             newly-broken edit, and even `edits: []` (re-validate unchanged, which the tool's own
             description says is legal with a draftId)
  Observed:  All three variants return only `An error occurred invoking 'validate_patch'.` — no
             diagnostics, no draft, nothing recoverable. Reproduced 4 times across 2 different
             source drafts (one from a successful identity edit, one from a failed non-compiling
             edit). The identical edits submitted as a *fresh* call (baseVersions instead of
             draftId) succeed normally and return proper diagnostics.
  Expected:  Same success/diagnostic response shape as a fresh call, scoped to the draft's proposed
             text and merged baseVersions, per the tool's own description and CLAUDE.md.
  Condition: None specimen-specific — this reproduces on the simplest possible symbol
             (`ServerTools.Ping`, a 3-line method with no dependencies).
  Fix in:    `Tools/PatchTools.cs` (the draftId branch) or `Validation/PatchDraftStore.cs`
             (draft retrieval/merge). This is the single most consequential finding in this run:
             CLAUDE.md instructs "On failure, amend through the returned draftId — don't rebuild
             the patch" as the required workflow, and that workflow does not currently work at all.

[bug] `search_index` silently fails to find/locate a `partial` type
  Call:      `search_index(query: "class", modifiers: "partial", kinds: "class")`
  Observed:  0 results, even though `SymbolStore` (confirmed via `get_symbol` to be
             `public sealed partial`, split across `SymbolStore.cs` and `SymbolStore.Update.cs`)
             is the only partial type in the repo. Separately, any type-level hit for `SymbolStore`
             from `search_index` (via plain query, `groupBy:"none"`, or `groupBy:"namespace"`) comes
             back with no `file`/`line`/`endLine` at all — under `groupBy:"namespace"` it lands in a
             literal `namespace: "(unresolved)"` / `file: "(unresolved)"` bucket, the same placeholder
             legitimately used for BCL/external symbols with no repo file. `get_symbol` on the same
             symbolId correctly returns both `declarationSites`.
  Expected:  `modifiers: "partial"` should match a type declared `partial`; a type-level hit for a
             local, resolvable symbol should carry at least one representative file/line, not the
             external-symbol placeholder.
  Condition: A repo-declared partial type — present here, absent from most flat single-file specimens,
             which is presumably why this has gone unnoticed.
  Fix in:    `Tools/ContextTools.cs`'s `search_index` (or the underlying `SymbolStore`/index-build
             path) — the modifier filter and the type-level location resolution for partial types.

[warning] `get_scope` with `receiver` on a generically-typed variable pays ~4x per item in re-stated generic type text
  Cheap route:  `get_scope(file, line: 296)` (no receiver, all locals/params/members) — 1 call,
                775 tokens, 40 items ⇒ ~19.4 tokens/item
  Route taken:  `get_scope(file, line: 300, receiver: "rows")` where `rows` is
                `IReadOnlyDictionary<string, (string? FqName, string? Kind, string? DisplayString)>`
                — 1 call, ~3,146 tokens, 40 items ⇒ ~78.7 tokens/item
  Frequency:    Every LINQ extension-method entry (`Aggregate`, `Average`, `AsParallel`, `Chunk`, …)
                fully re-spells the receiver's generic arguments in its own `displayString`, and some
                overloads (e.g. `Aggregate`'s 3-type-param form) repeat the whole tuple type 3-4 times
                within one line. `receiverType` is already reported once in the header, so this is a
                restated-input cost paid per row instead of once.
  Fix in:       `Tools/FlowTools.cs`'s `get_scope`/`GetScope` extension-method rendering — shorten
                `displayString` for extension methods to elide the already-known receiver type (or a
                short alias), the way `get_call_hierarchy`'s own code comment (FlowTools.cs:288-290)
                already documents doing for call-tree nodes for exactly this reason.

[message] `origin: "external"` correctly separates BCL hits and reuses the same `file: "(unresolved)"` placeholder as the partial-type bug above, which is legitimate there (no repo file exists) but is worth distinguishing from the local-partial-type case in any fix, so a caller can tell "no file, because external" from "no file, because the index didn't resolve it."

[message] `ambiguous_symbol`'s candidate list was well-behaved on the only real ambiguity in this repo (2 overloads of the bare name `GetSymbol`, compact `displayString`s). The "forty fully-qualified candidates" stress case from the skill's guidance was not exercised — this repo has no method with more than 2 overloads sharing a bare name.

Family results with no findings: A (orientation — clean, no diagnostics), C (retrieval — `get_symbol`'s
`include` ladder, batching, subtractive `source` query, and `symbol_not_found` all behaved as documented),
E (history — `search_log` and `get_semantic_diff`, including an unresolvable ref, behaved as documented).

## Route table

| Outcome wanted | Cheap route | Expensive route |
| --- | --- | --- |
| Same `get_symbol` fetch, cheapest format | `toon` — 1 call, 114 tokens | `json` — 1 call, 156 tokens (+37%); `compact` — 1 call, 115 tokens (tied with toon) |
| Multi-term discovery query | `search_index(query: "validate patch symbol")` — 1 call, ~3,744 tokens (13 results across 3 terms) | 3 separate single-term calls — 3 calls, ~5,082 tokens for overlapping/fewer combined hits |
| What is callable on a collection-typed local | `get_scope(file, line)` no receiver — 1 call, 775 tokens | `get_scope(file, line, receiver:"rows")` — 1 call, 3,146 tokens for the same 40-item cap (see [warning] above) |
| Who calls this, one hop | `get_call_hierarchy(maxDepth:1)` — 1 call, ~270 tokens (part of the 541/2 calls) | `get_references` — 1 call, 326/2 ≈ 163 tokens; in this repo `get_references` was actually cheaper for a low-fan-out method, so the ladder's ordering is workload-dependent, not a fixed ranking |
| Ripple size only, no tree | `get_call_hierarchy(includeTree:false)` — 1 call, part of 541/2 | full tree at same depth — larger by construction; not separately isolated this run |
| Amend a failed/stale patch | *(no cheap route currently exists — see [bug] above; only a full resubmission via fresh `baseVersions` works)* | resubmit whole patch — works, but defeats the documented purpose of `draftId` |

## Not exercised

- **Generics** — no repo-declared generic type or method was found; `search_index(modifiers:"generic")`
  is not a valid modifier token (not in the tool's own list), so this cannot be probed that way either —
  worth a documentation check separately (not filed here as a tool bug since no generic specimen exists
  to confirm against).
- **Interface with several implementers** — `IKnowledgeStore` has exactly one implementer
  (`KnowledgeStore`); `get_type_hierarchy`/`get_references` dispatch-coverage over a real multi-implementer
  interface is untested here.
- **Large ambiguous_symbol payload** (dozens of candidates) — no bare name in this repo has more than 2
  overloads.
- **Multi-project cross-references / real cycles** — only 2 projects with one simple reference edge;
  `detect_circular_dependencies` returning `cycles: []` here is consistent with "no cycles" but was not
  confirmed against a solution that actually has one.
- **Five tools recording no telemetry**, judged only by eye, not measured: `ping`, `workspace_status`,
  `set_output_format`, `reload_workspace`, `get_retrieval_metrics` — all returned small, well-formed
  payloads in this run.
