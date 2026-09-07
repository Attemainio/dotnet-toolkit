---
name: dotnet-guard-audit
description: Use when asked to check whether this plugin's own guard hooks actually enforce what they claim — "try to break the .cs guards", "red-team the hooks", "does the read guard actually fire on X", "audit guard-cs-edit/read/bash-read/bash-write for bypasses", "can you get an edit past validate_patch through Bash". Launches the dotnet-guard-redteam agent, which tries a battery of known and improvised bypasses (spaced paths, symlinks, copies, variable expansion, writing to a .cs file through Bash instead of Edit/Write) with the guards left ARMED — the opposite of dotnet-performance, which needs them suspended. The agent only reports; fixing anything it finds is this session's job, not the agent's.
---

# Auditing the guard hooks for bypasses

## Host adapter

Claude Code can launch `dotnet-guard-redteam` directly. In Codex, launch a general worker and begin
its brief with: "Read `<pluginRoot>/agents/dotnet-guard-redteam.md` and follow its Markdown body;
ignore only the Claude-specific YAML frontmatter." Use the canonical file by reference; do not restate
its test battery in the launch prompt.

`dotnet-performance` measures what the plugin costs when the guards are suspended. This skill asks a
different question: **with the guards left exactly as armed as a real session meets them, does a call
that should be governed by one actually get caught?** The two must never run in the same window — one
needs the guards down, this one needs them up, and a red-team result taken while guards happen to be
suspended for an unrelated reason is not a result at all, just an unenforced repo behaving as expected.

## Why a dedicated agent, not the main session poking at it

The same reasoning `dotnet-performance` uses for its probes applies here for a different reason: this
session's own context already knows the shape of a real gap (the spaced-path bug this plugin has
already found and documented), and testing your own hypothesis is a weaker test than a fresh agent
working from the same battery of attempts with no investment in finding — or not finding — anything in
particular. `dotnet-guard-redteam` is deliberately self-contained and briefed with the list of attempts
directly, rather than inventing its own, so a run is repeatable and comparable across time.

## Step 0 — confirm the guards are actually armed

Call `workspace_status`. If its `hookGuards: SUSPENDED` line is present, **stop** — either wait out the
suspension or ask the user whether it is safe to restore now (another session may be relying on it).
Launching the red-team agent during a suspension window produces a report where everything "passes"
trivially, which is worse than no report: it reads as clean enforcement when nothing was actually being
tested.

Also confirm `workspace: loaded` (not `degraded`) — an attempt that fails because the workspace itself
is unhealthy is not a guard finding.

## Step 1 — launch the agent

```
Agent(subagent_type: "dotnet-guard-redteam", description: "Red-team the .cs guard hooks")
```

No question matrix to build and no prompt to hand-author: the agent's own file carries its full battery
of attempts, the boundaries (never leave a change behind, report don't fix), and the report format.
Nothing here should be re-typed into the launch prompt — a copy would drift from the agent's own file.

## Step 2 — read the report as findings, not as a to-do list you execute blindly

The agent's report ranks gaps by stakes: an unguarded write (through `Edit`/`Write`, or through `Bash`
in a shape `guard-cs-bash-write`'s `FindWriteTargetCsArgument` does not recognize) outranks a missed
advisory note on the read side, which outranks an already-documented gap like the spaced-path tokenizer
issue. Triage in that order.

**Fixing a confirmed gap is this session's job, using `dotnet-write`** — the agent that found it must
not also be the one patching `Hooks/*.cs`, since it was deliberately given no stake in whether it found
something. A write-side gap (item 10 in the agent's battery) is the one worth treating as urgent: it
means an edit can reach disk with no compile check and no development-log entry, which is exactly what
`guard-cs-edit`/`guard-cs-bash-write` exist to prevent. `guard-cs-bash-write` closed the `Bash` half of
this on 2026-08-21 for the write shapes it names (redirects, `sed -i`/`perl -i`, `tee`, `cp`/`mv`/`dd`);
item 10 still runs on every audit because a scanner regression or a genuinely new write shape is exactly
what this agent exists to keep re-discovering, not something a single fix retires permanently.

## Step 3 — write the report

`.claude/dotnet-toolkit/redteam/<date>-<HHmmss>.md` — gitignored by the same
`.claude/dotnet-toolkit/.gitignore` that covers `review/`, `eval/` and `perf/`, for the identical
reason: a run describes this plugin's enforcement as it stood on one date, and a stale one committed to
the repo would be read as current long after a gap it found was fixed. Carry the agent's report
verbatim, plus one line at the top naming whether guards were confirmed armed before launch (Step 0)
and whether any confirmed gap was fixed in the same session or left open for the user.

## Boundaries

- **Never launch `dotnet-guard-redteam` directly outside this skill.** It has no question list or
  report format of its own beyond what its file already carries, but the armed-guards precondition in
  Step 0 is this skill's job, not something the agent checks for itself.
- **Never run this alongside a guard suspension** (`dotnet-performance`, or a manual
  `set_hook_guards(state: "suspend")` left open from another task). Check `workspace_status` fresh
  immediately before launching, not from memory of an earlier check this session.
- **A finding here is about this plugin's hooks, never about the consuming repo's own code** — same
  boundary `dotnet-performance` and `dotnet-selfeval` hold.
