---
name: dotnet-architecture
description: >-
  Use for a SYSTEM-LEVEL review of C#/.NET code — "what is stale here", "is this still needed", "clean
  up after this refactor", "find backwards-compatibility code", "which tests are outdated", "where
  should this be one abstraction", "consolidate", "simplify the architecture" — and after a substantial
  architectural change (a removed engine or subsystem, a new subsystem, a cross-project refactor).
  Launches the dotnet-architecture-review subagent. Use dotnet-review instead when the question is
  whether code is correct, safe or conventional; the two can run side by side.
---

# Delegating to dotnet-architecture-review

`dotnet-architecture-review` reviews **what the system still needs and how it could be simpler**:
staleness, compatibility layers, scattering, abstraction fit, stale tests. `dotnet-code-review`
(`dotnet-review`) judges each piece of code on its own merits against the standards; this agent judges
the pieces against each other and against what uses them. Both are read-only and report; acting on a
finding is the invoking agent's job, after the user agrees.

## Host adapter

Claude Code launches the bundled `dotnet-architecture-review` agent directly. Codex does not install
custom-agent registrations: there, launch the built-in read-only explorer and start its brief with
"Read `<pluginRoot>/agents/dotnet-architecture-review.md` and follow its Markdown body; ignore only the
Claude-specific YAML frontmatter." The canonical brief stays under `agents/`.

## Step 0 — workspace readiness

Same gate as `skills/dotnet-review/SKILL.md` Step 0, and it matters more here: **every finding this
agent makes is a usage claim**, so a workspace that is not `loaded` turns live code into "stale" code.
Call `workspace_status` yourself before launching; take `pluginRoot` from it for the briefs.

## Partitioning

Coarser than a code review — staleness and scattering only show across files:

- **One project or one subsystem** (a removed engine and its callers, a feature folder): one instance.
- **A solution**: one instance per project, or per large top-level folder of a big project. Keep a
  production folder and its test folder in the **same** slice, so the `[tests]` lens sees both sides.
- **After a change**: diff mode with the baseline, one instance per cluster of changed folders.

Scopes are disjoint and stated as explicit paths.

## What to tell each instance

- **Scope** and **`pluginRoot`** (required); **mode** (`scope` by default, or `diff` with its baseline).
- **Who consumes the code.** The agent assumes a single user and no external consumers unless told
  otherwise. Say so if a library is published, an API has outside clients, or old files on disk must
  keep loading.
- **Recorded decisions** the agent cannot find itself (it has no `git`): compatibility the user already
  chose to keep, families the user confirmed as capability, a decision log path.

## Merging and acting

Concatenate by scope; collect `Outside scope:` lines; sum the totals. Then sort every finding by its
**action class**:

- **`auto`** (behaviour-identical, low risk) — the consuming repo's policy decides whether these are
  applied without asking. Apply them through `dotnet-write`, then `dotnet build` and the tests.
- **`approve`** — present as one batch for the user to accept or decline item by item. Nothing is
  removed without a yes.
- **`ask`** — every backwards-compatibility layer and every uncertain capability verdict, as the
  question the agent phrased.

Present `[gate]` proposals (an analyzer rule to enable, a ratchet test) as `approve` items: once on, a
whole class of finding stops needing review. Report what was applied, what was approved, what was
declined, and record declined or kept items where the next run will see them (the agent's memory, or the repo's decision log), so they are not raised
again.

## Where the report goes

`.claude/dotnet-toolkit/review/<UTC-date>-<HHmmss>-architecture-<target>.md`, the same git-ignored
folder `dotnet-review` uses, and answer inline. Not an Artifact unless the user asks.

## What this agent will never do

Edit code, or propose removing a capability family. Like `dotnet-code-review`, its `Write`/`Edit` come
from `memory: project` and are limited by instruction to its memory namespace.
