# dotnet-toolkit self-evaluation — 2026-08-12

Specimen: `/mnt/c/Users/atte9/source/repos/dotnet-toolkit` · `dotnet-toolkit.slnx` · 2 projects
(DotnetToolkit.McpServer, DotnetToolkit.McpServer.Tests) · 151 files, 245 types · workspace loaded in
3.2s, no diagnostics, no failed projects.

Census: partial ✓ (`SymbolStore`, `OutlineBuilder`, `SymbolResolver`, `DevlogParser`, `CsFileMembership`) ·
nested ✓ (`SymbolStore` N5, `SemanticDiff.Result`, `TelemetryRecorder.PatchEvent`) · overloads ✓
(`Result` → 12 candidates) · generics ✓ (usage only; **no generic type declared in source**) ·
long members ✓ (`ContextTools.SearchIndex` L394, `ValidatePatch` L288, `GetCallHierarchy` L177) ·
records ✓ · enums ✓ · delegates ✗ · interface with several implementers ✗ (the only interface,
`IKnowledgeStore`, has exactly one) · multi-project ✓ (2, acyclic).

Task ids: `p_<family>_<name>_20260812**r2**`. The `r2` suffix is load-bearing — an aborted run earlier
today (17:01–17:14 UTC) had already burned the unsuffixed `p_<family>_<name>_20260812` ids, which
would have summed both runs into one row. Families: A orientation, B discovery, C retrieval,
D relations, D3 advice pairs, E history, F write path, G formats, X census, 3e guidance.

Run cost: ≈87 measured probe calls, ≈41,800 tokens returned, plus 11 unmeasured control calls
(`workspace_status` ×2, `ping`, `set_output_format` ×3, `reload_workspace`, `get_retrieval_metrics` ×4).

Instrument check: two identical `search_index` calls under novel ids both returned exactly 224 tokens
(non-zero, deterministic, landed on the `search_index` row); the unfiltered snapshot showed 271 calls
against 1 filtered. Every probe row in the final readback reads `calls: 1` (except the deliberate
5-call `p_C_singles5`) — no collisions, numbers below are sound.

## Findings

### [bug] Every record's declaration lease is the same constant, so `validate_patch` cannot detect a stale record declaration

    Call:      get_symbol(symbol: "sym_e8d5722edc616f78")   # Git.SemanticDiff.Result
               get_symbol(symbol: "sym_cd16432a9d53b7c1")   # Indexing.CallSlice.Result
               get_symbol(symbol: "sym_7603d2e2c6dee13e")   # Indexing.TypeEntry
    Observed:  all three → contentVersion "decl:e3b0c44298fc|…"
               e3b0c44298fc is the first 12 hex of SHA-256("") — the empty string.
               Six distinct records in three different files all return it.
               A class (DevlogParser → decl:79c7d21e13b4) and a method
               (FlowTools.GetTypeHierarchy → decl:db909e4d4ac0) hash correctly.
    Expected:  a hash of each record's own declaration text, distinct per record.

Second half of the reproducer, which turns this from cosmetic into a lost safety property:

    Call:      validate_patch(
                 baseVersions: {"sym_7603d2e2c6dee13e": "decl:e3b0c44298fc|refs:98814db332b0"},
                 edits: [{symbolId: "sym_7603d2e2c6dee13e",
                          find: "public sealed record TypeEntry",
                          replace: "public sealed record TypeEntry /* eval probe */"}],
                 applyOnSuccess: false)
    Observed:  succeeded: true, isSufficient: true, project_compile clean.
               Both halves of that baseVersions value belong to a DIFFERENT record
               (Git.SemanticDiff.Result). TypeEntry's own refs layer is f8d23edfe66a.
    Expected:  error: stale_base — the control does fire, since the same patch shape with
               "decl:aaaaaaaaaaaa|body:bbbbbbbbbbbb" on a method was correctly rejected.

    Condition: records only. Any repo using records for its models is affected.
    Fix in:    the declaration-hash path behind get_symbol's contentVersion (Fingerprint/*),
               for RecordDeclarationSyntax.

