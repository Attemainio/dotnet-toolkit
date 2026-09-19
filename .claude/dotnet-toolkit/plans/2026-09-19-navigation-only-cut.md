# dotnet-toolkit: drop the write path, make the index follow edits, hand code text back to `Read`

**Date:** 2026-09-19
**Input:** `FluidScript/.claude/dotnet-toolkit/feedback/2026-09-19-tool-response-observations.md` (the
defect-sweep report: ~420 `validate_patch` calls, ~360 `get_symbol` calls across tiers 2–4), checked
against this repo's source at `ac929bb`.
**Status:** decisions made; implementation plan for another session. Nothing here has been applied.

---

## 1. Decisions

| # | Proposal | Decision | Deciding reason |
|---|---|---|---|
| 1 | Remove `validate_patch` | **Yes.** Remove it with everything that exists only for it: drafts, `baseVersions` leases, the compile ladder, analyzer runs, the write guards, the write-time checklist's patch-specific bullets. | The report's failure classes are the design, not bugs in it. Line-anchored edits, the three pre-flight refusals (`unheld_symbol`, `unleased_body`, `stale_base`) and the 38-line success response are each what the lease protocol *is*. Fewer than half of 382 calls did what they were sent to do. `Edit` has no anchoring failure class. |
| 2 | After a write, the index must follow automatically | **Yes — and the mechanism is the periodic sweep, not only a hook.** The SQLite symbol store never subscribed to file changes at all (§2.2). Fix that first; the hook then only makes it *immediate*. | With `validate_patch` gone, `Edit` is the write path. Today an `Edit` updates the live Roslyn solution within ~2 s but `search_index`, `sym_` ids, `get_references` edges and hierarchies stay at the last rebuild until someone calls `reload_workspace`. That was masked because the apply path called the rebuild itself. |
| 3 | Navigation-only toolkit | **Yes, with one write tool kept: `rename_symbol`, stripped** to "Roslyn rename → write files → refresh index". No `baseVersion`, no ladder, no intent, no dev-log entry. | A solution-wide semantic rename is the one edit `Edit` cannot do. It needs none of the validation stack once `dotnet build` is the gate. |
| 4 | Remove source text from `get_symbol` | **Yes.** Also remove `includeBodies` from `get_references`, the large-source `ResponseGuard`, the `source` grammar (`:code`, `@lines`, `-modifiers`, `-exact/-compact`), and `search_index`'s `read` advice column and `intent` argument. `get_symbol` becomes a locator: `declarationSites` + facts. Text is read with `Read(file, offset, limit)` or `sed -n`. | Most of the 693 K chars were `get_symbol` used as a line-numbered file reader because a patch needed current line numbers. That demand disappears with line anchoring. What is left (whole-symbol-across-partials, doc-comment stripping) does not pay for a rendering subsystem, a grammar and a guard. |
| — | Development log (`search_log`, `recentLog`, `feature_log`) | **Remove.** Consequence of #1: nothing writes it any more. | A read-only log whose last entry is dated the day of the cut would be answered with authority forever — the same reason run reports are gitignored. |
| — | Post-write `dotnet build` hook (report §6) | **No.** | Fires on every `Edit`; 33 edits × 6.6 s is three and a half minutes of hook time for information the agent gets from one build at the end. The refresh hook already gives Roslyn the new text. |
| — | A `get_diagnostics` tool (compile errors from the in-memory compilation) | **Deferred.** Not part of this cut. | It is the only thing the ladder was actually valued for (dependent breaks before a build), and it would cost one call rather than a protocol. Revisit after a month of metrics on the navigation-only surface. |
| — | Cache corruption (report 1.3) | **Not in this cut**, beyond capping the quarantine files. Hypothesis recorded in §2.4. | `ac929bb` added detection, quarantine and rebuild; the cause is still unknown. |

Net effect on the server: roughly a fifth of `src/` and a quarter of the tests go away
(`PatchTools` 825 lines, `Validation/` 1 787, source rendering in `ContextTools` ~600, `RenameTools`
593 → ~250, `ResponseGuard`, `SourceQuery`, `ContentVersion`, `ReadAdvice`, two guards, `Devlog/`,
`FeatureLogStore`, patch telemetry). One new capability lands: scoped index refresh on file change.

---

## 2. What the report says, checked against the source

### 2.1 The failures are structural

- **Line anchoring** (report §6, §8.6): eight failed rounds in tiers 3–4 alone were a range one line
  short of the `}` or one past it. `declarationSites.endLine` never said whether it *is* the brace.
  No fix short of string anchoring removes the class, and `Edit` already is string-anchored.
