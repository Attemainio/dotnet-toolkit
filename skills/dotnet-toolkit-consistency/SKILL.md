---
name: dotnet-toolkit-consistency
description: Use when auditing whether this plugin's own docs, skills, agent, rules, hooks, CLAUDE.md, and README.md are in sync with the actual MCP tool implementation under src/DotnetToolkit.McpServer — after adding, removing, renaming, or changing the signature/return shape/[Description] of a tool, after adding or editing a hook/script, or any time something in a doc/skill/rule looks stale, contradicts the code, or a new file was added that nothing else references. Audits Tools/*.cs as ground truth against every file that describes the tool surface, and reports or fixes exact drift, file by file.
---

# Auditing plugin self-consistency

This plugin teaches Claude to use its own MCP tools through a scattered set of hand-maintained files —
skills, an agent definition, rules, hooks/scripts, `docs/*.md`, `CLAUDE.md`, `README.md`. None is
generated from the code, so any of them can silently drift from
`src/DotnetToolkit.McpServer/Tools/*.cs` the moment a tool changes and the doc edit is skipped. This is
the audit pass that catches that, run as its own task rather than trusted to unrelated work.

**Ground truth is always the code.** Every check starts from `Tools/*.cs` and treats every other file as
a claim to verify against it, never the reverse: if a doc and the code disagree, the doc moves.

## Step 0 — enumerate the actual tool surface

Get the current, complete list directly from the source — don't trust any doc's existing tool list,
including this skill's own examples below; that is exactly the kind of claim being audited. **Not
`grep`**: `guard-cs-bash-read.sh` blocks shell reads of solution `.cs` files, and rightly — this is a
symbol query, not a text search.

```
get_symbol(symbols: ["ContextTools", "FlowTools", "GraphTools", "HistoryTools",
                     "PatchTools", "MetricsTools", "ServerTools"], include: "members")
```

Each `[McpServerToolType]` class's public methods **are** its tools, one per method, and the response
gives every parameter with its default — more than the `grep` gave, since signatures come with it.
Confirm the class list itself first with `search_index(query: "Tools", pathPrefix: "src/DotnetToolkit.McpServer/Tools")`,
so a **new** tool-group file is caught rather than assumed away by reusing the list above; a new
`Tools/*.cs` file (a new tool *group*, not just a new tool) is itself a finding — see Step 6. Note that
not every file under `Tools/` is a tool group: `ToolTelemetry.cs` is a shared internal helper with no
`[McpServerToolType]`, and belongs in `docs/architecture.md`'s `Tools/` table rather than in any tool list.

## The audit, step by step

**1. Implementation.** For each tool from Step 0, read its method body (not just the `[Description]`) via
`get_symbol` (`include: "source"`) — confirm what it actually accepts, returns, and what it errors on.
Docs get audited against this, not against their own prior text.

**2. `[Description]` attributes vs. documented behavior and return shape.** Compare each tool's
`[Description]` attribute (on the method and on each parameter) against:
   - `docs/tools/<tool>.md` — its "When to reach for it" guidance, arguments, defaults, the example
     call/response, and its "Next steps" footer. If you can, actually call the tool with the documented
     example arguments and confirm the response still matches what's printed; an example that no longer
     matches the current return shape is worse than no example.
   - Whether the `[Description]` is written as a **search target**. Tool definitions are deferred by
     default (Claude Code enables tool search automatically), so the model sees only the tool *name*
     until it searches. The attribute's job is to be findable by the vocabulary a caller would search
     for and to say what question the tool answers — not to restate `docs/tools/<tool>.md`. A
     `[Description]` that has grown into a manual is a finding: it is re-paid on every fetch.
   - Any `[Description]` wording that has drifted from what the code now does (e.g., a default value
     mentioned in the attribute that the method body no longer honors).

