---
name: dotnet-selfeval
description: Use when evaluating how well dotnet-toolkit's own MCP tools perform against the repo they are currently pointed at — "self-evaluate", "run the tool evaluation", "how efficient are these tools here", "audit the MCP responses for redundancy", or before/after changing a tool's arguments or return shape. Runs a fixed probe matrix over every shipped tool, measures each call's exact token cost from get_retrieval_metrics deltas isolated by a caller-supplied taskId, and reports where the same outcome was reachable with fewer calls or fewer tokens, which response fields restate what the caller already knew, and which outputs carry noise. Also audits whether every shipped guidance document — the always-loaded rule, the skills, the standards, the agent definitions, the tool manuals and the hook messages — states *why* a directive holds rather than only what to do, since a reader who has to reconstruct the rationale guesses at it instead. Every finding is an improvement to dotnet-toolkit, never to the consuming repo's code. Never edits .cs and never applies a patch; it writes its report, and folds confirmed cheap-route findings directly into the dotnet-read and dotnet-write skills so the next session reads them without opening a findings document.
---

# Evaluating the toolkit against the repo it is pointed at

This plugin's whole claim is that its tools answer C# questions more completely, and for fewer tokens,
than Grep and Read. That claim is only ever exercised against whatever repo the server happens to be
loaded with — and repos differ in exactly the ways that break retrieval tools: nested types, partial
classes split across files, heavy overloading, generics, multi-targeted `.csproj`s, `.slnx` vs `.sln`,
source generators, unconventional folder layouts. This skill is the measurement pass that turns "the
tools feel efficient" into a per-call token number with a cheaper alternative next to it.

## Prerequisite: Load dotnet-read skill

**Before proceeding, ensure `dotnet-read` has been loaded.** This skill contains the validated
cheap-route table that serves as the ground truth for route comparison, and teaches how to interpret
`limitedBy` values and workspace readiness. If you haven't read it yet, invoke `dotnet-read`.

This takes <1s and gives you:
- The cheap-route table of verified cheaper alternatives
- Workspace readiness rules for `stale`, `degraded`, and `index_only`
- Best practices for `taskId` isolation and metrics sampling
- Field naming conventions and response structure

Reading this before the evaluation ensures you're comparing against the correct baseline and can
recognize when your own route analysis aligns with established guidance.

## The one rule: specimen vs subject

**The consuming repo is the specimen. dotnet-toolkit is the subject.**

Every probe reads the repo the server is pointed at — whatever `workspace_status` reports as `root`,
resolved by `SolutionLocator` from `DOTNET_TOOLKIT_PROJECT_DIR`, then `CLAUDE_PROJECT_DIR`, then the
working directory. When this repo is the target it probes itself; installed elsewhere, it probes that
repo. **Never aim the probes at a different repo than the loaded workspace** — the semantic tools can
only answer for the loaded solution, so a cross-repo probe measures nothing.

But **no finding is ever about the specimen's code.** "This method is too long", "this class needs an
interface" — out of scope, always, even when true. A finding is valid only if it names something
dotnet-toolkit should change: a tool's arguments, its return shape, its defaults, its `[Description]`,
its docs, or a bug in its behaviour. The specimen's oddities matter only as the *conditions* under which
a tool underperforms — which is the entire point. A tool that is lean on flat, single-project code and
wasteful on nested partial classes has a bug that only a specimen with nested partial classes reveals.

## Step 0 — build the instrument, and prove it works

Every measurement is a delta between two `get_retrieval_metrics` snapshots around a probe. Two things
make that delta trustworthy.

**Attribute every probe call with your own `taskId`.** Each measurable tool takes an optional `taskId`;
the value is written to the telemetry row and is the only axis that separates concurrent callers — the
session id is one per *server process*, so every agent talking to this server shares it.

**Give each individual probe its own id** — `p_<family>_<name>_<today>`, all sharing one run-wide prefix
— and read them all back with a **single** `get_retrieval_metrics(groupBy: "task", since: "<today>")` at
the end. That is one call per run instead of two per probe (2 instead of ~130 on a full matrix), and it
yields the same per-probe numbers, because a per-probe id makes each probe its own group:

```
<every probe call carries its own taskId, e.g. taskId: "p_C_rt_short_20260804">
...
costs = get_retrieval_metrics(groupBy: "task", since: "2026-08-04")   // one call, one row per probe
cost  = costs.groups["p_C_rt_short_20260804"].tokensReturned
```

