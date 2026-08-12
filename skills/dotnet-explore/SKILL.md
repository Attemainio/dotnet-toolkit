---
name: dotnet-explore
description: Use before changing C#/.NET code when the set of symbols the task touches is not already known — surveying an unfamiliar subsystem, assessing how a new feature lands in the existing codebase, or finding out what a change would break. Delegates the wide search_index/get_references sweep to the plugin's dotnet-explore subagent, which spends those responses in its own context and hands back symbolIds, file:line use sites and the blast radius. Read-only: it cannot edit.
---

# Delegating the sweep to dotnet-explore

This plugin ships one read-only navigator subagent, `dotnet-explore`. It turns a prose task into the
symbols it is about: `symbolId`s, use sites, affected files, and how far a change would reach. It
never judges code and it cannot change it — the invoking agent decides what to do with the map.

**Why delegate rather than fan out yourself.** A wide `search_index` plus a `get_references` per
candidate is the single most expensive shape of read this server produces, and almost all of those
tokens are scaffolding you throw away once you know the answer. The agent pays them in its own
context and returns a report a fraction of the size.

## When to invoke it — and when not to

**Invoke it** when the symbol set is genuinely unknown: an unfamiliar subsystem, "how would this
feature land", "what breaks if I change this", a review or refactor whose boundary you cannot yet
name.

**Skip it** when:

- **The symbol is already known.** A two-call lookup is not what this is for; use `dotnet-read`.
- **The next step needs a `contentVersion`.** The agent is instructed never to report one — it is an
  edit lease that goes stale the moment anything moves, and a patch built on a relayed one gets
  `stale_base` at best and a silent revert at worst. You fetch your own with `get_symbol`.
- **The answer is in a non-C# file.** `.csproj`, `.json`, `.md`, `.editorconfig` are out of its
  scope; it will say so and stop.

## Step 0 — check readiness before launching

**Call `workspace_status` yourself, before spawning.** The agent checks too, but a cold or degraded
workspace wastes an entire agent run:

- Still loading, or `index_only` → semantic results are unavailable, **not empty**. Wait, or accept a
  syntax-tier map and say so.
- `degraded` (projects failed to load) → the map may be silently **wrong**. Fix the build and
  `reload_workspace` before spawning, or don't spawn.
- Just did a `git pull`/`checkout`/rebase, or added or deleted a `.cs` file →
  `reload_workspace(scope: "all")` first.

## What to tell it

Everything you state is context it does not have to re-derive, and re-derivation is the expensive
part of a cold start.

- **The task in prose** (required): what someone is about to do to this codebase — not "find
  `FooService`", but "we want to add cancellation to the training loop".
- **Any symbol, file, or project you already know is involved.** It will start from there instead of
  guessing search terms.
- **Depth**: "where is this" (a one-hop map, ~8 calls) vs. "what breaks if I change this" (full blast
  radius, up to its 20-call ceiling). Transitive reach costs real tokens — ask for it only when the
  question is about ripple.
- **A `taskId` you want it to use**, if you plan to read its exact cost back with
  `get_retrieval_metrics(groupBy: "task")`. Otherwise it mints its own and reports it.

## Parallelism

One instance per **independent question**, launched in a single message. Do not split one question
across instances — the second would re-derive the first's search terms from scratch, and each cold
start re-pays the whole baseline. Two genuinely separate subsystems is two instances; one subsystem
explored "twice as thoroughly" is one.

## Reading its report

Fixed sections: **Target**, **Entry points**, **Blast radius**, **Affected files**, **What would need
to change**, **Suggested next calls**, **Not covered**.

- **`symbolId`s are the payload** — paste them straight into `get_symbol` or `validate_patch`. A
  `symidx_` prefix is provisional (syntax tier) and a `symfb_` one is not a fetch target at all (a
  local function, a lambda, a symbol with no doc-comment id); neither is usable for editing, and the
  report flags those on the row.
- **Read `Not covered` first, not last.** It is mandatory and carries `limitedBy` verbatim, budget
  stops, non-C# files the answer touches, and any ambiguity it resolved on your behalf. A map that
  says where it ends is useful; one that looks complete is dangerous.
- **`Suggested next calls` names the write path but never performs it.** Verify a suggested call
  against the actual schema before running it.
- **It reports locations, not conclusions.** No quality judgments, no design opinions, no proposed
  diffs. If it offered one anyway, treat it as noise.

## Then what

The map is the input to a change, not the change. Invoke **`dotnet-write`** next: step 1 of its loop
(fetch with `get_symbol(include: "all")` for your own `contentVersion`) is exactly the step the agent
deliberately did not do for you.
