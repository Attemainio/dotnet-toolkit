---
name: dotnet-toolkit-selfeval
description: Use when evaluating how well dotnet-toolkit's own MCP tools perform against the repo they are currently pointed at — "self-evaluate", "run the tool evaluation", "how efficient are these tools here", "audit the MCP responses for redundancy", or before/after changing a tool's arguments or return shape. Runs a fixed probe matrix over every shipped tool, measures each call's exact token cost from get_retrieval_metrics deltas isolated by a caller-supplied taskId, and reports where the same outcome was reachable with fewer calls or fewer tokens, which response fields restate what the caller already knew, and which outputs carry noise. Every finding is an improvement to dotnet-toolkit, never to the consuming repo's code. Read-only — it never applies a patch.
---

# Evaluating the toolkit against the repo it is pointed at

This plugin's whole claim is that its tools answer C# questions more completely, and for fewer tokens,
than Grep and Read. That claim is only ever exercised against whatever repo the server happens to be
loaded with — and repos differ in exactly the ways that break retrieval tools: nested types, partial
classes split across files, heavy overloading, generics, multi-targeted `.csproj`s, `.slnx` vs `.sln`,
source generators, unconventional folder layouts. This skill is the measurement pass that turns "the
tools feel efficient" into a per-call token number with a cheaper alternative next to it.

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

**Give each individual probe its own id** — `p_<family>_<name>`, all sharing one run-wide prefix — and
read them all back with a **single** `get_retrieval_metrics(groupBy: "task")` at the end. That is one
call per run instead of two per probe (2 instead of ~130 on a full matrix), and it yields the same
per-probe numbers, because a per-probe id makes each probe its own group:

```
<every probe call carries its own taskId, e.g. taskId: "p_C_rt_short">
...
costs = get_retrieval_metrics(groupBy: "task")     // one call, one row per probe
cost  = costs.groups["p_C_rt_short"].tokensReturned
```

To isolate one probe mid-run instead (a suspected drift, an instrument check), snapshot
`get_retrieval_metrics(taskIds: [<that id>], groupBy: "tool")` before and after it and subtract the
probed tool's own `tokensReturned`. Reading one tool's row makes both snapshot calls' own cost and any
other caller's traffic irrelevant to the number. Never take a per-probe cost from `totals`.

**This is what makes parallel evaluation possible at all.** Several agents can run different probe
families against the same server simultaneously, each passing its own `taskId` and reading back only its
own rows; `groupBy: "task"` then shows every family's totals side by side at the end. Without distinct
`taskId`s the agents silently attribute each other's tokens to themselves, and the whole run is void.

Before trusting a single number, verify the instrument on a call you can bound by eye:

1. Snapshot, run `search_index(query: "<one distinctive type name>", limit: 3, taskId: <yours>)`, snapshot.
2. Confirm the delta is non-zero, lands on the `search_index` row, and is plausible for the response you
   just saw.
3. Repeat the identical call and confirm the delta is the same. A drifting delta for an identical call
   means the instrument is unreliable — **stop and report that as `[bug]`**, since every number below
   depends on it, and a metrics tool that miscounts is itself the most important finding available.
4. Confirm your `taskIds` filter actually isolates: a snapshot with no `taskIds` should show strictly
   more calls than the same snapshot filtered to your id (unless you are genuinely alone on the server).