### [bug] `symbolId`s are emitted for symbols that `get_symbol` cannot resolve, and four distinct local functions share one id

Both `get_references`' contract ("An item's `symbolId` is a `get_symbol` target") and
`ambiguous_symbol`'s whole purpose (hand back a disambiguating handle) depend on these round-tripping.

    Call:      get_symbol(symbol: "ValidationLevel")
    Observed:  ambiguous_symbol, candidates: sym_5cd71ff80f9939a0 "ValidationLevel.ValidationLevel()",
               sym_f18b2925aafbbe5c "ValidationLevel"
    Call:      get_symbol(symbol: "sym_5cd71ff80f9939a0")
    Observed:  error: symbol_not_found          # the handle the tool just offered
    Also:      sym_f0c9636c3b2c6ee2 (a record copy-constructor from the "Result" ambiguity) → not_found
    Contrast:  sym_964a5104252a3bb1 (SymbolStore's hand-written ctor) resolves fine,
               so the defect is specific to compiler-synthesized members.

    Call:      get_references(symbol: "DotnetToolkit.McpServer.Output.Formats.Render")
    Observed:  four separate items, all displayString "Fail/3", all symbolId
               sym_583d7fbe40dcc1b0, at FlowTools.cs:71, :220, :371 and :534 — which are four
               DIFFERENT local functions, one each inside GetScope, GetCallSlice,
               GetCallHierarchy and GetTypeHierarchy.
    Call:      get_symbol(symbol: "sym_583d7fbe40dcc1b0")
    Observed:  error: symbol_not_found
    Expected:  one id per local function, each resolvable; or no id at all rather than a
               colliding one. As shipped, the id is simultaneously ambiguous and dead.

    Condition: synthesized record members; local functions. Both are ordinary modern C#.
    Fix in:    SymbolKey.IdOf / SymbolResolver's handling of synthesized and local symbols.

### [bug] A bare type name resolves ambiguously against the type's own constructor, breaking `dotnet-read`'s own cheap route

    Call:      get_symbol(symbol: "SymbolStore", include: "members")
    Observed:  error: ambiguous_symbol — candidates SymbolStore.SymbolStore(IKnowledgeStore)
               and SymbolStore. Same for WorkspaceHost.
               SymbolResolver and DevlogParser (static, no declared ctor) resolve first time.
    Expected:  the type wins. Its fully-qualified name ends at the bare name at a segment
               boundary; the constructor's does not, so an exact-suffix preference resolves it.
    Impact:    the cheap-route row "search_index(pathPrefix: <one file>) → get_symbol(symbol:
               "TypeName", include: "members")" costs two calls, not one, on every class with a
               constructor — which is most non-static classes in any repo.
    Fix in:    SymbolResolver.ResolveAsync's candidate ranking.

### [warning] `recentLog` is 57% of the default `get_symbol` response, on the tool that is 68% of all retrieval spend

    Cheap route:  get_symbol(include: "referenceCounts")       → 142 tokens
    Route taken:  get_symbol()  [default: referenceCounts + recentLog] → 330 tokens
                  (+188 tokens, same call count)
    Frequency:    get_symbol.calls = 1,762 unfiltered, 1,785,487 tokens = 68% of all
                  retrieval tokens across every session on this server.
    Why it is waste on a read pass: "why does this code look like this" routes to search_log in
    dotnet-read, not to get_symbol — so on a navigation or read fetch nothing branches on
    recentLog. It is load-bearing on a write pass, which is the conditional to preserve.
    Recommendation: keep referenceCounts in the default; move recentLog to an opt-in component
    (it already is one — it just should not be in "standard"), or serve it only under
    intent/include values the write path uses. Not a blunt removal.
    Fix in:      get_symbol's "standard" include set.