- **The pre-flight refusals are the lease protocol working as designed**: `unheld_symbol` (2.1),
  `unleased_body` (8.1), `stale_base` on the containing type after any sibling edit (8.2). All three
  carry the versions the caller is then asked to send back. 127 of 382 calls (33 %) carried no edit.
- **Response noise** (§3): `detectedChanges[]`, `notAssessed[]` boilerplate, `checks.levels[]`,
  generic `nextAction`/`fixHint` — all by design, ~200 lines per success.
- **Two real defects** — 1.1 (trailing `\n` in `newText` inserts a blank line) and 1.2 (edits added
  to an existing `draftId` mis-anchor) — are in code this plan deletes rather than fixes.

### 2.2 Confirmed from the source: the symbol store does not follow edits

This is the finding that makes item 2 a prerequisite for item 1, not a follow-up.

- `search_index` answers **only** from the SQLite `SymbolStore`
  (`src/DotnetToolkit.McpServer/Tools/ContextTools.cs:923`).
- A `sym_…` id resolves through `SymbolStore.FqNameFor` (`ContextTools.cs:2567`). A stale store
  therefore returns `symbol_not_found` for an id the tool itself just handed out — report 8.3 and the
  "`symbol_not_found` for a fresh id" symptom, both explained without any cache corruption.
- `SymbolIndexBuilder.RebuildAsync` runs at startup (`Program.cs:93`), on `reload_workspace` and the
  control channel's `reload` (`ServerTools.cs:186`, `ControlServer.cs:120`), and after a
  `validate_patch`/`rename_symbol` apply (`PatchTools.cs:299`, `RenameTools.cs:242`). **Nothing else
  triggers it.** It does not subscribe to `ProjectIndex.FilesChanged`; only `WorkspaceHost` does
  (`WorkspaceHost.cs:145`).
- So after an external `Edit`: the mtime sweep (`ProjectIndex.EnsureFreshAsync`, 2 s quick / 30 s
  full, run before every query) swaps the new text into the live solution via
  `WorkspaceHost.ApplyChangedPaths`, and `get_symbol` **by name** is correct. `search_index`, every
  `sym_` id, `get_references` edges, `get_call_hierarchy`, `get_type_hierarchy` and `referenceCounts`
  are frozen at the last rebuild. `reload_workspace` repairs it with a **full MSBuild reload** — the
  seven reloads the report counts as "cache-corruption recoveries" were at least partly this.
- Every existing rebuild is whole-solution (`RebuildAsync` walks every project and document, then
  `SymbolStore.ApplyIncremental` diffs rows by fingerprint). There is no document-scoped path.

### 2.3 Smaller findings, checked

- **8.4 `kinds: ["Method","Property","Field"]` rejected**: the parameter is a comma/space-separated
  *string*; a JSON array arrives as the text `["Method",…]` and matches no kind. Cheap parser fix.
- **8.4 string literals not found**: `search_index` indexes names only (`symbols_fts`), by design.
  The response should say so instead of listing each English word under `termsWithNoHits`.
- **8.4 `LoopDrop` property not found while grep found four hits**: almost certainly §2.2 — the
  property was added during the session and the store had not been rebuilt.
- **8.6**: `declarationSites.startLine` includes the leading `///` comment; `endLine` is the closing
  brace line. True today, stated nowhere. Once `Read` ranges are the read path this must be a
  documented, tested contract.
- **8.5 (CS1620 hint)** and **2.2 (location caps)**: moot with the ladder gone. A `dotnet build`
  reports every location.

### 2.4 Cache corruption (report 1.3)

`ac929bb` (2026-09-19, current head) added `StoreCorruption` classification, `PRAGMA quick_check` on
the index pass, quarantine to `knowledge.db.corrupt-<timestamp>`, `feature_log` salvage
(`StoreRecovery`), and `ToolFailureReporting` so the failure names the cache. The **cause** is not
found. One hypothesis worth recording, not asserted: every server process on a repo opens the same
`knowledge.db`. A Claude Code session and a Codex session on the same checkout, or two Claude
windows, run two servers over one SQLite file on DrvFs (`/mnt/c`), where POSIX locking over 9p is
unreliable, and the telemetry purge-at-startup already assumes one process per file. Report 1.3's
nine corruptions in one session coincide with a session that also spawned subagents. Out of scope
here; §5 Phase 5 adds the quarantine cap and a pid line in the log so the next occurrence can be
correlated.

---

## 3. Target surface after the cut

### 3.1 MCP tools

