---
name: dotnet-review
description: Use when the user asks to review C#/.NET code, check a PR/diff, look for naming or styling issues, assess performance or concurrency, find dead code/duplication, review XML documentation, check test coverage, or do a security review. Delegates to the plugin's dotnet-code-review subagent — each instance reviews ALL quality aspects of one precisely stated scope, and large targets are partitioned into disjoint scopes reviewed by parallel instances — rather than reviewing inline, since it runs with fresh context and reads the project's standards in .claude/rules/.
---

# Delegating to dotnet-code-review

This plugin ships one review subagent, `dotnet-code-review`. Each invocation reviews **all quality
aspects at once** — correctness, naming, styling, best practices, performance, concurrency, security,
testing, XML documentation, cleanup/duplication — over **one stated scope**. It starts with **no prior
context of the project**: it reads code and the shared standards in the plugin's `.claude/rules/` (or a
consuming repo's overrides under `.claude/dotnet-toolkit/`) fresh, like a senior developer seeing the
codebase for the first time, and reports findings without editing anything. Route review requests to it
instead of reviewing inline yourself — it reads the actual standards files that you only have a summary
of.

**Parallelism is by scope, not by aspect.** For anything larger than a handful of files, partition the
target into disjoint slices and launch one instance per slice in a single message (parallel tool
calls). Every instance covers every aspect of its slice, so nothing is reviewed twice and no aspect is
silently skipped.

## Partitioning the scope

- **Small change / single folder** (≲ 10 files): one instance, whole target as its scope.
- **A project or large diff**: partition along natural seams — one instance per subsystem folder
  (`Workspace/`, `Store/`, `Tools/`…) or per project. Prefer seams that keep tightly-coupled files in
  the same slice, so an instance sees a whole unit.
- **A diff spanning several subsystems**: cluster the changed files by folder and give each instance
  one cluster *plus the shared baseline statement*.
- **Whole-solution audit**: one instance per project, or per top-level folder of a large project.

State each instance's scope **precisely** — an explicit folder path or file list, never "the rest" or
"everything else". Scopes must be disjoint: the same file in two scopes produces duplicate,
possibly-conflicting findings. Each instance stays strictly inside its slice (per
`docs/agent-reference.md`'s scope-discipline section) and reports anything it notices outside as a
one-line `Outside scope:` note — check those notes against your partition to see whether another
instance already covered them.

## What to tell each instance

- **Scope** (required): the exact folder(s)/file list this instance owns.
- **`mode`**: `diff` (changed files vs. a stated baseline — say what the baseline is: `main`, last
  commit, uncommitted working tree) or `scope` (the slice as a cohesive unit). If a baseline is
  relevant, state it for every instance identically.
- **`focus:`** (optional, exceptional): one or more aspects (`correctness`, `performance`,
  `concurrency`, `cleanup`, `docs`, `testing`, `security`) when the user *explicitly* asked for a
  narrow review ("security review only", "just check test coverage"). Omit it otherwise — the default
  is all aspects, and that default is the point: a full review that silently skipped concurrency or
  docs because nobody asked is the failure mode this design replaces.
- Any hot/cold-path hint you already know — saves the instance re-deriving something you established
  earlier in the conversation.

Because it has `get_semantic_diff`, a review scoped to committed refs is worth stating as such — the
instance can then skip files a formatting-only commit merely touched. It reads git refs, so it cannot
see uncommitted work; for a working-tree review, state the file list instead.

Note: it consults the development log with `search_log` before asserting a finding, so a pattern
recorded as a deliberate past decision is cited rather than re-flagged. The log only covers changes
applied through `validate_patch`, so decisions made outside that path leave no trace — if a finding
might reflect one, that context still has to come from you.

The `[security]` aspect has no dedicated static-analysis scanner behind it (no CVE/dependency check, no
taint tracking) — its findings come from reading source and tracing references like every other aspect.
If the user needs a CVE/dependency-vulnerability scan specifically, say that's out of scope rather than
letting a review imply it was covered.

## Merging results

Every instance returns findings in the same format (aspect tag + 🔴/🟡/🔵, grouped by file, per-aspect
totals — defined once in `docs/agent-reference.md`). When more than one instance ran:

- Concatenate by scope — scopes are disjoint, so there is no per-file dedup to do; a reader wants
  everything about `OrderService.cs` together, and exactly one instance produced it.
- Collect the `Outside scope:` one-liners, drop those already covered by another instance's findings,
  and surface the rest (they point at code no slice owned).
- Sum the per-aspect totals across instances so the merged report states clean aspects explicitly.
- Preserve each finding's severity, aspect tag, and file:line exactly as reported; don't re-summarize
  away specifics.

## What this agent will never do

`dotnet-code-review` has no `Edit`/`Write` or `validate_patch` access — it cannot modify code even if
asked to, and it cannot record log entries. If the user wants findings actually applied, that's your job
after reviewing what it reported: apply them through `validate_patch` with an `intent`, which both
validates the change and records why it was made.
