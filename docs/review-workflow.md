# Review agent workflow

Shared process for `dotnet-code-review`, this plugin's single review subagent. It is launched once per
dimension (`correctness`, `performance`, `cleanup`, `docs`, `testing`, `security` — see the table in
`agents/dotnet-code-review.md`), in parallel when more than one dimension applies to a request. That agent
file states **which** dimension maps to **which** doc(s); this doc states **how** any invocation operates,
regardless of dimension — read once, referenced by every invocation, so the process stays in one place
instead of drifting per dimension.

Each invocation is a **validation layer**: it checks that code follows the standards recorded in this
`docs/` folder. It does not own the standards themselves — this folder does, and the main agent reads the
same files, so the main agent and every review invocation are working from one shared source of truth.

## Setup — before reviewing anything

1. **Read the doc(s) for your stated `dimension`**, per the table in `agents/dotnet-code-review.md`,
   checking for a repo-local override first:
   `${CLAUDE_PROJECT_DIR}/.claude/dotnet-toolkit/<name>.md` if it exists, else
   `${CLAUDE_PLUGIN_ROOT}/docs/<name>.md`. A repo-local file fully replaces the bundled default for that
   doc — don't blend the two.
2. **Orient with symbol retrieval, not file reads.** Locate things with `search_index`; get a type's
   members with `get_symbol` (`include: "members"`) and a specific symbol's source with
   `include: "all"`. Only `Read` a file in full when you're about to judge specific lines and
   `get_symbol` didn't give you them. Trace callers, implementations and overrides with `get_references`
   rather than grepping — a text search misses interface and virtual dispatch and returns comment hits.
   Three more tools answer questions symbol lookup cannot, and each replaces a guess with a fact:
   `get_scope` (what is actually callable at a line, including extension methods — use it before
   claiming a helper doesn't exist or that one should be added), `get_call_slice` (the shortest call
   path between two symbols — use it to establish whether something is reachable, or how a value gets
   somewhere, instead of walking outwards with repeated `get_references`), and `get_semantic_diff`
   (what a range of commits changed semantically, with API impact per symbol).
3. **Check for a prior recorded decision before asserting a violation.** `search_log` queries the
   development log — the intents recorded when past changes were applied. A pattern that looks wrong
   may be a deliberate, previously-reasoned choice. Search it whenever a finding could plausibly be
   an intentional tradeoff. If the log records the decision, cite it and drop the finding or reframe
   it as a question. The log only covers changes applied through `validate_patch`, so an empty result
   is not proof of absence: it means nothing was recorded, not that nothing was decided — mark such
   findings lower-confidence rather than asserting a violation.

## Review modes

`mode` (diff/scope) is independent of `dimension` (correctness/performance/cleanup/docs/testing/security)
— the invoking agent states both. If mode isn't stated, default to your dimension's default mode from the
table in `agents/dotnet-code-review.md` (diff for every dimension except `cleanup`, which defaults to
scope).

- **Diff mode** — review changed files against a stated baseline (`main`, last commit, uncommitted
  working tree). Start with `get_semantic_diff` against that baseline: it reports exactly which symbols
  moved and which are breaking, and it is trivia-blind, so a formatting- or comment-only commit reports
  no change and needs no correctness review at all. Then use `get_references` on every changed public
  symbol to find callers, and check those call sites too — a change is only correct relative to how it's
  actually used. `get_semantic_diff` works from git refs, so it cannot see uncommitted work; fall back
  to the stated file list when the baseline is the working tree.
- **Scope mode** — review a whole folder/project as a cohesive unit regardless of what changed.
  Cross-file inconsistency within scope is in-bounds here even where no single file is wrong alone.
  `cleanup` defaults to scope mode when unstated (dead code needs a wide view).

## Staying in your lane

Each invocation owns exactly the one dimension stated in its prompt. When you notice something squarely
in a different dimension, name it in one line and tag it `[see: dimension:<name>]` rather than reviewing
it yourself — this is what keeps several parallel invocations, one per dimension, from producing
overlapping opinions about the same line, and keeps each one's output genuinely focused.

## Output format

For each finding:
- **File and line**: `path/to/File.cs:42`
- **Severity**: 🔴 Bug/must-fix, 🟡 Convention violation or needs verification, 🔵 Suggestion.
- **What**: the issue, concisely.
- **Why**: why it matters in this code specifically — not generic advice restating the doc.
- **Fix**: describe the remedy; a short snippet when the fix is unambiguous.
- **How to verify** *(performance 🟡 findings only)*: a specific counter, trace, or benchmark setup.

Group findings by file, ordered 🔴 → 🟡 → 🔵. End with a totals line. If a scope is clean, say so in one
sentence — don't pad with praise, and don't manufacture findings to justify having run.

## Boundaries — every invocation

- **Never modify code.** `dotnet-code-review` has no `Edit`/`Write` tool access — this is enforced
  structurally, not just by instruction. Report findings for the main agent (or the user) to act on.
- **Never guess at something checkable.** A dead-code claim needs a stated `get_references` result, not a
  text search. A hot-path claim needs a marker, a stated hint, or a clear heuristic match, not an assumed
  guess — say "uncertain, verify" rather than assert.
- **Zero callers is not proof of dead code.** The count is of *static call sites in the loaded solution*,
  so anything a framework invokes reports only whatever happens to call it by name as well: reflection-
  registered entry points, DI-resolved implementations, serialization targets, `[Theory]` data, event
  handlers wired by attribute. The count is then incidental rather than meaningful — in this plugin,
  `HistoryTools.SearchLog` reports 0 callers and `ContextTools.GetSymbol` reports 3, purely because tests
  invoke one directly and not the other. Both are equally live, and neither number says so. A registration
  attribute on the symbol (or on its type) is the signal that the count is not the answer. Before claiming
  removal, check whether something reaches it another way — `get_call_slice` from a plausible entry point,
  or such an attribute — and if it is framework-invoked, say so and drop the finding.
- **Stay in your one dimension.** Defer everything else per "Staying in your lane" above.
- **Don't flag pure preference** outside what your dimension doc actually states.

## Memory

`dotnet-code-review` has persistent, project-scoped memory (`memory: project` — one namespace per
consuming repo, **shared across all dimensions** since every invocation is the same agent). Prefix every
note with the dimension it applies to (e.g. `[performance] ...`) — an invocation reading memory written
under a different dimension should skip notes not tagged for its own. Record concise, factual notes on:
project-specific conventions confirmed intentional (via a `search_log` hit or repeated deliberate pattern)
so you stop re-flagging them, recurring finding classes, and anything your dimension doc doesn't cover
that this project has clearly standardized on. Memory does not authorize editing anything in `docs/` —
doc changes stay with the main agent and the user.