| Tool | Outcome | What changes |
|---|---|---|
| `search_index` | keep, changed | `intent` argument and `read` column removed (`ReadAdvice`); `shape` (line/member counts) stays because it sizes the `Read` that follows. Accept a JSON-array string for `kinds`/`modifiers`. "Names only" note when nothing matches. |
| `get_symbol` | keep, changed | `source` argument, `contentVersion`, `recentLog` and the lease layers removed. Response = skeleton (`kind`, `origin`, `containingType`, `declarationSites{file,startLine,endLine,signatureLine}`, `displayString`, `modifiers`) + opt-in `xmlDoc`, `members`, `attributes`, `baseType`, `interfaces`, `usings`, `bodyOutline`, `mechanicalFacts`, `referenceCounts`. Description says: text comes from `Read(file, offset, limit)` using `declarationSites`. `symbols[]` batch stays. |
| `get_references` | keep, changed | `includeBodies` and `content` removed; `contentVersion` extra removed. |
| `get_call_hierarchy`, `get_call_slice`, `get_scope`, `get_type_hierarchy`, `get_project_graph`, `detect_circular_dependencies`, `get_semantic_diff` | keep | untouched (they never carried source text or versions). |
| `rename_symbol` | keep, rewritten | `(symbol, newName, apply=false, renameOverloads, renameInComments, renameInStrings, taskId)`. Dry run lists files, reference counts, new `symbolId`, the "file named after the type was not renamed" hint. `apply: true` writes, adopts text, refreshes the store for the changed files, and says that compilation was not checked. |
| `workspace_status`, `reload_workspace`, `ping`, `set_output_format`, `set_hook_guards`, `get_retrieval_metrics` | keep | `workspace_status` loses the "edits absent from the development log" sentence; `get_retrieval_metrics` loses the `patch_events` fold-in. `reload_workspace` stays as the manual big hammer. |
| `validate_patch` | **removed** | |
| `search_log` | **removed** | Phase 4. |

Contract: `ctx-contract/4.0` at Phase 2, `4.1` at Phase 3, `4.2` at Phase 4. Plugin version `2.0.0`.

### 3.2 Hooks (`hooks/hooks.json`)

| Hook | Outcome |
|---|---|
| `guard-cs-edit` (PreToolUse Edit/Write/NotebookEdit/apply_patch, hard deny) | **removed** |
| `guard-cs-bash-write` (PreToolUse Bash, hard deny) | **removed** |
| `hint-write-checklist` (PreToolUse on `validate_patch`) | **re-targeted** → `hint-cs-write-checklist`: PreToolUse on Edit/Write/NotebookEdit/apply_patch, fires once per session on the first write to a compiled `.cs` file (same `CsFileMembership` gate the edit guard used, same temp-marker dedupe). Keeps the four security/testing bullets; drops the two `validate_patch` bullets. Never denies. |
| `hint-reload-new-cs-file` (PostToolUse Write/apply_patch) | **generalised** → `refresh-cs-files`: PostToolUse on Edit/Write/NotebookEdit/apply_patch **and** Bash. Sends `refresh <paths>` over the control channel and waits for the scoped refresh (bounded). Silent on success; injects context only when a reload was started (new/deleted file) or the channel is unreachable for a new file. |
| `guard-cs-read`, `guard-cs-bash-read` (advisory) | **narrowed**: advise only on a whole-file read (`Read` without `offset`/`limit`; `cat`/`type`/`less` on a `.cs`). A ranged `Read` or `sed -n a,bp` is now the *intended* path and passes silently. Forced-block mode (`set_hook_guards(state:"block")`) must also allow ranged reads, or the MCP performance probe cannot read code at all. |
| `meter-tool-call`, `track-subagent-start/stop` | keep |

Nine hooks become seven.

### 3.3 Skills, agents, rules