**Both the date suffix and `since:` are mandatory, and they are not redundant.** The readback defaults
to `scope: "global"`, and telemetry outlives the server process — so a probe id reused from an earlier
run comes back as that run's rows summed with this one's, silently doubling the cost of every probe it
collides on. The matrix below is fixed, which *guarantees* the same names recur run over run; on one
measured run roughly a third of the ids collided, reporting `p_C_source` at 4,485 tokens against a real
2,406. Read the `calls` column on every group as a check: any probe issued once whose row says `calls: 2`
is a collision (or a double-recording bug), and the run's numbers are void until it is explained. The
instrument check below cannot catch this on its own, since it uses novel ids by construction.

To isolate one probe mid-run instead (a suspected drift, an instrument check), snapshot
`get_retrieval_metrics(taskIds: [<that id>], groupBy: "tool")` before and after it and subtract the
probed tool's own `tokensReturned`. Reading one tool's row makes both snapshot calls' own cost and any
other caller's traffic irrelevant to the number. Never take a per-probe cost from `totals`.

**This is also what makes parallel evaluation possible**: several agents can run different families
against one server at once, each reading back only its own rows. Without distinct `taskId`s they
silently attribute each other's tokens to themselves and the whole run is void.

Before trusting a single number, verify the instrument on a call you can bound by eye. Snapshot, run
`search_index(query: "<one distinctive type name>", limit: 3, taskId: <yours>)`, snapshot; confirm the
delta is non-zero, lands on the `search_index` row, and is plausible for the response you just saw. Then
repeat the identical call: a **drifting delta means the instrument is unreliable — stop and report
`[bug]`**, since every number below depends on it, and a metrics tool that miscounts is the most
important finding available. Finally confirm the filter isolates — an unfiltered snapshot should show
strictly more calls than the same one filtered to your id, unless you are genuinely alone on the server.

**Six tools record no telemetry and cannot be measured this way**: `ping`, `workspace_status`,
`set_output_format`, `reload_workspace`, `set_hook_guards` (constant-cost control calls) and
`get_retrieval_metrics` itself — the last deliberately, because a metrics tool that recorded its own
calls would perturb every delta it is used to compute. Probe them for correctness and judge their
output size by eye; say so in the report rather than reporting a measured number you did not measure.

**Do not probe `set_hook_guards` by calling it.** It is the one tool on that list whose effect
outlives its response: suspending the guards would let the rest of this run make raw `.cs` reads that
silently bypass the tools being evaluated, which is precisely the measurement error this skill exists
to avoid. Check it by reading its source and `docs/tools/server.md`. Measuring the *unguarded* route
on purpose is `dotnet-performance`'s job, not this one's.

Record `totals.toolCalls` and `totals.tokensReturned` for your `taskId` at the start and end, so the
report can state what the evaluation itself cost.

## Step 1 — environment fingerprint and specimen census

Every finding is conditional on the specimen, so the report has to describe it.

**Fingerprint** — from `workspace_status` and `get_project_graph`: root, entry file and its kind
(`.slnx` / `.sln` / bare `.csproj`), project count and names, index size (files, types), workspace load
time, and any load diagnostics. A workspace that loads *with* diagnostics, or an index that is ready
while the workspace never loads, is itself a finding about the toolkit's behaviour in that environment —
record it, then continue with the index-only tools.

**Census** — actively hunt the structural features that stress retrieval, because a matrix run only
against flat code proves nothing about the environments this plugin ships into. Find at least one
specimen of each, and note which are **absent** (an absent feature is untested coverage, not a pass):

| Feature | How to find one | Why it stresses the tools |
| --- | --- | --- |
| Partial class split across files | `search_index(modifiers: "partial", kinds: "class")` | `get_symbol` claims to return the whole symbol across fragments — verify `declarationSites` lists every file |
| Nested type | a `.`-heavy `displayString` among type-kind hits | tests `containingType` and name resolution |
| Overload set | `get_symbol` on a bare method name → `ambiguous_symbol` | tests the disambiguation path *and the size of its error payload* |
| Generic type/method | `search_index` for a known generic | tests `displayString` verbosity and id stability |
| Interface with several implementers | `search_index(implements: "<name>")` | tests `get_type_hierarchy` and `get_references` dispatch coverage |
| Very long member (>150 lines) | widest `endLine - line` in census results | the whole case for `bodyOutline` + `source:code@a-b` |
| Records / enums / delegates | `kinds:` filter per kind | kind-specific response shapes |
| Multi-project solution | `get_project_graph` | cross-project references, cycle detection |

