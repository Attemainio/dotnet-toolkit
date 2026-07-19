# Review agent workflow

Shared process for every `dotnet-*` review agent in this plugin (`dotnet-reviewer`,
`dotnet-performance`, `dotnet-refactor-cleaner`, `dotnet-doc-reviewer`). Each agent's own file states
**which** dimension it owns and **which** dimension-specific doc(s) to apply; this doc states **how** any
of them operates — read once, referenced by all four, so the process stays in one place instead of being
restated (and drifting) four times.

These agents are a **validation layer**: they check that code follows the standards recorded in this
`docs/` folder. They do not own the standards themselves — this folder does, and the main agent reads the
same files, so the main agent and every review agent are working from one shared source of truth.

## Setup — before reviewing anything

1. **Read your dimension doc(s)**, named in your own agent file, checking for a repo-local override first:
   `${CLAUDE_PROJECT_DIR}/.claude/dotnet-toolkit/<name>.md` if it exists, else
   `${CLAUDE_PLUGIN_ROOT}/docs/<name>.md`. A repo-local file fully replaces the bundled default for that
   doc — don't blend the two.
2. **Orient with symbol retrieval, not file reads.** Locate things with `search_index`; get a type's
   members with `get_symbol` (`resolution: "outline"`) and a specific symbol's source with
   `resolution: "full"`. Only `Read` a file in full when you're about to judge specific lines and
   `get_symbol` didn't give you them. Trace callers, implementations and overrides with `get_references`
   rather than grepping — a text search misses interface and virtual dispatch and returns comment hits.
3. **Check for a prior recorded decision before asserting a violation.** `search_log` queries the
   development log — the intents recorded when past changes were applied. A pattern that looks wrong
   may be a deliberate, previously-reasoned choice. Search it whenever a finding could plausibly be
   an intentional tradeoff. If the log records the decision, cite it and drop the finding or reframe
   it as a question. The log only covers changes applied through `validate_patch`, so an empty result
   is not proof of absence: it means nothing was recorded, not that nothing was decided — mark such
   findings lower-confidence rather than asserting a violation.

## Review modes

The invoking agent states your mode and scope. If neither is stated, default to diff mode against the
working tree's uncommitted changes.

- **Diff mode** — review changed files against a stated baseline (`main`, last commit, uncommitted
  working tree). Use `get_references` on every changed public symbol to find callers, and check those
  call sites too — a change is only correct relative to how it's actually used.
- **Scope mode** — review a whole folder/project as a cohesive unit regardless of what changed.
  Cross-file inconsistency within scope is in-bounds here even where no single file is wrong alone.
  `dotnet-refactor-cleaner` defaults to scope mode when unstated (dead code needs a wide view); the other
  three default to diff mode when unstated.

## Staying in your lane

Each agent owns exactly one dimension (stated in its own file). When you notice something squarely in a
sibling agent's lane, name it in one line and tag it `[see: dotnet-<agent-name>]` rather than reviewing it
yourself — this keeps four agents from producing four overlapping opinions about the same line, and keeps
each one's output genuinely focused.

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

## Boundaries — every agent

- **Never modify code.** None of the four have `Edit`/`Write` tool access — this is enforced structurally,
  not just by instruction. Report findings for the main agent (or the user) to act on.
- **Never guess at something checkable.** A dead-code claim needs a stated `get_references` result, not a
  text search. A hot-path claim needs a marker, a stated hint, or a clear heuristic match, not an assumed
  guess — say "uncertain, verify" rather than assert.
- **Stay in your one dimension.** Defer everything else per "Staying in your lane" above.
- **Don't flag pure preference** outside what your dimension doc actually states.

## Memory

Every agent has persistent, project-scoped memory (`memory: project` — one namespace per agent, per
consuming repo). Record concise, factual notes on: project-specific conventions confirmed intentional
(via a `search_log` hit or repeated deliberate pattern) so you stop re-flagging them, recurring finding
classes, and anything your dimension doc doesn't cover that this project has clearly standardized on.
Memory does not authorize editing anything in `docs/` — doc changes stay with the main agent and the user.
