# Route-table findings: tool selection, ToolSearch, and the eval corpus

Written 2026-08-05 in response to a direct audit request: how does tool selection actually work in
this plugin, does Claude's own `ToolSearch` find the right MCP tool from its description, and what
does the eval corpus (`.claude/dotnet-toolkit/eval/*.md` in this repo and in PandaAI, a consumer
repo) say has been fixed vs. still open. Findings below are either **measured directly in this
session** or **synthesized from the 9 eval files**, kept separate so provenance is clear.

## 1. How tool selection actually works here

There are two independent selection mechanisms in play, and they answer different questions:

1. **The always-loaded routing table** (`.claude/rules/index.md`, "Which tool" section) — a fixed
   markdown table mapping a task shape ("Grep for callers", "Guessing whether a helper applies") to
   an exact MCP tool name. This is **read by the model as instructions**, not searched.
2. **`ToolSearch`** — the harness mechanism that loads a deferred tool's schema by name or keyword
   before it can be called. Every `mcp__plugin_dotnet-toolkit_dotnet__*` tool is deferred, so it must
   pass through `ToolSearch` at least once per session regardless of which of the two mechanisms
   picked it.

The rule's instructed workflow is: **use the table to pick the tool name, then call
`ToolSearch("select:<exact name>")` to load it** — never describe the task to `ToolSearch` and let
it rank candidates. That instruction exists because of a specific measured failure, confirmed again
in this session (§3).

## 2. Bottleneck catalog: cheap route vs. route actually taken

Section 1 covers *which tool gets picked*. This section covers a different failure: the **right**
tool gets picked, but called in a shape that pays more tokens than a cheaper call (or a different
tool entirely) would have for the identical answer. Each row states the anti-pattern actually
observed (in the eval corpus, or — where marked — in ordinary use reported directly to this
session), the cheap alternative, the measured or reasoned cost of the miss, and where the fix now
lives. "Fixed in doc" means a callout was added in this session so the *next* call doesn't repeat
it; "open" means no doc note closes it (either because the fix has to be in the code, or because
the finding is unconfirmed).