## Step 2 — the probe matrix

Run to completion; there is no clock. The matrix is fixed so two runs are comparable — same families,
same questions, different specimen. Every tool is exercised several times with varied arguments, never
once with defaults.

| Family | Tools | Probes |
| --- | --- | --- |
| A · Orientation | `ping`, `workspace_status`, `reload_workspace`, `get_project_graph`, `detect_circular_dependencies` | each cold; `workspace_status` again after a `reload_workspace`; `get_project_graph` whole-graph vs scoped to one project; `detect_circular_dependencies` with the unsupported `scope: "type"` |
| B · Discovery | `search_index` | one multi-term query vs. the same terms as separate calls; `kinds`, `modifiers` (AND semantics), `pathPrefix`, `implements`, `xmlDoc`, `summary: "has"` vs `"full"`; all three `groupBy` values on one identical query; `limit` at 3 / 10 / 50; `origin: "external"`; one query spanning several kinds, so the `shape` column varies across `P`/`M`/`N`/`O`, kept for Step 3d; that same query with `intent` omitted, `"edit"`, `"logic"` and `"surface"`, so the `read` column varies across all four values and its absence, also kept for 3d |
| C · Retrieval | `get_symbol` | the full `include` ladder on **one** census symbol (Step 3a); `symbols:[…]` batch vs. N single calls; `source:code@a-b` with several ranges; a subtractive query (`source:full-remarks-attributes`); the default `Automatic` source line format against the same fetch forced `-exact` and `-compact`, to confirm Automatic actually picked the shorter of the two; an `ambiguous_symbol` case; a symbol that does not exist |
| D · Relations | `get_references`, `get_call_slice`, `get_call_hierarchy`, `get_type_hierarchy`, `get_scope` | each on a census symbol; `get_references` on an interface member (dispatch coverage); `get_call_hierarchy` at rising `maxDepth`, `includeTree: false` vs `true`, and the same call at `maxDepth: 1` with default vs. raised `maxChildrenPerNode` (`blastRadius.totalUniqueNodes` must not move, and the capped run must report `truncated`/`omittedChildren`; at greater depths a tighter cap legitimately reaches fewer nodes, so do not assert equality there); `get_scope` with and without `receiver` |
| E · History | `search_log`, `get_semantic_diff` | `search_log` for a term known to be in the log and one known not to be; `get_semantic_diff` over a recent commit range and over an unresolvable ref |
| F · Write path | `validate_patch`, `rename_symbol` | **`applyOnSuccess: false` only** — an identity edit on a census symbol, a deliberately non-compiling edit (judge the distilled diagnostics), a stale `baseVersions` token (judge the conflict payload), a body-changing edit whose `baseVersions` came from a *default* `get_symbol` (expect `unleased_body`; judge that payload), and the same clean edit run once with default `runAnalyzers` and once with `runAnalyzers: false` (judge the token delta and confirm `checks.analyzers` reports `{"ran": false, "skipReason": "…"}` rather than a verdict). `rename_symbol`: a dry run on a census symbol (judge whether `files`/`occurrences` justify their cost against `detectedChanges`), a colliding name (judge the distilled diagnostics), and a stale `baseVersion` |
| G · Meta | `set_output_format`, `get_retrieval_metrics` | one identical `get_symbol` call rendered as `toon`, `compact` and `json`, measuring each; **restore the original format before finishing** |

Errors are probes, not accidents. An error payload's token cost and usability are part of the tool — an
`ambiguous_symbol` response listing forty candidates with fully-qualified `displayString`s is a finding.

## Step 3 — the five analyses

Every probe feeds five analyses. Each is a different question about the same responses, and a run is not
complete until all five have been applied or explicitly reported as not applicable:

| # | Asks | Finds |
| --- | --- | --- |
| **3a · Routes** | was the same outcome reachable with fewer calls or fewer tokens? | guidance that recommends the more expensive route |
| **3b · Redundancy** | does the response restate what the caller already held? | fields that are `restates-input`, `restates-prior`, `constant`, or `unconsulted` |
| **3c · Noise** | what could be said once instead of many times? | unhoisted repetition, verbose scalars, format overhead, uncapped growth |
| **3d · Advice** | does a field telling the caller what to do next actually pay? | advice that costs more than ignoring it, is owed but absent, or is unactionable |
| **3e · Reasoning** | does the guidance say *why*, or only *what*? | directives a reader has to reconstruct the rationale for before they can apply them |