| File | Outcome |
|---|---|
| `.claude/rules/dotnet-index.md` | Router rows unchanged. Last section rewritten: Edit/Write are the write path; a PostToolUse hook refreshes the index; whole-file reads get an advisory. |
| `skills/dotnet-write/SKILL.md` | Rewritten to ~80–100 lines: workspace_status; standards step (kept verbatim); write with Edit/Write, string-anchored; new file → Write, then wait for `workspace_status: loaded`; `rename_symbol`; blast radius via `get_references`/`get_call_hierarchy` before a signature change; `dotnet build` is the gate, run after a burst of edits, not after each; failure modes (`limitedBy: stale`, file not in solution). |
| `skills/dotnet-read/SKILL.md` | `get_symbol` section, cheap-route table, "Reading responses": locate → `Read` range. `search_log` section removed. |
| `skills/dotnet-explore`, `agents/dotnet-explore.md`, `agents/dotnet-code-review.md`, `skills/dotnet-review` | Replace "fetch `source:code`" guidance with "`get_symbol` then `Read` the `declarationSites` range". Explore and review agents already hold `Read`. |
| `skills/dotnet-guard-audit`, `agents/dotnet-guard-redteam.md` | **removed** — they red-team hooks that no longer exist; bypassing an advisory has no stakes. |
| `skills/dotnet-performance` + the three probe agents | MCP probe gains ranged `Read` (it must, to read code); methodology text updated. The measured question becomes "locate via MCP + ranged Read" vs "grep + Read". |
| `skills/dotnet-selfeval` | Drop the `validate_patch` analyses and the "read column" pricing analysis. |
| `skills/dotnet-consistency` (+ `drift.md`, `harness-compliance.md`) | Update the authoritative file lists; then *run it* (Phase 6). |
| `skills/dotnet-init` | One sentence about `validate_patch` as the write path; verify/uninstall docs mention it. |
| `standards/*.md` | Six one-line mentions of `validate_patch`/`dotnet-write` — reword, standards themselves unchanged. |

### 3.4 Docs and manifests