### [warning] `intent: "logic"` and `"edit"` override the `read` column without consulting `D`, advising fetches that save nothing

    Cheap route:  get_symbol(source: "full")  on ContextTools.FormatTypedConstant → 217 tokens
    Route taken:  get_symbol(source: "code")  — what read:"code" advised → 217 tokens
                  (+0 tokens; byte-identical responses)
    Cause:        that hit's shape is P1-L11-O1-C2 — no D, i.e. zero doc-comment lines, so
                  "code" and "full" are the same lines by construction. The evidence was already
                  in the same row the label was computed from.
    Census:       under intent:"logic", 4 of the 6 hits labelled "code" had no D at all.
    Recommendation: suppress "code" when D is absent, and fall through to whatever the
                  no-intent path would have said. Under no intent the label correctly did not
                  fire on any of these hits — only the intent override is miscalibrated.
    Fix in:      ReadAdvice, the intent-override branch.

### [warning] `intent: "edit"` collapses `read` to a constant `all` on every row, and `all` is the most expensive lease available

    Observed:    the same 12-hit query under intent:"edit" labels all 12 rows "all" — including a
                 1-line field (PatchDraftStore._drafts, L1) and a 1-line method (Ids.Draft(), L1).
                 A column with one value on every row carries no information (3b: constant) and
                 is not hoisted the way the legends are.
    Cheap route: get_symbol(include: "bodyOutline") → 192 tokens
    Route taken: get_symbol(include: "all")         → 2,133 tokens   (+1,941, 11×)
                 Both return contentVersion "...|body:2f64d2bf8eb8" — the identical body layer —
                 and a find/replace validate_patch built on the 192-token fetch validated clean at
                 project_compile. source:"code" (1,785) also leases it.
    Frequency:   search_index.calls = 841 unfiltered; every edit-intent search pays the constant
                 column, and any caller who follows "all" literally pays the 11× on top.
    Recommendation: under intent:"edit", either hoist the constant into the header, or make the
                 label the cheapest include that carries the body layer rather than the widest.
    Fix in:      ReadAdvice's edit-intent branch; and the row now added to dotnet-write's table.

### [warning] Analyzer suggestions are not scoped to the change, so a rename reports findings from lines it never touched

    Call:      rename_symbol(symbol: "sym_67f755fdb114beee", newName: "GetTypeHierarchyRenamed",
                             applyOnSuccess: false)
    Observed:  5 suggestions, all in WorkspaceIntegrationTests.cs at lines 803, 919, 1201, 1327,
               1336 — while the rename touched FlowTools.cs 508–624 and that file's lines
               2391–2500. 5 of 5 are pre-existing and unrelated to the change.
    Cost:      ~100 tokens here; it scales with the size of the touched *file*, not the change.
    Note:      notAssessed already says "analyzers covered 2 changed document(s)", so the scope is
               documented — but the caller still has to decide, per suggestion, whether it is
               theirs. Filtering to the changed spans (or tagging each suggestion in-change vs
               pre-existing) is the fix.
    Fix in:    the analyzer-reporting path shared by validate_patch and rename_symbol.

### [message] `ambiguous_symbol` candidate lists are inflated by compiler-synthesized record members

`get_symbol(symbol: "Result")` → 248 tokens, `totalCandidates: 12`, `truncated: true`, of which only 4
are the record types the caller almost certainly meant; the other 8 are primary and copy constructors,
and (per the second finding above) their ids do not resolve. The payload is otherwise well built —
`sharedPrefix` is hoisted, the list is capped at 10 and says so.

### [message] The `read` column's empty cells are not free, contrary to how `dotnet-read` describes them

Measured: identical query at `limit: 50`, `include` default 2,390 tokens vs `include:
"shape,refs,modifiers"` 2,315 — so the whole column costs **75 tokens** to deliver **4** usable labels;
46 rows paid `,""` for nothing, ~32 tokens of the 75. TOON's tabular schema cannot omit a cell once any
row fills it, so the skill's "absent means the default fetch is already right, which is why the column
costs nothing on an ordinary result" is true only when *no* hit is labelled (then the column and its
legend are dropped entirely, correctly observed on two census probes).