**Five tools record no telemetry and cannot be measured this way**: `ping`, `workspace_status`,
`set_output_format`, `reload_workspace` (constant-cost control calls) and `get_retrieval_metrics` itself
— deliberately, because a metrics tool that recorded its own calls would perturb every delta it is used
to compute. Probe them for correctness and judge their output size by eye; say so in the report rather
than reporting a measured number you did not measure.

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
| A · Orientation | `ping`, `workspace_status`, `get_project_graph`, `detect_circular_dependencies` | each cold; `workspace_status` again after a `reload_workspace`; `get_project_graph` whole-graph vs scoped to one project; `detect_circular_dependencies` with the unsupported `scope: "type"` |
| B · Discovery | `search_index` | one multi-term query vs. the same terms as separate calls; `kinds`, `modifiers` (AND semantics), `pathPrefix`, `implements`, `xmlDoc`, `summary: "has"` vs `"full"`; all three `groupBy` values on one identical query; `limit` at 3 / 10 / 50; `origin: "external"` |
| C · Retrieval | `get_symbol` | the full `include` ladder on **one** census symbol (Step 3a); `symbols:[…]` batch vs. N single calls; `source:code@a-b` with several ranges; a subtractive query (`source:full-remarks-attributes`); an `ambiguous_symbol` case; a symbol that does not exist |
| D · Relations | `get_references`, `get_call_slice`, `get_call_hierarchy`, `get_type_hierarchy`, `get_scope` | each on a census symbol; `get_references` on an interface member (dispatch coverage); `get_call_hierarchy` at rising `maxDepth`, `includeTree: false` vs `true`, and the same call at `maxDepth: 1` with default vs. raised `maxChildrenPerNode` (`blastRadius.totalUniqueNodes` must not move, and the capped run must report `truncated`/`omittedChildren`; at greater depths a tighter cap legitimately reaches fewer nodes, so do not assert equality there); `get_scope` with and without `receiver` |
| E · History | `search_log`, `get_semantic_diff` | `search_log` for a term known to be in the log and one known not to be; `get_semantic_diff` over a recent commit range and over an unresolvable ref |
| F · Write path | `validate_patch`, `rename_symbol` | **`applyOnSuccess: false` only** — an identity edit on a census symbol, a deliberately non-compiling edit (judge the distilled diagnostics), a stale `baseVersions` token (judge the conflict payload), and a body-changing edit whose `baseVersions` came from a *default* `get_symbol` (expect `unleased_body`; judge that payload). `rename_symbol`: a dry run on a census symbol (judge whether `files`/`occurrences` justify their cost against `detectedChanges`), a colliding name (judge the distilled diagnostics), and a stale `baseVersion` |
| G · Meta | `set_output_format`, `get_retrieval_metrics` | one identical `get_symbol` call rendered as `toon`, `compact` and `json`, measuring each; **restore the original format before finishing** |

Errors are probes, not accidents. An error payload's token cost and usability are part of the tool — an
`ambiguous_symbol` response listing forty candidates with fully-qualified `displayString`s is a finding.

## Step 3 — the three analyses

### 3a · Routes: was the same outcome reachable with fewer calls or fewer tokens?

Score a *route to a stated outcome*, not a single call. Each route costs **(calls, tokens)** — both
matter, and they trade against each other: a two-call route that costs fewer tokens than a one-call
route is usually the better one, but say so explicitly rather than ranking on tokens alone.

A cheap route that forced a follow-up call did not answer; its true cost is the sum of both calls. Record
that honestly — a route that looks cheapest but never suffices is the most expensive finding of all,
because the documentation recommending it is wrong.

| Outcome wanted | Cheap route | Expensive route |
| --- | --- | --- |
| What is this symbol for? | `search_index(summary: "full")` — answered by the search itself | `search_index` → `get_symbol(include: "source")` |
| What does it do, in more detail? | `get_symbol(include: "xmlDoc,bodyOutline")` | `get_symbol(include: "source")` |
| What happens near line N of a long member? | `bodyOutline` → `get_symbol(include: "source:code@N-M")` | `get_symbol(include: "source")` |
| What is its signature? | default `include` | `include: "source"` |
| What shape are these five symbols? | one `get_symbol(symbols: [...])` | five `get_symbol` calls |
| Who calls it (just the list, one hop)? | `get_call_hierarchy(maxDepth: 1)` | `get_references` |
| Where exactly is it called (file/line/snippet)? | `get_references` | repeated file reads |
| How much does changing it ripple? | `get_call_hierarchy(includeTree: false)` | full tree |
| What does it implement? | `search_index(implements:)` | `get_type_hierarchy` |
| How does X reach Y? | `get_call_slice` | repeated `get_references` hops |

