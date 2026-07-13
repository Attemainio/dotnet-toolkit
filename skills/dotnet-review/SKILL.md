---
name: dotnet-review
description: Use when the user asks to review C#/.NET code, check a PR/diff, look for naming or styling issues, assess hot/cold-path performance, find dead code/duplication, or review XML documentation. Delegates to the plugin's four review agents rather than reviewing inline, since they run with fresh context and read the project's reference docs in docs/.
---

# Delegating to the dotnet-toolkit review agents

This plugin ships four specialized review subagents. Each starts with **no prior context of the
project** — it reads code and the shared `docs/` reference (styling, naming, best practices, performance,
XML documentation, common anti-patterns, and `docs/review-workflow.md`'s shared process) fresh, like a
senior developer seeing the codebase for the first time — and reports findings without editing anything.
Route review requests to them instead of reviewing inline yourself; they read the actual reference docs
(or a consuming repo's overrides under `.claude/dotnet-toolkit/`) that you only have a summary of.

All four have the plugin's full read-side MCP toolset: Roslyn queries (`find_symbol`, `find_references`,
`find_implementations`, `get_symbol`, `diagnostics`), structure queries (`project_tree`, `list_folder`,
`outline`), and **read-only** devlog access (`devlog_search`, `devlog_get` — never `devlog_add`; they check
prior recorded decisions for context, they don't record new ones).

## Which agent(s) to launch

| Request shape | Launch |
|---|---|
| "Review this code" / "review this PR" / naming or style feedback / general code-quality check | `dotnet-reviewer` |
| Code that's loop-heavy, request-handling, tick-based, or the user asks about performance/allocations | `dotnet-reviewer` + `dotnet-performance` |
| "Find dead code" / "find duplication" / cleanup / tech-debt pass | `dotnet-refactor-cleaner` (add `dotnet-reviewer` too if the request is a general "review," not cleanup-specific) |
| New/changed public API surface, or "review the docs/comments on this" | `dotnet-doc-reviewer` (add `dotnet-reviewer` too for a general review that happens to touch public API) |
| A full/comprehensive review with no narrower scope stated | All four, in parallel |

Launch multiple agents in a single message (parallel tool calls) when more than one applies — they're
independent and read-only, so there's no ordering dependency.

## What to tell each agent

Every one of the four needs the same context from you (per `docs/review-workflow.md`'s review-modes
section, which they each read themselves — you just need to supply the specifics):
- **Mode**: diff (changed files vs. a stated baseline — say what the baseline is: `main`, last commit,
  uncommitted working tree) or scope (an entire folder/project as a unit). Leave unstated to let the agent
  apply its own default (diff for three of the four; `dotnet-refactor-cleaner` defaults to scope).
- **Scope**: which files/folders/project, not the whole solution unless asked.
- Any hot/cold-path hint you already know, if launching `dotnet-performance` — saves it re-deriving
  something you already established earlier in the conversation.

## Merging results

Each agent returns findings in the same severity-tagged format (🔴/🟡/🔵, grouped by file, totals line —
defined once in `docs/review-workflow.md`). When more than one ran:
- Merge by file, not by agent — a reader wants everything about `OrderService.cs` together.
- Findings that duplicate across two agents' output (rare, since each stays in its lane and defers with
  `[see: dotnet-agent-name]` tags per `docs/review-workflow.md`) — keep the more specific one, drop the
  deferred restatement.
- Preserve each finding's severity and file:line exactly as reported; don't re-summarize away specifics.

## What these agents will never do

None of the four have `Edit`/`Write` tool access, and none call `devlog_add` — they cannot modify code or
record devlog entries even if asked to. If the user wants findings actually applied or a devlog entry
recorded, that's your job after reviewing what they reported.