**3. Skills vs. tool set.** For each of `skills/dotnet-code-query/SKILL.md`, `skills/dotnet-change/SKILL.md`,
`skills/dotnet-review/SKILL.md`, confirm every tool they describe still exists (Step 0's list) with the
arguments they claim, and that every tool relevant to that skill's subject actually appears in it — a tool
added to `Tools/*.cs` that fits an existing skill's scope but isn't mentioned there is a finding, not just
a missing doc row. `skills/dotnet-toolkit-init/SKILL.md` embeds its own copy of the tool table for
*consuming* repos — check that copy separately; it drifts independently of `docs/tools/_index.md`'s router.

**4. Every instruction/guideline file that tells a caller to use the MCP tools.** Check each one still
lists every tool from Step 0, with nothing stale (a tool it describes that no longer exists) and nothing
missing (a tool that exists but appears nowhere in it):

| File | What it must carry |
| --- | --- |
| `docs/tools/_index.md` | **the router** — one row per tool mapping the question it answers to the tool and its detail file; plus the common call chains, workspace readiness, and response conventions (documented once, here, not per tool). A tool missing from this table is unreachable no matter how good its own file is |
| `docs/tools/<tool>.md` (one per tool, plus `server.md`) | when to reach for it, arguments, one real example call/response, and a **Next steps** footer naming what to call with what it just returned. Every tool from Step 0 has a file; no file names a tool that no longer exists |
| `docs/tool-reference.md` | now an **index only** — the file table plus the conventions that hold across every tool. Check its table lists every file in `docs/tools/`. It must not re-acquire per-tool detail; that is what was split out |
| `CLAUDE.md`'s "Non-negotiable workflow" and "Where to read what" | that they still route to `docs/tools/_index.md` and the other reference files rather than carrying a copy of any table. A per-tool table, architecture rundown, or skill catalog reappearing here is drift — each was moved out deliberately, and two copies always diverge |
| `docs/architecture.md`'s `Tools/` table | every `Tools/*.cs` file and the tool names it groups — a new file here (Step 0) needs a new row. Also its Id-namespace table, subsystem list, and "Changing the tool surface" consequences (positional test callers, `Contracts/Contract.cs` bump) |
| `.claude/rules/csharp-standards.md` | the **master index** — its read-before-writing table lists exactly the standards files present in `.claude/rules/`, and its `validate_patch` line matches the current write path. That table's "When" column doubles as the **reviewer's trigger table**, so each condition must be an *observable property of the code* (awaits/locks, hot path, public surface change), not a topic a reviewer can't match against retrieved source; the always-loaded core it names must match `agents/dotnet-code-review.md`'s exactly |
| `skills/dotnet-change/SKILL.md`'s pre-edit standards step | its own enumeration of the standards files — every file in `csharp-standards.md`'s index appears in it under the right trigger (always / conditional / skim), nothing stale. This list drifts independently of the index and of the agent's list; a file present in two of the three and missing from the one is the usual shape of the bug |
| every standards file in `.claude/rules/` (per `csharp-standards.md`'s index) | every MCP tool named in them (e.g. `get_references` in `testing.md`'s calibration, `get_symbol` in `xml-documentation.md`'s) still exists with the described behavior; cross-file pointers between them still resolve |
| `skills/dotnet-toolkit-init/SKILL.md`'s rule template | its own embedded copy of the tool table and its standards-file list, written into consuming repos — both drift independently. Also the "what is deliberately *not* written" table and the uninstall list: every asset the plugin ships is in exactly one of write / not-written / uninstall, and the write and uninstall lists name the same files |
| `skills/dotnet-toolkit-install-check/SKILL.md` | the installation audit. Its Step 1 inventory must cover every top-level directory the plugin actually ships (`skills/`, `agents/`, `hooks/`, `docs/`, `docs/tools/`, `.claude/rules/`, `scripts/`, `.mcp.json`) under one of its four delivery mechanisms, and its Step 6 scenario table must resolve to files that exist. It audits init; this skill audits it |
| `skills/dotnet-toolkit-selfeval/SKILL.md` | the efficiency probe matrix. Check that its Step 2 families still cover every tool from Step 0; that the tools it lists as recording **no** telemetry (`ping`, `workspace_status`, `set_output_format`, `reload_workspace`, `get_retrieval_metrics`) are still exactly the ones with no `ToolTelemetry.Record`/`RecordPatch` call; and that its `taskId`/`taskIds`/`groupBy:"task"` measurement recipe still matches `MetricsTools`/`MetricsReader`. A tool newly instrumented but still listed there as unmeasurable understates what the evaluation can see |
| `agents/dotnet-code-review.md` | the agent's **complete, self-contained** instructions. Check: `tools:` frontmatter matches Step 0's read-side subset (nothing stale, and still excluding `get_project_graph`/`detect_circular_dependencies`, withheld as out of a scope slice's reach); every tool it names in Process, evidence bars, and Boundaries is granted there *and* behaves as described; its standards core and per-aspect fold-ins resolve to real `.claude/rules/` files; it still requires the `Standards:` line; and it has **not** re-acquired a `skills:` grant or a directive to read `docs/agent-reference.md` — both removed to hold the per-instance token baseline down |
| `docs/agent-reference.md` | **human-facing only; the agent must not be told to read it.** Check it does not contradict the agent file (authoritative), that its tool-grant and token-budget sections match the agent's actual frontmatter and loading rule, and that every tool it names still exists. Anything here duplicating the agent file is drift waiting to happen — prefer a pointer |
| `docs/hook-reference.md` | describes exactly the hooks `hooks/hooks.json` registers and the behavior their scripts implement — matchers, allow/deny cases, fallback chain |
| `docs/skill-reference.md` | one entry per skill under `skills/`, none stale, none missing |
| `README.md`'s Features table | every tool from Step 0 appears in some row; no row names a tool that no longer exists |

**4b. Consumer reachability — does the plugin work out of the box?** Steps 1–4 check that the files
describing the tools are *accurate*. This step checks they are *reachable from a consuming repo*, where
this repo's `CLAUDE.md` does not exist and this repo's `.claude/rules/csharp-standards.md` was never
copied. Every operational instruction must therefore live in something that ships: a skill, the agent
file, a `.claude/rules/` standards file (copied by init), a `docs/` file reachable by
`${CLAUDE_PLUGIN_ROOT}` path, or `dotnet-toolkit-init`'s protocol-rule template.

Two sweeps, both cheap:

- **This repo's `CLAUDE.md`, paragraph by paragraph.** For each instruction in it, decide: is this
  *about maintaining the plugin* (correct — it belongs only here), or is it *about using the tools*?
  Anything in the second category must also exist in a shipped file, and CLAUDE.md should carry a
  pointer rather than a second copy. The write-path discipline is the canonical example — the
  `validate_patch` argument in CLAUDE.md's "Non-negotiable workflow" is the same argument the init
  template and `dotnet-change` must both make, because a consumer never sees CLAUDE.md.
- **The maintainer's memory directory** (`~/.claude/projects/<repo-slug>/memory/`, indexed by its
  `MEMORY.md`). Read the index, then each entry, and sort every one into: **(a)** operational for
  consumers → must be embedded in a shipped file; **(b)** operational for this repo only → belongs in
  `CLAUDE.md` or `.claude/rules/`; **(c)** personal or environment-specific → belongs in neither, and
  is correctly only in memory. Memory is user-local and does not travel with the plugin, so anything
  in (a) or (b) that exists *only* there is a finding — it will be silently absent for every other
  user of this plugin, and for this repo after a memory reset.

Report each finding as: the memory or CLAUDE.md paragraph, its category, and the shipped file that
should carry it. Do not copy a category-(c) fact into a shipped file to close a finding — categorising
it correctly *is* closing it.

**5. Hooks and scripts.** Read `hooks/hooks.json` and every script it points at
(`scripts/guard-cs-edit.sh`, `scripts/guard-cs-read.sh`, `scripts/guard-cs-bash-read.sh`,
`scripts/hint-reload-new-cs-file.sh`, `scripts/run-server.sh`, `scripts/build-plugin.sh`), plus
`scripts/lib-cs-membership.sh` (the solution-membership check shared by the two read guards, not itself
registered in `hooks/hooks.json`). Specifically:
   - Does the deny/hint message text in each guard script still name the correct tool(s) and describe the
     correct procedure (`validate_patch`'s current argument names, `search_index`/`get_symbol` for the
     read guards, `reload_workspace(scope: "all")` for the reload hint)? A guard script's message is read
     at the exact moment a caller is blocked — a stale one teaches the wrong fix at the worst moment.
   - Does `hooks/hooks.json` still point at scripts that exist, with matchers (`Edit`/`Write`/`NotebookEdit`/
     `Read`/`Bash`) that match what `docs/hook-reference.md` and `docs/architecture.md`'s "Packaging"
     section claim they do?
   - Any new script under `scripts/` not mentioned in `docs/hook-reference.md` or
     `docs/architecture.md`'s "Packaging" section is a finding (see Step 6).
   - `hooks/hooks.json`'s matchers key on tool name only, not on which agent issues the call, so they
     fire for `dotnet-code-review` too. Two things must stay true together: (a) the agent's `tools:`
     list never grants `Edit`/`Write`/`NotebookEdit` — `memory: project` makes the harness grant them
     anyway, which is why its Boundaries section has to carry the weight; (b) its Process step 2 still
     sends it to `search_index`/`get_symbol` first and to `Read` only when a symbol lookup didn't give
     it the lines — a narrow fallback `guard-cs-read.sh` still enforces, not an escape hatch. Either
     one drifting is a finding here, not just in Step 4's table.

**6. New or modified files nothing else references.** This is the drift-detection step, not just a
per-file check: `git status`/`git diff --stat` (or `git log -p` for a stated commit range) against the
last time this audit ran, or against a stated baseline, and ask — for every added or non-trivially-modified
file under `src/`, `docs/`, `skills/`, `agents/`, `hooks/`, `scripts/`, `.claude/rules/` — does *something*
in Steps 3–5's tables now mention it? A new `Tools/*.cs` file, reference doc, skill, or hook script that
shipped without a row anywhere is the gap `docs/architecture.md`'s "Changing the tool surface" warns about;
`get_scope`, `get_call_slice`, and `get_semantic_diff` were a real instance — shipped in the code, named
in none of the docs.

**7. The skills' own instructions.** Once Steps 1–6 have surfaced concrete drift, the fix usually touches
a skill file itself, not just a table row — e.g. a new tool needs a new row in `docs/tools/_index.md`'s
router *and* its own `docs/tools/<tool>.md`, and a new "when to reach for this" line in
`dotnet-code-query` only if it changes which tool a caller should pick. Update the skill body, not only
its tool list, so a caller reading the skill gets the same guidance a caller reading the code would.

**7b. Context budget — the size check.** Verbosity drift is as real as factual drift and costs every
session. Run:

```bash
for f in skills/*/SKILL.md CLAUDE.md .claude/rules/csharp-standards.md; do
    printf "%-50s %6d B  ~%.1fk tok\n" "$f" $(wc -c < "$f") $(echo "$(wc -c < "$f")/3800" | bc -l)
done
```

Budgets, and why each one is a correctness bug rather than a cost note, are stated in CLAUDE.md's
"Context budget" section — don't restate them here, just enforce them:

- **Any `SKILL.md` over ~19 KB (~5k tokens)** — past that, auto-compaction silently drops the skill's
  later sections while its decision table still points at them. Fix: move per-tool mechanics into
  `docs/tools/<tool>.md`, leave the routing decision in the skill.
- **`CLAUDE.md` over ~10 KB (~150 lines)**, **`.claude/rules/csharp-standards.md` over ~6 KB** — the two
  always-loaded files, both deliberately indexes. Content rather than pointers is the drift: architecture
  belongs in `docs/architecture.md`, per-tool detail in `docs/tools/`, catalogs in
  `docs/skill-reference.md` / `docs/agent-reference.md`.
- **`dotnet-toolkit-init`'s protocol-rule template over ~6 KB** — the always-loaded footprint it
  writes into every *consuming* repo. Procedure detail belongs behind its
  `${CLAUDE_PLUGIN_ROOT}/docs/tools/<tool>.md` pointers. `dotnet-toolkit-install-check` owns this
  check too; either finding it is fine, both missing it is not.
- **A `[Description]` attribute that has grown into a manual** (Step 2) — the per-call equivalent.

Report each overage with its size, the budget, and which sections to move where. Do not fix an overage
by deleting guidance — move it behind a pointer, or the next session loses the rule.

**8. `CLAUDE.md` and `README.md` last.** The two files a fresh session or a new user reads first, so they
should reflect the *already-corrected* state of everything above rather than being patched independently
of it. Update `CLAUDE.md` per Step 4's table, then `README.md`'s Features table and prose.

## Output format

Report drift the same way `dotnet-code-review` reports findings — concrete and file-anchored, not a
narrative:

- **File:line** of the stale claim.
- **What it claims** vs. **what the code (Step 0/1) actually is**.
- **Exact fix** — the replacement text or the specific file to add a row/paragraph to. Point at the file,
  don't just describe the fix in the abstract.

Group findings by file, in the Step 4 table's order, then hooks/scripts, then CLAUDE.md/README.md last.
If every file checked is in sync, say so in one line — don't manufacture findings to justify the run.

## Fixing vs. reporting

Apply fixes yourself once found, in Step 1→8 order (code is never edited by this skill — only the
docs/skills/rules/hooks describing it). Every fix is to a non-`.cs` file, so `Edit`/`Write` apply
directly, not `validate_patch`. Never silently skip a step because "nothing looks wrong there" — state
it was checked and came back clean.
