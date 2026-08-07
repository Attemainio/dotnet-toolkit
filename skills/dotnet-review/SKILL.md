---
name: dotnet-review
description: Use when the user asks to review C#/.NET code, check a PR/diff, look for naming or styling issues, assess performance or concurrency, find dead code/duplication, review XML documentation, check test coverage, or do a security review. Delegates to the plugin's dotnet-code-review subagent — each instance reviews ALL quality aspects of one precisely stated scope, and large targets are partitioned into disjoint scopes reviewed by parallel instances — rather than reviewing inline, since it runs with fresh context and reads the plugin's standards files directly.
---

# Delegating to dotnet-code-review

This plugin ships one review subagent, `dotnet-code-review`. Each invocation reviews **all quality
aspects at once** — correctness, naming, styling, best practices, performance, concurrency, security,
testing, XML documentation, cleanup/duplication — over **one stated scope**. It starts with **no prior
context of the project**: it reads code and the shared standards under the plugin's `standards/`
fresh, like a senior developer seeing the
codebase for the first time, and reports findings without editing anything. Route review requests to it
instead of reviewing inline yourself — it reads the actual standards files, which you have not loaded.

## Step 0 — check workspace readiness before launching

**Call `workspace_status` yourself, before spawning anything.** Each instance starts cold and cannot
fix a broken workspace; launching several against one is a parallel waste, not a parallel review.

- **`workspace: degraded`, or projects under `failed:`** — semantic results may be silently **wrong**,
  not merely thin. A review run against it reports findings it cannot stand behind. Fix the build,
  `reload_workspace`, then spawn.
- **Still loading, or index-only** — `get_references`, `get_call_hierarchy`, `get_type_hierarchy` and
  `get_semantic_diff` return nothing rather than nothing-found, and the classic failure is an instance
  reporting live code as dead. Wait for it.
- **After a `git checkout`/`pull`/rebase, or a new or deleted `.cs` file** — `reload_workspace(scope:
  "all")` first, or every instance's line numbers are wrong.
- **Take `pluginRoot` from the same response** and state it in each instance's brief, so no instance
  has to rediscover where the standards live.

State the readiness verdict in the merged report. A review run on a degraded workspace is reported as
such, never as clean.

**It loads standards selectively.** The routing table is `<pluginRoot>/standards/index.md`, shared with
`dotnet-write`. Six files are read every time (`naming`, `styling`, `best-practices`,
`xml-documentation`, `antipatterns`, `security`); the other seven are read only when the retrieved code
matches their "When" condition in that table. This keeps each instance's
baseline down — the cost is paid once per parallel instance, so it dominates a multi-instance run — and
every report ends with a `Standards:` line naming what was loaded and what was not. Treat an
untriggered aspect as **not assessed**, not clean.

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
`agents/dotnet-code-review.md`'s scope-discipline section) and reports anything it notices outside as a
one-line `Outside scope:` note — check those notes against your partition to see whether another
instance already covered them.

## What to tell each instance

Anything you state here is context the instance does not have to re-derive — and because each instance
starts cold, re-derivation is the expensive part of a parallel run.

- **Scope** (required): the exact folder(s)/file list this instance owns.
- **`pluginRoot`** (required): the path from step 0, so the instance can reach
  `<pluginRoot>/standards/index.md` without a lookup of its own.
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
totals, then a `Standards:` line — defined once in `agents/dotnet-code-review.md`). When more than one
instance ran:

- Concatenate by scope — scopes are disjoint, so there is no per-file dedup to do; a reader wants
  everything about `OrderService.cs` together, and exactly one instance produced it.
- Collect the `Outside scope:` one-liners, drop those already covered by another instance's findings,
  and surface the rest (they point at code no slice owned).
- Sum the per-aspect totals across instances so the merged report states clean aspects explicitly.
- **Merge the `Standards:` lines, and don't flatten them into "clean".** Different instances load
  different triggered files, so an aspect can be assessed in one slice and untriggered in another. In
  the merged report, an aspect is clean only for the scopes that actually loaded its standard; name the
  scopes where it went unassessed. If an aspect was untriggered *everywhere* and the user asked for a
  full review, say so plainly — that is the one failure mode selective loading introduces, and it is
  only visible at merge time.
- Preserve each finding's severity, aspect tag, and file:line exactly as reported; don't re-summarize
  away specifics.

## What this agent will never do

`dotnet-code-review` has no `validate_patch` access — it cannot record log entries, and it is
instructed never to modify code. Note that this is **instruction, not sandboxing**: `memory: project`
makes the harness grant it `Write`/`Edit` for its own memory namespace, so its resolved tool list does
include them (see `docs/design/agents.md`). If the user wants findings actually applied, that's your
job after reviewing what it reported — **invoke `dotnet-write`** and apply them through
`validate_patch` with an `intent`, which both validates the change and records why it was made.
