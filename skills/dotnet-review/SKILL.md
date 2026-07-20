---
name: dotnet-review
description: Use when the user asks to review C#/.NET code, check a PR/diff, look for naming or styling issues, assess hot/cold-path performance, find dead code/duplication, review XML documentation, check test coverage, or do a security review. Delegates to the plugin's dotnet-code-review subagent (launched once per dimension) rather than reviewing inline, since it runs with fresh context and reads the project's reference docs in docs/.
---

# Delegating to dotnet-code-review

This plugin ships one review subagent, `dotnet-code-review`, that reviews exactly **one dimension per
invocation** — `correctness`, `performance`, `cleanup`, `docs`, `testing`, or `security` — stated as
`dimension: <name>` in its prompt. It starts with **no prior context of the project** — it reads code and
the shared `docs/` reference (styling, naming, best practices, performance, XML documentation, testing,
security, common anti-patterns, and `docs/review-workflow.md`'s shared process) fresh, like a senior
developer seeing the codebase for the first time — and reports findings without editing anything. Route
review requests to it instead of reviewing inline yourself; it reads the actual reference docs (or a
consuming repo's overrides under `.claude/dotnet-toolkit/`) that you only have a summary of.

It has the plugin's read-side MCP toolset: `search_index` to locate symbols, `get_symbol` for a symbol's
shape/members/source, `get_references` to trace callers, implementations and overrides semantically,
`get_scope` for what is callable at a point (including extension methods), `get_call_slice` for the
shortest path between two symbols, and `get_semantic_diff` for what a commit range actually changed and
whether any of it is breaking. It has no `Edit`/`Write` and no `validate_patch`, so it cannot change code.

Because it has `get_semantic_diff`, a review scoped to committed refs is worth stating as such — the
agent can then skip files a formatting-only commit merely touched. It reads git refs, so it cannot see
uncommitted work; for a working-tree review, state the file list instead.

Note: it can consult the development log with `search_log` before asserting a finding, so a pattern
recorded as a deliberate past decision is cited rather than re-flagged. The log only covers changes
applied through `validate_patch`, so decisions made outside that path leave no trace — if a finding
might reflect one, that context still has to come from you.

The `security` dimension has no dedicated static-analysis scanner behind it (no CVE/dependency check, no
taint tracking) — its findings come from reading source and tracing references the same way every other
dimension does. If the user needs a CVE/dependency-vulnerability scan specifically, say that's out of
scope rather than letting a `security` review imply it was covered.

## Which dimension(s) to request

| Request shape | Launch with `dimension:` |
|---|---|
| "Review this code" / "review this PR" / naming or style feedback / general code-quality check | `correctness` |
| Code that's loop-heavy, request-handling, tick-based, or the user asks about performance/allocations | `correctness` + `performance` |
| "Find dead code" / "find duplication" / cleanup / tech-debt pass | `cleanup` (add `correctness` too if the request is a general "review," not cleanup-specific) |
| New/changed public API surface, or "review the docs/comments on this" | `docs` (add `correctness` too for a general review that happens to touch public API) |
| "Is this tested" / "check test coverage" / new code with no tests visible | `testing` |
| "Security review" / auth, secrets, or input-handling code / pre-production hardening pass | `security` |
| A full/comprehensive review with no narrower scope stated | All six dimensions, in parallel |

Launch one `dotnet-code-review` invocation per dimension in a single message (parallel tool calls) when
more than one applies — they're independent and read-only, so there's no ordering dependency. Adding a
new dimension later is a change to `agents/dotnet-code-review.md`'s dimension table and a new `docs/*.md`
file — not a new agent to route to here.

## What to tell each invocation

Every invocation needs the same context from you (per `docs/review-workflow.md`'s review-modes section,
which it reads itself — you just need to supply the specifics):
- **`dimension`**: `correctness` | `performance` | `cleanup` | `docs` | `testing` | `security`, per the
  table above. Required — the agent defaults to `correctness` if you omit it, which is only right for the
  first row.
- **`mode`**: `diff` (changed files vs. a stated baseline — say what the baseline is: `main`, last commit,
  uncommitted working tree) or `scope` (an entire folder/project as a unit). Leave unstated to let the
  invocation apply its dimension's own default (diff for every dimension except `cleanup`, which defaults
  to scope).
- **Scope**: which files/folders/project, not the whole solution unless asked.
- Any hot/cold-path hint you already know, for a `performance` invocation — saves it re-deriving
  something you already established earlier in the conversation.

## Merging results

Every invocation returns findings in the same severity-tagged format (🔴/🟡/🔵, grouped by file, totals
line — defined once in `docs/review-workflow.md`). When more than one dimension ran:
- Merge by file, not by dimension — a reader wants everything about `OrderService.cs` together.
- Findings that duplicate across two dimensions' output (rare, since each stays in its lane and defers
  with `[see: dimension:<name>]` tags per `docs/review-workflow.md`) — keep the more specific one, drop
  the deferred restatement.
- Preserve each finding's severity and file:line exactly as reported; don't re-summarize away specifics.

## What this agent will never do

`dotnet-code-review` has no `Edit`/`Write` or `validate_patch` access — it cannot modify code even if
asked to, and it cannot record log entries. If the user wants findings actually applied, that's your job
after reviewing what it reported: apply them through `validate_patch` with an `intent`, which both
validates the change and records why it was made.