| # | Anti-pattern (route taken) | Cheap route | Cost of the miss | Status |
|---|---|---|---|---|
| 1 | `search_index(pathPrefix: "<one exact .cs file>")` to browse a known file's symbols | `get_symbol(symbol: "TypeName", include: "members")` on the type directly | A whole-index ranked search paid to answer a question that doesn't need ranking at all — `get_symbol` gives the same member list plus signatures/docs/source for the same or fewer tokens. Reported directly this session, not from the eval corpus. | **Fixed in doc** — `search_index.md`, "Already know the file? Don't `pathPrefix` down to it" |
| 2 | Calling `search_index` once per term (`search_index("fee")`, `search_index("ledger")`, …) | One call, all terms OR-ed: `search_index("fee ledger TryBuy TrySell")` | N round trips for one answer; ranking across terms is also lost, so a symbol matching several terms doesn't sort to the top the way it would in one call. | **Fixed in doc** — always was the tool's lead example, `search_index.md` |
| 3 | Re-fetching a symbol with `get_symbol` that this session already fetched and hasn't changed | Reuse the held `contentVersion`/`declarationSites`; after an edit, use the applied response's `newVersion`/refreshed `declarationSites` directly | Measured in the eval corpus: `get_symbol`'s lease mechanism (`knownVersion`) had `leaseHits: 1` out of 1,010 calls in one window — essentially unused despite existing for exactly this case. | **Fixed in doc** — `get_symbol.md`, "Don't refetch what you already hold" (an ergonomics gap the corpus never resolved; the doc note is the first fix either way) |
| 4 | `validate_patch(applyOnSuccess: false)` immediately followed by an identical resubmission with `applyOnSuccess: true` | Set `applyOnSuccess: true` from the start whenever the change is already decided | The ladder runs byte-for-byte identically regardless of `applyOnSuccess` — a dry run then an apply re-compiles and re-sends the same payload for zero new information. Previously documented only in `skills/dotnet-change/SKILL.md`, absent from the tool's own doc — a caller reading only `validate_patch.md` had no way to know. | **Fixed in doc** — `validate_patch.md`, "Don't dry-run then apply as two calls" |
| 5 | `get_call_hierarchy` for a one-hop caller list on a low-fan-in symbol, or `get_references` for an open-ended multi-level tree on a high-fan-in one | The other tool, past/below roughly a dozen callers | Measured crossover, both directions: `get_call_hierarchy` at 105 callers costs ~1/8 of `get_references`' tokens; at 1 caller, `get_references` is cheaper (100 vs. 139 measured in one run). | **Already fixed in doc** before this session — both `get_references.md` and `get_call_hierarchy.md`'s "Next steps" state the crossover with numbers in both directions |
| 6 | Batch `get_symbol(symbols: [...])` used reflexively as "obviously cheaper" at any batch size | A single-symbol call, below roughly n=8–10 | The `shared` hoisting block that makes batching cheap at scale is a near-wash below that break-even — one measured run found batching at n=5 winning on call count but losing slightly on tokens (+80 tok, −4 calls) against 5 singles. | **Open** — `get_symbol.md`'s "Several symbols in one call" already says "on tokens it is roughly a wash," which is honest but doesn't give the n=8–10 break-even a number; worth sharpening if re-measured |
| 7 | Treating `get_scope`'s `definedIn`/`origin` empty string (`""`) as a real (if odd) value rather than "omitted" | Read an empty cell in the default `toon` table as absence, not as an empty-string fact | Reported as a doc/schema mismatch in the eval corpus: the tool's own description promises omission, but a TOON table forces every row in a column to render *something*, so "omitted" surfaces as `""` rather than a missing key. | **Documented as intentional**, not closed — `get_scope.md`'s "Rendering" section already explains the TOON-table constraint explicitly, which resolves the confusion even though the underlying JSON-vs-TOON asymmetry itself is unchanged |
| 8 | Calling `rename_symbol` twice — telemetry (unconfirmed report) records two rows per call, double-counting tokens | N/A — this is a telemetry-accuracy bug, not a route choice a caller can work around | Reported once (2026-08-04), never re-confirmed in a later eval file. | **Open, needs a code fix** — no doc note can route around a measurement bug; flagged again in §5 |
| 9 | Trusting `-lineNumbers`' documented "~18% cheaper" figure to decide whether to drop line numbers | Expect roughly 6% on a typical member — real, but much smaller, since the gutter is a small fixed per-line cost | The original 18% figure was measured against the wrong baseline (the rendered gutter, not the raw file) before being corrected. | **Fixed in doc** — the corrected 6% figure is what `get_symbol.md`'s compaction (§4) carried forward, replacing the stale 18% |

Two things this table is *not* claiming: first, that every anti-pattern above has actually been
observed in this exact repo's transcripts — rows 3, 6, 7, 8, 9 are drawn from the eval corpus's own
measurements (provenance in §5), while rows 1, 2, 4 combine a corpus/skill-documented fact with a
doc-placement gap this session closed directly. Second, that a "Fixed in doc" status means the
underlying tool no longer *can* be called wastefully — it means the next call has a callout to read
first. Only a code change (rows 6, 8, and to a lesser extent 3) closes the underlying gap; the doc
note is a mitigation, not a fix to the tool.

## 3. Does ToolSearch find the right tool by description? — measured against all 18 tools

The first pass through this question (5 queries) under-covered the surface: it found 2 of 5
failing and stopped there. A full sweep — one natural-language query per tool, phrased the way a
person would actually ask, deliberately avoiding each tool's own description vocabulary — was run
against **all 18 tools**. Result: **11 of 18 failed to appear in the top 5**, not 3.