**How to run 3a–3d — the route tables, the field taxonomy, the advice-vs-default measurement, and the
ranking rules — is in `${CLAUDE_PLUGIN_ROOT}/skills/dotnet-selfeval/analyses.md`.** Read it before
Step 3; it is one file, and the numbers it asks for come from probes already run. 3e reads no probes
and is written out in full below.

Two rules span 3a–3d. Rank by **cost × real-world frequency**, never by cost alone — a 4-token field
on the highest-traffic tool outranks a 200-token field on a tool called three times. And a claim without
a measured delta is a `[message]`, never a `[warning]`.

### Where a route finding goes: the skill, not a findings doc

**A confirmed 3a route finding is applied to `skills/dotnet-read/SKILL.md`'s cheap-route table (or
`skills/dotnet-write/SKILL.md`'s, for a write route) in this same pass.** Not recorded in a separate
findings document for someone to transcribe later.

The reason is the failure that created the table. A route finding parked in
`docs/design/route-table-findings.md` is only read by someone who already went looking for it — so
the expensive route gets taken again, by every session, until a human copies the row across. The
whole point of the cheap-route table living in an on-demand skill is that a caller reading
`dotnet-read` *before* choosing a tool already knows both the route and **why** it is cheaper,
without a second file being opened. A finding that has not reached that table has not been delivered.

- **Write the row in the table's own voice**: the anti-pattern actually observed, then the cheap
  route, then the reason it wins — the measured delta, the threshold where it flips, or the
  information the expensive route pays for and discards. A row saying only "use X instead of Y"
  teaches nothing and gets ignored the first time the case looks slightly different.
- **A route with a crossover point states it**, because a rule with no boundary gets misapplied in
  the other direction. `get_references` vs `get_call_hierarchy` is the worked example already in the
  table: which one wins depends on fan-in, so the row names roughly where.
- **Supersede, never append.** If a row already covers the route, correct it in place with the newer
  measurement. Two rows on one route is the drift this skill exists to catch.
- **`docs/design/route-table-findings.md` is a frozen, dated record and is not updated.** Its own
  header says so. Do not add to it, and do not treat its historical statuses as current.

This is the one thing this skill writes outside its report — see *Boundaries*.

## Step 3e — the guidance-reasoning audit

Steps 3a–3d measure what the *tools* return. This one audits what the *guidance* says, because a
correct tool reached by a caller who guessed at the route costs exactly as much as a wasteful tool.

**The premise, which is measurable and not a style preference:** a directive that states only *what*
to do forces the reader to reconstruct *why* before deciding whether their case is the case it
covers. A strong model reconstructs it silently and cheaply; a weaker one reconstructs it out loud,
in output tokens, and frequently gets it wrong — guessing at the rule's boundary rather than reading
it. Both failures disappear when the reason is already in the sentence. Embedding the reason is
therefore a **token optimisation**, paid once in a file that loads on demand, and it is the cheapest
one available because it costs no tool calls at all.

Walk every guidance document. Each is either checked and clean, or carries a finding — an omitted
file reads as a passed one:

| Tier | Files | What to hold it to |
|---|---|---|
| Always-loaded | `.claude/rules/dotnet-index.md`, `CLAUDE.md` | Reasoning is required *here* and must stay one clause, never a paragraph — these two are under `harness-compliance.md` §D's size budget. A reason that needs more room belongs in the skill the row routes to |
| Skills | `skills/*/SKILL.md` and their reference files | Every directive, every table row, and every cheap-route entry names its reason. This tier has the most room and the most leverage |
| Standards | `standards/*.md` | Each rule says what it prevents — the bug, the cost, or the review comment it pre-empts — not merely what to write |
| Agents | `agents/*.md` | Each constraint says what goes wrong without it, since an agent cannot ask a follow-up question mid-run |
| Tool manuals | `docs/tools/*.md` | Each "don't do X" names the failure X produces, and each **Next steps** row says why that call follows this response |
| Hook messages | the `Deny`/`additionalContext` strings under `src/DotnetToolkit.McpServer/Hooks/` | A block message arrives when the caller is already committed to a different plan, so it must say why the tool path is *better*, not only that it is mandatory — otherwise it reads as an obstacle to route around, which is exactly what a weaker model then tries |

Three failure shapes to look for, in descending value:

- **Bare imperative** — "always call `workspace_status` first", "never anchor an edit on a
  `search_index` span" with no consequence attached. Highest value to fix: these are the directives
  most often violated, because nothing tells the reader what breaks.