This is a `[message]` and not a `[warning]` because the column is strongly net-positive: following one
`read: mem` label measured 274 tokens against 1,995 for `source: "full"` on the same type — a single
followed label repays the whole page's column cost ~23×. Only the wording needs fixing.

### [message] `stale_base` returns no `draft`, while `validate_patch`'s description says every unapplied result does

Observed on both `validate_patch` and `rename_symbol`. The **code is right** — a patch built on moved
content cannot be amended, since its line coordinates are against content that no longer exists — so
this is doc drift, and belongs to `dotnet-consistency` rather than being fixed as a tool bug. Contrast
`unleased_body`, which correctly *does* return a draft plus the exact version to resend: that payload is
the best error message in the surface, naming the cause, the fix, and handing back the value needed.

### [message] A rename onto an existing name with a different arity silently creates an overload set

`rename_symbol(GetTypeHierarchy → GetCallHierarchy)` reported `succeeded: true` through
`dependent_compile`. It is correct C# — the two signatures differ — but the caller asked for a rename
and got an overload, with nothing in the response saying the new name was already taken. A genuine
same-signature collision *is* caught well: `_log → _store` produced CS0102/CS0229/CS8618 distilled from
11 raw diagnostics to 3 root causes with a usable `nextAction`. A `nameAlreadyExists` note would close
the gap.

### [message] `get_scope`'s `origin` restates `kind` when there is no receiver