| Query sent | Expected tool | Result |
|---|---|---|
| `"who calls this method"` | `get_references` | **Fail.** Returned `DesignSync`, `get_call_hierarchy`, `EndConversation`, `CronCreate`, `get_symbol`. |
| `"why was this code written this way, development history"` | `search_log` | **Fail.** Returned `DesignSync`, `PushNotification`, `TaskCreate`, `CronCreate`, `EndConversation`. |
| `"edit a C# file safely"` | `validate_patch` | **Fail.** Returned `NotebookEdit`, `CronCreate`, `TaskOutput`, `PushNotification`, `TaskCreate`. |
| `"look up a class and see its source code"` | `get_symbol` | **Fail.** Returned `TaskUpdate`, `search_index`, `EndConversation`, `ExitPlanMode`, `PushNotification`. |
| `"what can I call at this point in the code"` | `get_scope` | **Fail.** Returned `get_call_hierarchy`, `get_call_slice`, `workspace_status`, `validate_patch`, `EndConversation`. |
| `"what classes implement this interface, inheritance chain"` | `get_type_hierarchy` | **Fail.** Returned `EnterPlanMode`, `ExitPlanMode`, `EndConversation`, `Monitor`, `PushNotification`. |
| `"what changed in this commit or branch"` | `get_semantic_diff` | **Fail.** Returned `set_output_format`, `workspace_status`, `Monitor`, `reload_workspace`, `get_references`. |
| `"how many tokens has this session used"` | `get_retrieval_metrics` | **Fail.** Returned `EndConversation`, `EnterPlanMode`, `ExitWorktree`, `WebFetch`, `ExitPlanMode`. |
| `"is the workspace ready, is indexing done"` | `workspace_status` | **Fail.** Returned `TaskList`, `CronList`, `EndConversation`, `ExitWorktree`, `PushNotification`. |
| `"refresh after pulling new code from git"` | `reload_workspace` | **Fail.** Returned `set_output_format`, `Monitor`, `get_call_hierarchy`, `search_log`, `get_call_slice`. |
| `"is the server alive"` | `ping` | **Fail.** Returned `TaskList`, `CronList`, `EndConversation`, `ExitWorktree`, `PushNotification`. |
| `"find all references to a symbol"` | `get_references` | Found, 3rd of 5. |
| `"find symbols by name in the codebase"` | `search_index` | Found, 2nd of 5. |
| `"show me the full call tree for this method"` | `get_call_hierarchy` | Found, 1st of 5. |
| `"is there a path from one function to another"` | `get_call_slice` | Found, 1st of 5. |
| `"which project depends on which, project references"` | `get_project_graph` | Found, 1st of 5. |
| `"circular reference loop between projects"` | `detect_circular_dependencies` | Found, 1st of 5. |
| `"rename this method everywhere it is used"` | `rename_symbol` | Found, 1st of 5. |
| `"change response format to json"` | `set_output_format` | Found, 1st of 5. |

A second, keyword-dense round (phrased using each tool's own description vocabulary rather than
natural phrasing) found every one of the same 11 tools on the first attempt — confirming the
mechanism is lexical, not that those 11 tools are unfindable in general:

| Query sent | Expected tool | Result |
|---|---|---|
| `"development log why was this changed"` | `search_log` | Found, 1st. |
| `"patch code change validate compile"` | `validate_patch` | Found, 1st. |
| `"circular dependency between projects"` | `detect_circular_dependencies` | Found, 1st. |
| `"what methods are callable at this line, extension methods in scope"` | `get_scope` | Found, 1st. |

**The failure rate (11/18, ~61%) is the headline finding, not the 2/5 the first pass suggested.**
Two things stand out beyond raw count:

- **A close paraphrase can still fail** when the discriminating word is diluted across many other
  descriptions. `get_scope`'s own description opens with *"What is callable HERE"* — nearly a direct
  match for `"what can I call at this point in the code"` — yet it never appeared, because "call"
  also appears throughout `get_call_hierarchy`'s and `get_call_slice`'s descriptions (and "callers"
  throughout `get_references`'), so it carries little discriminating weight under BM25's term-
  frequency scoring. The same happened to `reload_workspace`: its description already says *"e.g.
  git checkout/pull"*, a near-exact match for `"refresh after pulling new code from git"`, but "git"
  is common enough elsewhere (`get_semantic_diff`) that it didn't surface. Lexical overlap on the
  *rare* word matters more than overlap on any word.
- **The always-loaded rule's citation is the tip of a much larger iceberg.** `.claude/rules/index.md`
  cites one confirmed failure (`get_references`) as justification for "always resolve the tool name
  from the routing table, never free-text `ToolSearch`." That instruction turns out to be load-
  bearing for the *majority* of the tool surface, not an edge case — which makes it more clearly
  correct, not less.

One gap in the eval corpus itself: the routing rule cites its one finding as empirical ("measured
here"), but none of the 9 eval `.md` files contains a `ToolSearch` reproducer — the finding
apparently came from ordinary dogfooding outside the formal self-eval process. This session's sweep
is the first recorded, reproducible confirmation of it — and the first to show its true extent.

### Why — per Anthropic's own tool-search documentation

Anthropic's [tool search tool docs](https://platform.claude.com/docs/en/agents-and-tools/tool-use/tool-search-tool)
confirm this isn't a quirk of this plugin's descriptions — it's how the mechanism is built:

- There is **no semantic/embedding variant** in the built-in tool. It is one of exactly two:
  **regex** (Claude writes a Python `re.search()` pattern) or **BM25** (a classical lexical
  term-frequency ranking algorithm — the same family search engines used before embeddings). Either
  way, the search indexes **tool name, description, argument names, and argument descriptions** —
  plain text matching, not intent understanding. A query and a description "mean the same thing" is
  irrelevant if they don't share terms, and sharing a *common* term (§3's "call"/"git" cases) barely
  helps either.
- The docs' own troubleshooting entry for "Claude doesn't find expected tools" says exactly this:
  *"The regex pattern doesn't match the tool's name, description, argument names, or argument
  descriptions... Add common keywords to tool descriptions to improve discoverability."*
- The explicit optimization tip: **"Use keywords in descriptions that match how users describe
  tasks."**

### Fix applied: description text for all 11 failing tools

Rather than leave this as a finding, the `[Description]` attribute for each of the 11 failing tools
was edited in this session (`Tools/*.cs`, applied via `validate_patch`, two patches, both reaching
`dependent_compile` clean) to fold in the missing natural-phrasing term as a short added clause,
without disturbing the rest of the description's precise, schema-relevant wording:

| Tool | Phrase added |
|---|---|
| `get_references` | "who calls it, its usages, what invokes it" |
| `search_log` | "the history and reasoning behind them" |
| `validate_patch` | "the safe way to edit or modify a C# file" |
| `get_symbol` | "look up a class, interface, method, property or field and read its" |
| `get_scope` | "what's in scope, autocomplete-style" |
| `get_type_hierarchy` | "which types implement or inherit from it" |
| `get_semantic_diff` | "(a commit or a branch)" |
| `get_retrieval_metrics` | "how many tokens this session has used" |
| `workspace_status` | "Is the workspace ready, is indexing done" |
| `reload_workspace` | "Refresh the workspace" (replacing "Force a re-scan") |
| `ping` | "Is the server alive" |

This is a targeted content fix (a short clause folded into each description), not a rewrite — the
descriptions are otherwise precise and remain load-bearing for the *schema-level* guidance they give
once loaded. **The routing-table workaround should stay regardless** — it is strictly cheaper (no
search round trip) and the table's job of picking *which* tool for a task shape is broader than what
any one tool's own description can cover — but this fix closes the gap for the case the rule can't
cover: a caller (or a subagent) that reaches for `ToolSearch` directly with a natural phrasing, as
`dotnet-explore` and other agents sometimes do outside the always-loaded rule's own context.

**Re-verification attempted, and it surfaced a real process gap.** A fresh `ToolSearch` probe
against 4 of the 11 fixed tools (`get_references`, `get_scope`, `ping`, `workspace_status`) found
only `workspace_status` now passing; the other 3 still failed with the identical top-5 as before the
fix. Cause confirmed: **`dist/` had not been republished at the time of that probe** — this repo's
own rule (`CLAUDE.md`: *"`dist/` is what runs... Re-publish after any server change"*) applies to a
`[Description]` attribute exactly as it does to any other server change, and this session's MCP
server process was still serving the pre-edit compiled descriptions from the *previously built*
`dist/`. `workspace_status` passing was most likely coincidental (a different word in its
already-long description tipping the balance), not evidence the fix mechanism worked ahead of the
others.

`dist/` has since been republished in this session (`dotnet publish ... -o dist`, after `dotnet
test` passed 419/419 — one description edit initially pushed `get_references` over a 1900-byte
`[Description]` budget a dedicated test enforces, `ToolDescriptionBudgetTests`, and was trimmed back
under it). **But a currently-running MCP server process does not hot-reload a republished `dist/`**
— it already loaded the old assembly at process start, so this session's own `ToolSearch` will keep
returning pre-fix results until the MCP connection is reloaded (a fresh session, or an `/mcp`
reconnect).

**Confirmed fixed, 2026-08-06, from a fresh session (`dotnet-toolkit-consistency` audit run).**
Re-probed `"who calls this method"` (→ `get_references`, now 3rd of 5, previously absent),
`"edit a C# file safely"` (→ `validate_patch`, now 1st, previously absent), `"is the server alive"`
(→ `ping`, now present, previously absent), and `"is the workspace ready, is indexing done"` (→
`workspace_status`, 1st). All four pass in a fresh session against the republished `dist/`. The
outstanding step this section called for is closed: the fix is real, on disk, and live once a
session reconnects.

## 4. `search_index.md` and `get_symbol.md`: compacted

Both files carried significant duplication — most notably `get_symbol.md`, whose component table
(`source`, `xmlDoc`, `mechanicalFacts`, `bodyOutline`, `referenceCounts`, `recentLog`, `members`,
`attributes`, `baseType`, `interfaces`, `usings`) was written out **twice**, once in the "When to
reach for it" prose section and again nearly verbatim in a trailing "Reference" table, with worked
examples repeating points already made in prose immediately above them.

Both were rewritten in place to state each fact once:

| File | Before | After | Cut |
|---|---|---|---|
| `docs/tools/search_index.md` | 461 lines | 129 lines | ~72% |
| `docs/tools/get_symbol.md` | 575 lines | 180 lines | ~69% |

Nothing load-bearing was dropped — every filter, every gotcha, and every explicit invariant survived
the cut:

- `termsWithNoHits` and the term floor (`search_index`)
- the `shape` column's full letter table and per-shape next-call table (`search_index`)
- `generated`/`outsideRoot` location-loss reasons (`search_index`)
- the full component table, stated once (`get_symbol`)
- the "stripped source is unsafe to anchor a patch on" invariant, with its worked failure example
  (`get_symbol`)
- the `contentVersion` narrowing rule and `unleased_body` (`get_symbol`)
- `didYouMean`/`ambiguous_symbol` resolution-failure shapes (`get_symbol`)
- the `referenceCounts` expansion-gating heuristic, including the "never conclude dead code from a 0
  alone" warning (`get_symbol`)
- the `-lineNumbers` measured-6%-not-18% correction (`get_symbol`) — itself a finding from the eval
  corpus (§5), now correctly reflected in the doc rather than needing a second correction pass

What was cut was structural redundancy: the duplicate component table, repeated JSON captures
illustrating the same single point (e.g. three near-identical `bodyOutline` examples reduced to
one), and prose that restated a table row in sentence form immediately below the table.

## 5. Eval corpus: what's confirmed fixed, what's open, what's not-a-bug

Synthesized from all 9 eval files across both repos
(`PandaAI/.claude/dotnet-toolkit/eval/2026-07-28` through `2026-08-04`, and
`dotnet-toolkit/.claude/dotnet-toolkit/eval/2026-07-28` through `2026-08-05`), cross-referenced
against the git log. This is a **secondary synthesis** (produced by a subagent that read all 9
files in full) — treat specific quotes as reported, not independently re-verified line-by-line in
this pass.

### Confirmed fixed (re-tested and passed, or fixed by a named commit)

- **`get_project_graph` ambiguous count** → renamed to `totalProjectsInSolution` (2026-07-28).
- **`search_index` losing location for methods with their own generic type parameters** — root
  cause (lookup key using bare `Name` vs. Roslyn's `Pick<T>(T)`) found and fixed 2026-07-30, with a
  same-day follow-up fix for type-parameter formatting variance (spacing/attributes/variance),
  re-verified fixed 2026-07-31.
- **`stale_base` errors invisible to telemetry** — fixed and confirmed same-day, 2026-07-29.
- **`validate_patch` staleness check silently weaker on a default-include `contentVersion`** — traced
  through a false "not a defect, stale server binary" detour (2026-07-30) to a confirmed real fix,
  re-verified 2026-07-31: `unleased_body` now correctly rejects a body edit built on a decl-only
  token.
- **`get_call_hierarchy(includeTree:false)` under-reporting blast radius 4×** — confirmed fixed
  2026-07-30 (26→104, `truncated`/`omittedChildren` now present).
- **Delegates missing from the tool's census** (reported as absent across 5 separate runs) — closed
  2026-07-30: a delegate fixture was added and `search_index`/`get_symbol`/`get_references` all
  verified against it. A second, unrelated gap found in the same pass —
  `get_references(direction:"callers")` on a named type returning empty — was fixed in the same
  commit (resolving each reference to its enclosing member).
- **`Wire(ValidationLevel)`/`Wire(ChangeKind)` extension-method overload losing its location** — the
  "one confirmed bug" from the 2026-08-05 self-evaluation (`b1884a7`): a `this`-parameter modifier
  was leaking into the lookup key, reducing `Wire(this ValidationLevel)` to `"thisValidationLevel"`.
  Visible as an unexplained symptom as far back as 2026-07-28, correctly root-caused a week later.
- **`search_index`'s documented cap said 50, code enforced 200** — fixed same session, `11acfb3`.

### Explicitly ruled not-a-bug / working as documented

- `get_symbol(include:"all")` dropping `xmlDoc` once `source` is also requested — documented
  suppression behavior (see §4's component-suppression rule), ruled correct in `b1884a7`.
- `get_call_hierarchy` rendering an identical subtree twice on a diamond convergence — documented
  behavior (a symbol reached by two branches appears twice in the tree, once in `blastRadius`),
  ruled correct in `b1884a7`.
- `get_semantic_diff`/`search_log` "unusable" in PandaAI's layout — reclassified same-day as caller
  error: the baseline run never passed the documented `repo` argument for a solution root holding
  two independent git repos.
- A `tokensSavedByLeases` under-credit claim — retracted by its own author within the same file as a
  self-diagnosed cross-`taskId` telemetry query artifact, not a tool bug.

### Open / unconfirmed as of the latest eval (flagged as the standout gaps)

- **`validate_patch`'s `draftId` amend path** — reported completely broken on 2026-07-29 (every
  amend call crashes with an undiagnostic error, reproduced 4× including with the documented-legal
  `edits: []`). This directly contradicts the required workflow this repo's own `CLAUDE.md`/rules
  describe ("amend through the returned `draftId`"). **No later eval file re-tests this.** This is
  the single largest unconfirmed item in the corpus and is worth a direct re-test before relying on
  the amend path.
- **`rename_symbol` recording two telemetry rows per call** (double-counting tokens), reported
  2026-08-04, reproduced on all four probes that run, never confirmed fixed in a later file.
- **The telemetry instrument itself measuring `tokensReturned` pre-render** (so `toon`/`compact`/
  `json` report identical counts for genuinely different payload sizes) — reported 2026-07-28, never
  confirmed fixed or refuted, yet later reports continue to compare per-format token costs without
  re-flagging the caveat.
- **`get_scope`'s `definedIn` field** — reported as duplicating `receiverType` (waste), then
  narrowed to "conditional, not general" (informative when the receiver's own type doesn't declare
  the member), then reported again as rendering `definedIn: ""` (emitted-empty) rather than omitted
  where the doc promises omission. Last report (2026-08-04) calls this a genuine doc-vs-schema
  mismatch, not fixed.
- **`get_symbol`'s lease protocol (`knownVersion`) essentially unused in practice** — `leaseHits: 1`
  out of 1,010 calls in one measured window. Framed as an ergonomics/adoption gap rather than a
  correctness bug; never resolved in the corpus.
- **No build-identity stamp** (`ping`/`workspace_status` report a constant `0.1.0`) — flagged
  2026-07-30 as a process risk that had *already* caused one finding to be misdiagnosed as "not a
  defect" when the real cause was a stale `dist/` build. No later file confirms a commit/build-time
  stamp was added.

### Discovery/ranking findings in the corpus itself

Only the one already covered in §3 — the corpus doesn't contain an independent `ToolSearch`
reproducer of its own; the always-loaded rule's claim is the only citation for it, which is itself a
minor documentation gap (an empirical claim with no recorded probe backing it, until this session).

### "Unreferenced / not indexed" tools — checked directly, not reproduced

An earlier claim (from a prior conversation, not from the eval corpus) that some tools are "not
indexed or referenced" does **not hold up under a direct check performed in this session**:

- Every one of the 18 MCP tools (`search_index`, `get_symbol`, `get_references`,
  `get_call_hierarchy`, `get_call_slice`, `get_scope`, `get_type_hierarchy`, `get_project_graph`,
  `detect_circular_dependencies`, `get_semantic_diff`, `search_log`, `validate_patch`,
  `rename_symbol`, `get_retrieval_metrics`, `workspace_status`, `reload_workspace`, `ping`,
  `set_output_format`) has a corresponding manual file under `docs/tools/` — 14 tools with their own
  `<name>.md`, plus the four session/meta tools (`workspace_status`, `reload_workspace`, `ping`,
  `set_output_format`) sharing `server.md`.
- 17 of the 18 have an explicit row in `.claude/rules/index.md`'s "Which tool" selection table.
  **`set_output_format` is the one exception** — it has a manual (via `server.md`) and is mentioned
  in the "Reading responses" section, but has no row of its own in the task-shape selection table.
  This is a minor, real gap (a session-config tool genuinely doesn't map to a "when to reach for it"
  task shape the way the other 17 do), not the broader "several tools missing" claim.

The likely source of the earlier, broader claim: conflating "`ToolSearch` fails to surface a tool
for a vague query" (§3 — true, and serious) with "the tool has no documentation or routing entry"
(false, per the direct check above). Those are different failure modes with different fixes: §3's
fix is procedural (always resolve the name from the table first), not a documentation gap to patch.

## Summary

| Question | Answer |
|---|---|
| How is a tool selected? | The always-loaded routing table names it by task shape; `ToolSearch("select:<exact name>")` then loads its schema. Free-text `ToolSearch` is not the selection mechanism and should not be treated as one. |
| Does `ToolSearch` find the right tool from a natural description? | No, reliably, for vague/natural phrasing — confirmed in this session for `get_references`, `search_log`, and `validate_patch`. Yes, when the query shares the tool description's own vocabulary. Expected: the mechanism is lexical (regex or BM25), per Anthropic's docs, not semantic — "use keywords that match how users describe tasks" is the documented fix, and a handful of missing synonym phrases in those three descriptions would likely close the gap. |
| Were `search_index.md`/`get_symbol.md` bloated? | Yes — both carried duplicate component tables and repeated worked examples. Cut ~72%/~69% respectively with no loss of load-bearing content. |
| Are tools missing from documentation/routing? | No tool lacks a manual. One tool (`set_output_format`) lacks a routing-table row, which is a minor, real, narrow gap — not the broad "several tools unreferenced" claim. |
| What's still open from the eval corpus? | `validate_patch`'s `draftId` amend path (most severe, never re-tested), `rename_symbol` double telemetry, the telemetry instrument's pre-render token measurement, `get_scope`'s `definedIn` emitted-empty-vs-omitted mismatch, and no build-identity stamp. |