- **Unbounded rule** — a directive with no stated threshold, so it cannot be applied to a case near
  the edge. Every route with a crossover point needs one.
- **Reason present but detached** — the why is in the file, three paragraphs from the rule it
  justifies. A reader following the rule never reaches it. Move it adjacent.

And one anti-finding, so this step does not become a mandate to pad: **do not flag a directive whose
reason is self-evident from the words already there.** "`dist/` is what runs, not `src/`" needs no
elaboration. Reasoning that restates the rule in different words is noise — 3c would flag it — and
this step must not manufacture what 3c then has to remove.

Findings are `[message]` by default: this step measures no tokens. It earns a `[warning]` only with
a concrete instance where the missing reason demonstrably produced a wrong route — one of this run's
own probes, or a route in the eval corpus.

## Severity labels

Every finding carries exactly one:

- **`[bug]`** — the tool is wrong: an incorrect or incomplete answer, a crash, a documented option that
  does not behave as documented, an uncapped or silently truncated response, a miscounting metric.
  Requires a **reproducer**: the exact call and the exact wrong output.
- **`[warning]`** — correct but wasteful or misleading: a measurably cheaper route to the same outcome, a
  redundant field, an error payload out of proportion to its information. Requires a **measured number** —
  a warning without a token or call delta is a message.
- **`[message]`** — an observation or opportunity: ergonomics, a missing argument, guidance drift, a
  feature this specimen could not exercise.

## Output format

Report inline, then write the same report to
`.claude/dotnet-toolkit/eval/<UTC-date>-<solution-name>.md` in the target repo and state the path — a
saved run is the baseline the next run is compared against, which is why the matrix is fixed.

```
# dotnet-toolkit self-evaluation — <date>

Specimen: <root> · <entry file> · <N projects> · <N files, N types> · workspace <loaded in Xs | diagnostics>
Census:   partial ✓ · nested ✓ · overloads ✓ · generics ✓ · long members ✓ · records ✗ · multi-project ✓
Task ids: <the ids used, and which family each covered>
Run cost: <N tool calls, N tokens returned> (this evaluation's own taskId totals)

## Findings
[bug] <one-line claim>
  Call:      <exact invocation>
  Observed:  <exact output, trimmed>
  Expected:  <what it should have been>
  Condition: <the specimen feature that triggers it, if any>
  Fix in:    <file/tool in dotnet-toolkit>

[warning] <one-line claim>
  Cheap route:  <calls> → N tokens
  Route taken:  <calls> → M tokens   (+K tokens, +C calls)
  Frequency:    <tool>.calls = <N, unfiltered> → est. waste <K×N>
  Fix in:       <file/tool>

[message] ...

## Route table
<the 3a table with measured (calls, tokens) per route>
<then, per row: applied to dotnet-read | applied to dotnet-write | already covered — superseded row X>

## Guidance reasoning (3e)
<one line per tier: files checked, and clean | N findings. An unlisted tier reads as skipped>

## Not exercised
<census features absent from this specimen, the probes they would have gated, and the five tools that
record no telemetry and so carry no measured cost>
```

Order findings by severity, then by measured impact within severity. If a family came back clean, say so
in one line — do not manufacture findings, and do not silently omit a family that was checked.

## Boundaries

- **Never changes the subject it measured.** `validate_patch` runs with `applyOnSuccess: false`,
  always. This skill never applies a patch and never edits `.cs` — a pass that rewrites the tools it
  is measuring invalidates its own baseline. Code findings feed a separate change task, through
  `validate_patch` as normal.
- **Writes exactly two things**: the report, and the cheap-route rows from 3a into
  `skills/dotnet-read/SKILL.md` / `skills/dotnet-write/SKILL.md`. That is not an exception to the
  rule above — a markdown table describing which tool to call is not a tool, so updating it changes
  nothing the next run measures. It is the one finding whose value expires if it waits: an
  un-transcribed route is re-taken by every session until someone copies it across. A 3e finding is
  reported, **not** applied — rewriting guidance prose in the same pass that judged it leaves nobody
  to check the judgement.
- **Restore mutated server state** before finishing: `set_output_format` back to what it was.
  `reload_workspace` is safe but slow — call it deliberately, not per probe.
- **No findings about the specimen's code.** A genuine bug in the specimen is `dotnet-review`'s job.
- **Do not report a doc/code mismatch as a tool bug** without deciding which side is wrong.
  `dotnet-consistency` owns drift between the tool surface and the files describing it; if the
  code is right and a doc is stale, hand it to that skill rather than duplicating its audit here.