Without `receiver`, the 15 rows mapped Local→local, Parameter→parameter, Method→member, Type→type — a
1:1 restatement. With `receiver` the column earns its place immediately (empty for the receiver's own
members, `inherited` with `definedIn` for the rest), so the fix is conditional suppression, not removal.
Cost is small (~15 tokens/page against `get_scope`'s 30 lifetime calls) — ranked last deliberately.

### [message] `detectedChanges` on a field rename omits `symbolId` on every entry

The `_log → _store` rename returned 5 entries with no `symbolId` at all, the last being a bare
`changeKinds: [removed]` with no `declarationSites` either — nothing the caller can act on or fetch.
The method rename by contrast carried `symbolId` + `previousSymbolId` on its first entry.

## Route table

| Outcome wanted | Cheap route | Expensive route |
|---|---|---|
| Caller list, names only (25 callers) | `get_call_hierarchy(maxDepth: 1)` — 1 call, **395** | `get_references` — 1 call, **2,255** (5.7×) |
| A type's surface | `get_symbol(include: "members")` — 1 call, **274** | `source: "full"` — 1 call, **1,995** (7.3×) |
| A body lease for an edit | `include: "bodyOutline"` — **192** | `include: "all"` — **2,133** (11×); `source: "code"` **1,785** |
| A member's body, hit has no `D` | `source: "code"` — **217** | `source: "full"` — **217** (no saving) |
| 5 homogeneous records | batch — 1 call, **680** | 5 singles — 5 calls, **655** (batch: −4 calls, +25 tokens) |
| Blast radius only | `includeTree: false` — **54** | full tree at depth 1 — **395** |
| Same 10 hits, grouping | `groupBy: "none"` — **554** | `"namespace"` **642**, `"file"` **656** |
| Summary + location for 10 hits | `include: "summary:full"` — 1 call, **515** | a `get_symbol` per hit |
| Same response, encoding | `toon` — **274** | `compact` **363** (+32%), `json` **504** (+84%) |

Applied to the skills in this pass:

- **`dotnet-write`** — new row on the body lease, and the existing `include: "all"` row reworded to say
  *serves the body* rather than naming `all`. Superseded in place, not appended.
- **`dotnet-read`** — the `get_call_hierarchy` vs `get_references` row now carries three fan-in data
  points (8 → ~1/3, 25 → ~1/6, 105 → ~1/8) and states *why* the gap widens, so the crossover is usable
  near the edge instead of being a bare ratio.
- **`dotnet-read`** — new row for the constructor-ambiguity bug above, so a caller pays one call
  instead of two until the resolver is fixed.

Not applied, reported only: `dotnet-write`'s **step 1 of the loop** still says `get_symbol(include:
"all")` unconditionally. The new table row qualifies it, but the two now sit at slightly different
altitudes and step 1 is the one people follow. Worth a deliberate edit in a separate pass — rewriting
prose in the same pass that judged it leaves nobody to check the judgement.

## Guidance reasoning (3e)

Sampled, not exhaustive — stated plainly so an unlisted file is not read as a passed one.

- **Always-loaded** — `CLAUDE.md`, `.claude/rules/dotnet-index.md`: checked, clean. Reasoning is present
  and stays one clause ("Text search gives **wrong answers** on C#… cannot see interface, virtual, or
  delegate dispatch"), which is what the size budget requires.
- **Skills** — `dotnet-read`, `dotnet-write`, `dotnet-selfeval` + `analyses.md`: checked, clean, and the
  strongest tier. `dotnet-write` attaches a consequence to nearly every directive ("so the lines you
  never saw are deleted"; "the ladder runs byte-for-byte identically either way"; "parse cannot tell a
  harmless reformat from `using Nope.Missing;`"). No bare imperatives found.
- **Standards** — `standards/index.md`: checked, clean, and unusually good: it has an explicit *"Why the
  'When' column is phrased the way it is"* section that states the failure mode (a cell that cannot be
  matched against source gets silently skipped or over-loaded). Individual standard files not read this
  pass.
- **Agents** — **not checked this pass.**
- **Tool manuals** — `docs/tools/get_symbol.md` sampled: dense with reasoning throughout (e.g. `xmlDoc`
  explains why suppression is judged on lines kept — "half a summary read as prose is not the summary").
  One row is a bare *what*: **`mechanicalFacts`** describes its shape and null behaviour but never says
  when a caller would want it over `bodyOutline`, so there is nothing to decide from. The other 14
  manuals were not read.
- **Hook messages** — `GuardCsRead`, `GuardCsBashRead` (the latter fired on me mid-run, which is the
  honest test): checked, clean, and correctly built for a reader already committed to another plan.
  They say why the tool path is better, name the skill rather than duplicating it ("a copy here would
  drift"), and pre-empt the obvious workaround ("Searching the tree rather than naming a file reads
  MORE, not less, so it is not a sanctioned way around it") — which is exactly the failure a bare
  imperative produces.

No `[warning]` earned in 3e: no missing reason in a checked file was traced to a wrong route taken
during this run.

## Not exercised

- **Census features absent from this specimen**: delegates (no `delegate` declared in source, so
  kind-specific response shapes for them are untested); an interface with more than one implementer
  (`IKnowledgeStore` has exactly one, so `get_type_hierarchy`'s `derived` list, its `limit`/`truncated`
  path, and `get_references(direction: "implementations")` fan-out were each exercised at n=1); a
  generic type or method *declared* in source (only usages of BCL generics); a multi-targeted `.csproj`;
  a source generator; a solution root holding several repositories (`get_semantic_diff`'s `repo`
  argument untested); more than 2 projects, so `get_project_graph` and
  `detect_circular_dependencies` were exercised on a trivial acyclic pair and the cycle-reporting path
  never ran.
- **Six tools record no telemetry**, so their cost below is by eye, not measured: `ping` (~10 tokens),
  `workspace_status` (~70), `set_output_format` (~8), `reload_workspace` (~15),
  `get_retrieval_metrics` (deliberately unrecorded, so it cannot perturb the deltas it computes), and
  `set_hook_guards`. All were exercised for correctness except `set_hook_guards`, which was
  **deliberately not called** — its effect outlives its response, and suspending the guards would have
  let the rest of this run make raw `.cs` reads that bypass the tools being measured. Checked by
  reading its docs instead; measuring the unguarded route is `dotnet-performance`'s job.
- `validate_patch` was never run with `applyOnSuccess: true`, and no `.cs` file was modified.
  `set_output_format` was restored to `toon` before finishing.
