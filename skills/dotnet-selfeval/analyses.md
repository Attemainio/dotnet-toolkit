# The four measured self-evaluation analyses

Read on demand by `skills/dotnet-selfeval/SKILL.md`'s Step 3, which names the analyses and routes
here for how to run each. Split out so the skill stays under the ~5k-token budget that
auto-compaction truncates it at — a run needs all of them, and a truncated skill would silently lose
the last ones.

This file covers **3a–3d, the four that read probe measurements**. Step 3e, the guidance-reasoning
audit, reads no probes and is written out in the skill itself; nothing here applies to it.

Every analysis below assumes the probe matrix has already run with per-probe `taskId`s (Step 0), so each
probe's `(calls, tokens)` is recoverable from one `get_retrieval_metrics(groupBy: "task", since: <today>)`
call — with the date bound, without which ids reused from a previous run report both runs' totals.

## 3a · Routes: was the same outcome reachable with fewer calls or fewer tokens?

Score a *route to a stated outcome*, not a single call. Each route costs **(calls, tokens)** — both
matter, and they trade against each other: a two-call route that costs fewer tokens than a one-call
route is usually the better one, but say so explicitly rather than ranking on tokens alone.

A cheap route that forced a follow-up call did not answer; its true cost is the sum of both calls. Record
that honestly — a route that looks cheapest but never suffices is the most expensive finding of all,
because the documentation recommending it is wrong.

| Outcome wanted | Cheap route | Expensive route |
| --- | --- | --- |
| What is this symbol for? | `search_index(include: "summary:full")` — answered by the search itself | `search_index` → `get_symbol(source: "full")` |
| What does it do, in more detail? | `get_symbol(include: "xmlDoc,bodyOutline")` | `get_symbol(source: "full")` |
| What happens near line N of a long member? | `bodyOutline` → `get_symbol(source: "code@N-M")` | `get_symbol(source: "full")` |
| What is its signature? | default `include` | `source: "full"` |
| What shape are these five symbols? | one `get_symbol(symbols: [...])` — **calls only; see below** | five `get_symbol` calls |
| Who calls it (just the list, one hop)? | `get_call_hierarchy(maxDepth: 1)` | `get_references` |
| Where exactly is it called (file/line/snippet)? | `get_references` | repeated file reads |
| How much does changing it ripple? | `get_call_hierarchy(includeTree: false)` — works on a **type** root too, whose depth-1 children are its referencing members | full tree, or `get_references` and counting |
| What does *this type* implement? | `get_symbol(include: "interfaces")` — one hop, no traversal | `get_type_hierarchy` |
| What implements *this interface*? | `get_references(direction: "implementations")`, or `search_index(implements:)` when a name filter narrows it further | `get_type_hierarchy` |
| The full base chain **and** every implementer | `get_type_hierarchy` — this is what it is for | repeated `get_symbol(include: "baseType")` hops |
| How does X reach Y? | `get_call_slice` | repeated `get_references` hops |

The batch row wins on calls always, and on tokens conditionally — measure it, don't assume either way.
What decides it is whether `shared` can hoist: a **homogeneous** batch (same `kind`, `origin`,
`modifiers`, one `include`) hoists those fields out of every entry and has measured *cheaper* than the
separate fetches from about n=5 — 842 tokens against 872 on five types, twice, on two consecutive runs.
A **mixed-kind** batch hoists little, and the per-entry `results[i]` nesting then costs more than it
saves until roughly n=8–10. So state the condition when reporting this row rather than a flat figure,
and never let a stale figure stand: this note itself claimed the opposite for two runs before it was
re-measured.

Report each row as `cheap (c calls, t tokens) → expensive (c, t)`. A row where the "expensive" route is
actually cheaper, or where the cheap route did not answer, is worth more than every row that confirms
the ladder — it means the routing in the `dotnet-read`/`dotnet-write` tool tables (or the tool's own
`[Description]`) is wrong, which is a `[bug]` in the docs rather than a `[warning]` about tokens.

## 3b · Redundancy: does the response restate what the caller already held?

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

## 3c · Noise: what could be said once instead of many times?

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

## 3d · Advice: does a field that tells the caller what to do next actually pay?

Most response fields are facts. A few are **advice** — they exist only to change the caller's next call.
There are three, and they are not equally settled:

- **`search_index`'s `read` column** (`mem`/`out`/`code`/`all`, absent when the default fetch is
  already right) — the real advice field, and the one to price first. Its thresholds are admitted
  guesses: `ReadAdvice.LargeDeclaration` (60 lines) decides when the no-intent path speaks at all,
  and the `intent` values (`edit`/`logic`/`surface`) override it entirely. Both were set by argument,
  not by measurement, and this analysis is what should move them.
- **`get_symbol`'s `guard` block** on an unsliced `source` request past `ResponseGuard.LineThreshold`
  (500 lines) — advice that also *withholds* the answer, so a wrong fire costs a whole round trip
  rather than a few tokens. Price the threshold and the cost of the extra hop separately.
- **`search_index`'s `shape` column** (`P`/`M`/`N`/`L`/`O`/`D`/`C`/`A`, each emitted at its real value
  whenever non-zero and applicable; semantics in `docs/tools/search_index.md`) — facts a caller is
  expected to derive advice *from*. Now that `read` states the derivation outright, the open question
  about `shape` is whether it still earns its per-row cost alongside it, or has become 3b's
  `unconsulted`.

Advice is the only field class that can be *wrong* rather than merely expensive, so it gets its own
test: follow it, ignore it, measure both. Nothing is threshold-gated in `shape`, so the question there
is not "did the label fire correctly" but "at what value does following it start to pay" — report the
crossover, not a verdict.

Take a `search_index` result with at least 8 hits spanning labelled and unlabelled rows — Family B's
matrix keeps one for this. For each hit, run **both** routes to the same stated outcome and record
`(calls, tokens)`:

| Label | Outcome wanted | Route it advises | Compare against |
| --- | --- | --- | --- |
| `read: mem` | what is on this type | `include: "members"` | `source: "full"` |
| `read: out` | what this member does | `bodyOutline` → `source: "code@a-b"` | `source: "code"` whole |
| `read: code` | what this member does | `source: "code"` | `source: "full"` |
| `read` absent on a hit | anything | the default `include` | whichever labelled route its `shape` would have implied |
| `intent: "logic"` vs. omitted | behaviour, not docs | whatever `read` then says | the same query with no `intent` |
| `guard: large_source` | what is in this declaration | the served `members`/`bodyOutline`, then a slice | the repeat call that returns the whole source |
| `L…` with a large `O…` | what this member does | `bodyOutline` → `source: "code@a-b"` | `source: "full"` |
| `L…` with a small `O…` | what this member does | `source: "code"` whole | `bodyOutline` → `source: "code@a-b"` |
| `M…` | what is on this type | `include: "members"` | `source: "full"` |
| `N…` | what is nested inside | `include: "members"` | `source: "full"` |
| `D…` | the implementation only | `source: "code"` | `source: "full"` |
| `C…` | what the body does | `source: "code-comments"` | `source: "code"` |
| `A…` | which attributes it carries | `include: "attributes"` | `source: "full"` |
| small `L`, nothing else | what this does | default `include` | any labelled route above |

Four outcomes, and **three of them are findings**:

- **Paid** — the advised route cost fewer tokens for the same outcome. Report the ratio; the confirming
  case, and the only one that is not a finding.
- **Cost more** (false positive) — the label fired and following it was worse. `[bug]`: misleading
  advice is strictly worse than none, because a caller cannot tell the two apart without measuring.
- **Owed but absent** (false negative) — an *unlabelled* hit where the advised route would still have
  won materially. That is a threshold set too high, not a per-hit accident; report the hit's real
  numbers so the constant can be retuned against evidence rather than taste.
- **Unactionable** — the label fired but no different call follows from it. `[warning]`: a fact wearing
  advice's clothing, belonging under 3b or 3c instead.

Check each label against ground truth too, which is cheap for a counting label: `D` must equal the number
of lines `source:full` carries that `source:code` does not, and `L` must equal `endLine - line + 1`. A
label that mispredicts its own fetch is a `[bug]` before any token argument is reached.

**Rank by expected value, not best case.** A label saving 80% on the 5% of hits that carry it is a
smaller win than one saving 15% on every hit — multiply the saving by the census fraction that actually
carried the label, and state that fraction. A label almost nothing triggers is a `[message]` about the
threshold, however good its best case looks.