Delete `docs/tools/validate_patch.md`, `docs/tools/search_log.md`. Rewrite `docs/tools/get_symbol.md`
(two selectors → one; add the `declarationSites` line contract), `docs/tools/rename_symbol.md`,
`docs/design/hooks.md` (sections for the removed/renamed hooks), `docs/design/architecture.md`
(subsystems: `Validation` gone, refresh path added, "Changing the tool surface"). Prune
`docs/design/route-table-findings.md` (42 mentions), `docs/design/agents.md`,
`docs/examples/compiler-aware-navigation.md`, `docs/install/*.md`, `docs/benchmarks.md`. `README.md`,
`CLAUDE.md` ("What this is": no development-log tools; compact instructions: no `draftId`/
`validate_patch`), `.claude-plugin/plugin.json`, `.codex-plugin/plugin.json`,
`.claude-plugin/marketplace.json` descriptions ("guarded edits", "validated against the compiler
before they touch disk" are no longer true). `.claude/settings.json` drops the `search_log`
permission. `.editorconfig` has one comment mentioning the patch analyzers.

---

## 4. The refresh design (decision #2, in detail)

Principle: **the sweep is correctness, the hook is immediacy.** A `git pull`, an external editor or a
Bash write the scanner does not recognise must all converge without any hook firing; the hook only
removes the up-to-2 s window and the "first query pays the sweep" latency for the common case.

### 4.1 Server side

1. **`ProjectIndex.RefreshPathsAsync(IEnumerable<string> paths) → IReadOnlyList<string> changedRel`.**
   Targeted version of the sweep: for each `.cs` path under root, stat it; deleted → remove from the
   index, structural; new → parse and add, structural; mtime/length moved → re-parse, non-structural;
   unchanged → skip. Raise `FilesChanged(changed, structural)` exactly as `SweepAsync` does (factor the
   per-file parse-and-store out of `SweepAsync` and share it). A `.csproj`/`Directory.Build.*`/`.sln*`
   path raises `ProjectFilesChanged`. Return what actually changed.
2. **`SymbolIndexBuilder` becomes a single-worker queue.** Replace the `_running` compare-exchange
   with one serial worker (a `Channel<Job>` or `SemaphoreSlim` plus a coalescing pending set) so a
   full rebuild and scoped refreshes never interleave, and a scoped job queued during a full rebuild
   still runs after it (cheap, and it removes any argument about whether the full pass saw the text).
3. **`SymbolIndexBuilder.RefreshDocumentsAsync(IReadOnlyList<string> relPaths) → RefreshResult`.**
   Wait for the workspace (`GetSolutionAsync`); map paths to `DocumentId`s via
   `solution.GetDocumentIdsWithFilePath`; a path with no document means "new file, reload pending" —
   report it, do not guess. Expand the document set to **partial siblings**: for every declaration in
   a changed document, `symbol.DeclaringSyntaxReferences` names the other parts' documents; index them
   too, or the row for a partial type is computed from one part and its hashes churn. Run the existing
   `IndexDeclaration`/`IndexTopLevelStatements` over that document set (factor the per-document loop
   out of `RebuildAsync`). Then:
4. **`SymbolStore.ApplyIncrementalScoped(rows, edges, facts, scopeFiles)`.** Upsert `rows`; delete
   rows whose *every* `declaration_sites` entry lies in `scopeFiles` and that are absent from `rows`;
   replace outgoing `reference_edges` of every symbol in scope; touch nothing else. For a symbol id
   that *disappeared* from scope (rename or delete), edges from other documents still point at it:
   query `reference_edges` by `to`, and if the referencing documents number under a bound (say 50)
   re-index them in the same job; above it, queue a full rebuild. This keeps `get_references` honest
   after a rename without a full pass on every edit.
5. **Subscribe the builder to `ProjectIndex.FilesChanged`**: non-structural → queue
   `RefreshDocumentsAsync(paths)`; structural → the workspace reload already runs, then `Start()` (the
   full rebuild; `RebuildAsync` awaits the *new* `_loadTask` because `TriggerReload` swaps it
   synchronously — no new event needed). This one subscription is what makes `git pull` and
   unrecognised Bash writes converge.
6. **Control channel: `refresh <json-array-of-paths>`** (prefix command like `meter`). Calls
   `RefreshPathsAsync`; if structural → `TriggerReload()` + `indexBuilder.Start()` and answer
   `ok:reload started (<n> new/deleted)`; else await `RefreshDocumentsAsync` with a budget of ~10 s
   and answer `ok:refreshed <symbols> symbols in <files> files` or `ok:refresh queued` on budget
   exhaustion. `ControlClient.SendAsync` gets a per-command response timeout (the current 8 s is too
   short for this one).
7. **`workspace_status`** gains one line: `index: … (last refresh <n>s ago, <m> queued)` — so a
   caller can tell "queued" from "done" without guessing.

The `limitedBy: stale` read backstop (`IsBehindDiskAsync`/`DiskDrift`) stays; after this it should
almost never fire.

### 4.2 Hook side (`Hooks/CsFileRefresh.cs`, replacing `ReloadHint.cs`)

- Path extraction per tool: `Edit`/`Write`/`NotebookEdit` → `tool_input.file_path`; `apply_patch` →
  `ApplyPatchCommand.Parse` (all four change kinds, not just `Add`); `Bash` →
  `BashCommandScanner.Segments` + `CdTarget` + `FindWriteTargetCsArgument` per segment (existing
  code), plus a `git checkout|switch|pull|merge|rebase|reset|stash|apply|cherry-pick|revert` detector
  that sends the existing `rescan` instead (full sweep; `FilesChanged` does the rest).
- Filter to `.cs` (and project files); nothing to do → plain allow, no output.
- Send `refresh`; on `ok:refreshed` → plain allow, **no `additionalContext`** (33 edits must not
  inject 33 notes). On `ok:reload started` → context: "workspace reload started for <file>; call
  `workspace_status` before `get_symbol`/`search_index` on it". On channel unreachable → context only
  for a *new* file (the old reminder); otherwise silence, the sweep converges.
- `hooks.json` timeout 15 s for this hook. It never blocks the edit — it is PostToolUse.

### 4.3 Why not a filesystem watcher

Already settled in `CLAUDE.md`: inotify does not fire on `/mnt/*`. The hook-plus-sweep design is the
watcher substitute; don't "fix" it into one.

---

## 5. Implementation plan

Each phase is one commit on `main`, with `dotnet test` green, `scripts/build-plugin.sh` run, and
`dotnet dist/DotnetToolkit.McpServer.dll doctor --no-probe` passing **before** the commit, `dist/`
committed in the same commit. Phases are ordered so the tree is always releasable: Phase 1 is
additive and must land before Phase 2 makes `Edit` the write path.

### Phase 1 — the index follows disk (additive, no contract change)

**Server**
- `Indexing/ProjectIndex.cs`: `RefreshPathsAsync`; factor per-file parse-and-store out of `SweepAsync`.
- `Indexing/SymbolIndexBuilder.cs`: serial worker; `RefreshDocumentsAsync`; partial-sibling
  expansion; subscribe to `FilesChanged`; factor the per-document indexing loop out of `RebuildAsync`.
- `Store/SymbolStore.Update.cs`: `ApplyIncrementalScoped`; a query for `reference_edges` by `to`.
- `Control/ControlCommands.cs`, `Control/ControlServer.cs`: `refresh`.
- `Hooks/ControlClient.cs`: per-command timeout.
- `Hooks/CsFileRefresh.cs` (new, replaces `ReloadHint.cs`); `Hooks/HookCli.cs` switch entry
  `refresh-cs-files`; `Hooks/ApplyPatchCommand.cs` unchanged.
- `Tools/ServerTools.cs`: the `workspace_status` refresh line.
- `hooks/hooks.json`: replace the `hint-reload-new-cs-file` entry with `refresh-cs-files` on
  `Edit|Write|NotebookEdit|apply_patch`, and a second PostToolUse entry on `Bash`.

**Tests**
- `ProjectIndexTests`: `RefreshPathsAsync` on changed / new / deleted raises `FilesChanged` with the
  right `structural` flag and returns only what moved.
- `WorkspaceIntegrationTests` (the fixture is already a temp copy, `SampleSolutionFixture` line 63):
  write a new method into a fixture file with `File.WriteAllText`, call
  `indexBuilder.RefreshDocumentsAsync`, assert `search_index` finds it, its `sym_` id resolves in
  `get_symbol`, and `get_references` sees the new call edge; restore the file (the pattern
  `RestoreSpinBodyAsync` uses today). Scoped refresh must leave the symbol count of *other* files
  unchanged. Editing one part of the fixture's partial class keeps both `declarationSites`. Removing a
  method that other files call re-indexes those files (the bounded `to`-edge path).
- `HookGuardTests`: the refresh hook extracts paths from each payload shape (Edit, Write,
  apply_patch with Update/Delete/Move, Bash `sed -i`, Bash `git pull`), and emits no context on
  `ok:refreshed`.
- A control-channel round-trip for `refresh` (start `ControlServer` against the fixture).

**Acceptance**: in a live session on FluidScript, `Edit` a method, then `search_index` it and
`get_symbol` the returned id — both correct without `reload_workspace`. Log line shows the scoped
refresh, not a rebuild.

### Phase 2 — remove the write path (contract 4.0, version 2.0.0)

**Delete**: `Tools/PatchTools.cs`; all of `Validation/` (`EscalationTable`, `ValidationLevel`,
`PatchDraftStore`, `TargetedTests`, `CheckReport`, `DiagnosticDistiller`, `ValidationLadder`,
`PatchSandbox`, `AnalyzerRunner`, `ChangeClassifier`); `Hooks/GuardCsEdit.cs`,
`Hooks/GuardCsBashWrite.cs`; `Identity/Ids.Draft()` and the patch-id minter; DI registrations of
`TargetedTests`/`PatchDraftStore` in `Program.cs`; tests `EscalationTableTests`, `PatchDraftStoreTests`,
`AnalyzerScopeTests`, `ChangeClassifierTests`, every `ValidatePatch_*` fact in
`WorkspaceIntegrationTests` (~24), the write-guard cases in `HookGuardTests` and `BashScannerGapTests`
that assert a *deny*; `skills/dotnet-guard-audit/`, `agents/dotnet-guard-redteam.md`;
`docs/tools/validate_patch.md`.

**Keep by moving**: `PatchSandbox.DominantLineEnding` and the write-document-preserving-line-endings
code → `Workspace/DocumentWriter.cs`; `PatchLineEndingTests` → `DocumentWriterTests`.
`BashCommandScanner.FindWriteTargetCsArgument` stays (the refresh hook uses it).
`WorkspaceHost.AdoptAppliedText`/`RunExclusiveApplyAsync` stay (rename uses them).

**Rewrite `Tools/RenameTools.cs`** per §3.1: resolve → `IsBehindDiskAsync` on the declaring files
(if behind, `RefreshPathsAsync` those paths and retry once; still behind → `stale_workspace`) →
`Renamer.RenameSymbolAsync` → dry run or apply (`RunExclusiveApplyAsync` → `DocumentWriter` →
`AdoptAppliedText` → await `RefreshDocumentsAsync(changed)` so the new `symbolId` resolves in the
same response) → response: `applied`, `files[]`, `referenceCount`, `newSymbolId`, `fileRenameHint`,
and the sentence "compilation was not checked; run `dotnet build`". Keep the keyword/identifier
validation. Tests to keep/adapt: `DryRunReachesImplementersAndCrossProjectCallers`,
`RenameInComments_EditsWhatTheDefaultLeavesAlone`, `AppliesRewritesEveryReference` (drop the "logs
once" half), `RenamingAType_ReportsANewSymbolIdAndTheFileRenameHint`,
`TypeNamedAfterItsFile_SaysTheFileWasNotRenamed`, `KeywordName_IsRejected`. Drop the mechanical-rekey,
colliding-member (both), `RequestedLevelTypo`, `StaleBaseVersion` facts.

**Hooks**: `WriteChecklistHint` → `CsWriteChecklistHint` on the first Edit/Write/NotebookEdit/
apply_patch to a compiled `.cs` (gate with `CsFileMembership.TryResolveOwningProject`, dedupe as
today); `HookCli` switch; `hooks.json` PreToolUse entries: drop the two write guards and the
`.*__validate_patch$` matcher, add the checklist on `Edit|Write|NotebookEdit`. `GuardSuspension`'s
messages and `workspace_status`'s `hookGuards` text lose the "write guards always did" clauses.

**Docs/skills** (§3.3–3.4): rule file last section; `dotnet-write` rewrite; `dotnet-read` mentions;
explore/review agents; performance and selfeval; consistency's file lists; init/verify/uninstall;
`hooks.md`, `architecture.md`, `agents.md`, `route-table-findings.md`, `compiler-aware-navigation.md`,
`benchmarks.md`, `README.md`, `CLAUDE.md`, three manifests, `.editorconfig` comment, six standards
lines. `Contract.Id = "ctx-contract/4.0"`; plugin version `2.0.0` in both `plugin.json` files and
`marketplace.json`.

**Acceptance**: `Edit` on a compiled `.cs` file is allowed, the checklist appears once, the refresh
hook is silent, `search_index` sees the edit. `rename_symbol` dry run and apply work on the fixture.
`ToolDescriptionBudgetTests` still pass with the trimmed descriptions.

### Phase 3 — `get_symbol` becomes a locator (contract 4.1)

**Delete**: `Contracts/SourceQuery.cs`, `Contracts/ContentVersion.cs`, `Tools/ResponseGuard.cs`
(+ `ResponseGuardTests`), `Output/ReadAdvice.cs` (+ `ReadAdviceTests`); in `ContextTools.cs` the
`source` parameter and `SourceOf`, `SourceLinesOf`, `RenderSource`, `ToSpans`, `SelectLines`,
`KeepsWholeDocComment`, `LineSpan`, `Exact/CompactRenderedLength`, `SplitLines`, `IndentStartOf`,
`IsWholeLine`, `AddSpan`, `FullVersionOf`, the version half of `IndexSymbol`, `contentVersion` on
symbols and members; `get_references`' `includeBodies`/`content`/`contentVersion` extra;
`search_index`'s `intent` and the `read` column. `SymbolComponents`: remove `Source`, `SourceQuery`,
`HasSlicedSource`, `RequiredLayers`; `Resolve(include, out invalid)`.

**Keep**: `ShapeFacts`/`SymbolShape` (`L`/`M` counts size the `Read`), `bodyOutline`,
`mechanicalFacts`, `OutlineBuilder`, all of `Fingerprint/` (drives incremental indexing and
`get_semantic_diff`).

**Read guards**: `GuardCsRead` advises only when `tool_input` has neither `offset` nor `limit`;
`GuardCsBashRead` only for whole-file shapes (`cat`, `type`, `less`, `more`, `sed -n` with no range);
both texts become "locate with `search_index`/`get_symbol`, then `Read` the `declarationSites` range";
forced-block mode allows ranged reads. `HookGuardTests`/`BashScannerGapTests` updated to that rule.

**Descriptions**: `get_symbol` — "locate and describe; read the text with `Read(file, offset:
startLine, limit: endLine-startLine+1)`; `startLine` includes a leading `///` comment, `endLine` is
the closing brace, every part of a partial is listed". `search_index`'s "read column" sentences out.

**Tests**: remove the ~25 `GetSymbol_Source*`/`SlicedSource`/`LineFormat`/`ContentVersion` facts;
add `GetSymbol_DeclarationSites_StartLineIncludesDocComment_EndLineIsClosingBrace` (the 8.6 contract)
and `GetSymbol_PartialClass_ListsEverySite`; `OutputFormatTests`/`SymbolShapeTests` adjust.

**Docs**: `docs/tools/get_symbol.md` rewrite (one selector, the line contract, partials, batch,
resolution failures); `get_references.md`, `search_index.md`; `dotnet-read` `get_symbol` section,
cheap-route table, "Reading responses"; explore/review agents; performance probes (ranged `Read` in
the MCP probe's grant and rationale); selfeval analyses. `Contract.Id = "ctx-contract/4.1"`.

### Phase 4 — development log and patch telemetry out (contract 4.2)

**Delete**: `Devlog/*` (5 files), `Store/FeatureLogStore.cs`, `HistoryTools.search_log`,
`SymbolComponents.RecentLog` + `ContextTools.RecentLogFor`, `StoreRecovery`'s `feature_log` salvage
(quarantine becomes rename-only), `Schema` tables `feature_log`, `feature_log_symbols`,
`patch_events` (schema version bump; drop on migrate), `TelemetryRecorder.RecordPatch`,
`MetricsReader`'s patch fold-in and `ToolTelemetry` mentions, `DevlogMigration.Run` in `Program.cs`;
tests `DevlogMigrationTests`, `DevlogTests`, `FeatureLogStoreTests`, the patch facts in
`TelemetryTests`; `StoreRecoveryTests` adjust. `docs/tools/search_log.md`; the `search_log` section of
`dotnet-read`; `.claude/settings.json` permission; `CLAUDE.md` "development-log tools";
`hooks.json` description string; `get_retrieval_metrics.md`. `Contract.Id = "ctx-contract/4.2"`.

Before this phase lands on a consuming repo, anyone who wants the FluidScript log keeps it by
running `search_log` once with a broad query and saving the output — after Phase 4 the table is
dropped. Legacy `.claude/dotnet-toolkit/devlog/*.md` files in consuming repos are not touched.

### Phase 5 — small fixes from the report

- `search_index`: parse `kinds`/`modifiers` given as a JSON-array string (strip `[`, `]`, quotes)
  — report 8.4. Test with `["Method","Property"]`.
- `search_index`: when the query has three or more terms and none hit, replace the per-word
  `termsWithNoHits` with one sentence: names only; string literals and comments are not indexed;
  use `grep`. Test.
- `KnowledgeStore`: after a successful index pass, keep the two newest `knowledge.db.corrupt-*`
  quarantines and delete the rest (report 1.3: 15 files, ~300 MB). Log the process id and the
  harness session id at store open, so two servers on one file can be seen in the log.
- Re-test report 8.3 (`symbol_not_found` for a returned id) on FluidScript after Phase 1; expected
  gone.

### Phase 6 — consistency and measurement

- Invoke `dotnet-consistency` (all three axes) and fix what it reports; it owns the authoritative file
  list and will catch mentions this plan missed (`docs/install/codex-*`, `CONTRIBUTING.md`,
  `AGENTS.md`).
- Run `dotnet-selfeval` and `dotnet-performance` against FluidScript; update `docs/benchmarks.md`.
- Update the memory notes that still say `validate_patch` is the write path.

---

## 6. Risks and how the plan handles them

| Risk | Handling |
|---|---|
| Scoped store update deletes or misses rows (partials, renames, edges from other files) | §4.1 steps 3–4: partial-sibling expansion, removal scoped to symbols declared *only* in the changed files, bounded re-index of `to`-edge documents, full rebuild above the bound. Integration tests for each. The 30 s full sweep plus `reload_workspace` remain as the manual repair. |
| A `Bash` write the scanner does not recognise | The sweep converges within 2 s on the next query; the hook was never the correctness path. |
| Losing "compile before write" | Accepted by decision #1; `dotnet build` is the gate. `get_diagnostics` is the deferred replacement if data shows agents miss it. |
| Consumers whose settings allow-list names `validate_patch`/`search_log` | Harmless; `dotnet-init`'s verify step reports the dead entry. |
| Marketplace: `2.0.0` removes tools | Breaking by definition; state it in the release note. |
| Performance methodology changes (MCP probe now reads via ranged `Read`) | Stated in the skill; earlier benchmark numbers are marked as pre-2.0. |
| Refresh hook exceeding its budget on a slow DrvFs compilation | Answers `ok:refresh queued`; the job still runs; the hook never blocks the edit. |
| Hook count and timeouts on Windows/Codex | Codex's `apply_patch` shape is already parsed; `refresh` is one more control command on the same channel. |

---

## 7. What this plan deliberately does not do

- **No post-write `dotnet build` hook.** Wrong cadence (per edit, not per burst) and 6 s of latency
  per `Edit`.
- **No `get_diagnostics` tool yet.** Deferred; it is the one piece of the ladder worth bringing back
  as a read tool if metrics show agents shipping compile breaks between builds.
- **No Bash write guard, not even advisory.** It was a workflow opinion tied to the dev-log
  argument, which is gone. The refresh path handles Bash writes.
- **No read-only `search_log`.** A log that stops on a fixed date is answered as if current.
- **No fix for 1.1, 1.2, 2.1, 2.2, 8.1, 8.2, 8.5.** All in deleted code.

## 8. Open questions for the author

1. `rename_symbol`: keep stripped (this plan) or drop for a pure navigation surface? Keeping it costs
   ~250 lines plus `DocumentWriter`; dropping it costs the only edit `Edit` cannot make.
2. The FluidScript development log has ~370 entries. Export before Phase 4, or let it go?
3. Version: `2.0.0` (this plan) or keep `1.x` and treat removed tools as a minor? The contract string
   already says breaking.