Report each row as `cheap (c calls, t tokens) → expensive (c, t)`. A row where the "expensive" route is
actually cheaper, or where the cheap route did not answer, is worth more than every row that confirms
the ladder — it means the guidance in `dotnet-code-query`'s protocol is wrong, which is a `[bug]` in the
docs rather than a `[warning]` about tokens.

### 3b · Redundancy: does the response restate what the caller already held?

Take each probe's response field by field and classify every field as exactly one of:

- **new** — the caller could not have known it before the call. Keep.
- **restates-input** — it echoes an argument just passed (the symbol name when queried by
  fully-qualified name; the `groupBy` value; the file path handed to `get_scope`). Justifiable only when
  the caller could have passed something ambiguous that the server resolved — say which.
- **restates-prior** — the preceding call in a realistic chain already stated it. The motivating case:
  `search_index` returns `kind` per hit, then `get_symbol` on that hit's id returns `kind` again. Verify
  against the actual two-call sequence, not from memory.
- **constant** — the same value on every row and every call across the whole matrix (`origin: source`
  when `origin` already defaults to source). A field that never varies carries no information.
- **unconsulted** — no branch of a caller's decision depends on it.

Quantify before reporting: measure the field's per-call cost (compare an `include` with and without it
where possible, otherwise count rendered characters), then multiply by that tool's `calls` from the
**unfiltered** metrics totals. **Cost × real-world frequency is the ranking key** — a 4-token field on
the highest-traffic tool outranks a 200-token field on a tool called three times.

For a `restates-prior` field the recommendation is never a blunt "remove it": a field that is redundant
mid-chain is load-bearing on a cold call. State the *conditional* — suppress when the caller passed a
`sym_…` id (which only a prior response could have produced), keep when the caller passed a name.

### 3c · Noise: what could be said once instead of many times?

- **Unhoisted repetition** — a value repeated per row that could be a header. `search_index`'s `groupBy`
  already does this; check whether every other multi-row response (`get_references`, `get_call_slice`,
  `get_call_hierarchy`, `get_type_hierarchy`, `get_scope`) carries repetition it does not hoist.
- **Verbose scalars** — fully-qualified `displayString`s where a short name under an existing namespace
  header would do; absolute paths where root-relative would do.
- **Format overhead** — from Family G, the per-format cost of one identical response. If `toon` is not
  cheapest on this specimen, that is a finding about the default.
- **Uncapped growth** — any response whose size scales with the specimen (all references, all members,
  all candidates) with no cap or no truncation signal. Uncapped is a `[bug]` waiting for a bigger repo;
  uncapped *and* without a "there is more" marker is a `[bug]` now.

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

## Not exercised
<census features absent from this specimen, the probes they would have gated, and the five tools that
record no telemetry and so carry no measured cost>
```

Order findings by severity, then by measured impact within severity. If a family came back clean, say so
in one line — do not manufacture findings, and do not silently omit a family that was checked.

## Boundaries

- **Read-only.** `validate_patch` runs with `applyOnSuccess: false`, always. This skill never applies a
  patch, never edits `.cs`, and never fixes what it finds — a pass that rewrites the tools it is
  measuring invalidates its own baseline. Findings feed a separate change task, through `validate_patch`
  as normal.
- **Restore mutated server state** before finishing: `set_output_format` back to what it was.
  `reload_workspace` is safe but slow — call it deliberately, not per probe.
- **No findings about the specimen's code.** A genuine bug in the specimen is `dotnet-review`'s job.
- **Do not report a doc/code mismatch as a tool bug** without deciding which side is wrong.
  `dotnet-toolkit-consistency` owns drift between the tool surface and the files describing it; if the
  code is right and a doc is stale, hand it to that skill rather than duplicating its audit here.
