---
name: dotnet-toolkit-consistency
description: Use when auditing whether this plugin's own docs, skills, agent, rules, hooks, CLAUDE.md, and README.md are in sync with the actual MCP tool implementation under src/DotnetToolkit.McpServer — after adding, removing, renaming, or changing the signature/return shape/[Description] of a tool, after adding or editing a hook/script, or any time something in a doc/skill/rule looks stale, contradicts the code, or a new file was added that nothing else references. Audits Tools/*.cs as ground truth against every file that describes the tool surface, and reports or fixes exact drift, file by file.
---

# Auditing plugin self-consistency

This plugin teaches Claude to use its own MCP tools through a scattered set of files — skills, an agent
definition, path-scoped rules, hooks/scripts, `docs/*.md`, `CLAUDE.md`, `README.md`. None of them is
generated from the code; every one is hand-maintained prose that can silently drift from
`src/DotnetToolkit.McpServer/Tools/*.cs` the moment a tool changes and the corresponding doc edit is
skipped. This skill is the audit pass that catches that drift, run as its own task rather than trusted to
happen implicitly during unrelated work.

**Ground truth is always the code.** Every check below starts from `Tools/*.cs` and treats every other
file as a claim to verify against it — never the reverse. If a doc and the code disagree, the code is
right and the doc is what moves.

## Step 0 — enumerate the actual tool surface

Before anything else, get the current, complete list directly from the source (don't trust any doc's
existing tool list, including this skill's own examples below, since that's exactly the kind of claim
being audited):

```bash
grep -rn 'McpServerTool(Name = "' src/DotnetToolkit.McpServer/Tools/*.cs
```

This gives every tool name, its containing file, and its line — the fixed reference point for every step
that follows. Note the file each tool lives in; a new `Tools/*.cs` file (a new tool *group*, not just a
new tool) is itself a finding — see Step 6.

## The audit, step by step

**1. Implementation.** For each tool from Step 0, read its method body (not just the `[Description]`) via
`get_symbol` (`include: "source"`) — confirm what it actually accepts, returns, and what it errors on.
Docs get audited against this, not against their own prior text.

**2. `[Description]` attributes vs. documented behavior and return shape.** Compare each tool's
`[Description]` attribute (on the method and on each parameter) against:
   - `docs/tool-reference.md`'s entry for that tool — arguments, defaults, the example call/response.
     If you can, actually call the tool with the documented example arguments and confirm the response
     still matches what's printed; an example that no longer matches the current return shape is worse
     than no example.
   - Any `[Description]` wording that has drifted from what the code now does (e.g., a default value
     mentioned in the attribute that the method body no longer honors).

**3. Skills vs. tool set.** For each of `skills/dotnet-code-query/SKILL.md`, `skills/dotnet-change/SKILL.md`,
`skills/dotnet-review/SKILL.md`, confirm every tool they describe still exists (Step 0's list) with the
arguments they claim, and that every tool relevant to that skill's subject actually appears in it — a tool
added to `Tools/*.cs` that fits an existing skill's scope but isn't mentioned there is a finding, not just
a missing doc row. `skills/dotnet-toolkit-init/SKILL.md` embeds its own copy of the tool table for
*consuming* repos — check that copy separately; it drifts independently of `CLAUDE.md`'s table.

**4. Every instruction/guideline file that tells a caller to use the MCP tools.** Check each one still
lists every tool from Step 0, with nothing stale (a tool it describes that no longer exists) and nothing
missing (a tool that exists but appears nowhere in it):

| File | What it must carry |
| --- | --- |
| `docs/tool-reference.md` | complete per-tool catalog: arguments, one real example call/response, what it replaces |
| `CLAUDE.md`'s "Working in this repo" table (`Instead of / Use`) | one row per read/write tool a session would otherwise reach for Grep/Read/`find` instead of |
| `CLAUDE.md`'s Architecture section, `Tools/` bullet | every `Tools/*.cs` file and the tool names it groups — a new file here (Step 0) needs a new clause |
| `CLAUDE.md`'s Code review section (aspect list + read-side toolset paragraph) | the review agent's actual granted tool list, not a stale subset |
| `.claude/rules/csharp-standards.md` | the always-loaded standards index — its read-before-writing table must list exactly the standards files that exist in `.claude/rules/`, and its `validate_patch` line must match the current write path |
| the nine standards files in `.claude/rules/` (`naming`, `styling`, `best-practices`, `antipatterns`, `performance`, `concurrency`, `security`, `testing`, `xml-documentation`) | every MCP tool named in them (e.g. `get_references` in `testing.md`'s calibration, `get_symbol` in `xml-documentation.md`'s) still exists with the described behavior; cross-file pointers between them still resolve |
| `skills/dotnet-toolkit-init/SKILL.md`'s rule template | its own embedded copy of the tool table and its standards-file list, written into consuming repos — both drift independently |
| `agents/dotnet-code-review.md` | `tools:` frontmatter list matches Step 0 exactly for whatever subset the agent should have — every read-side MCP tool the agent needs for efficient orientation (not a stale subset missing a tool added since); its standards-file list (all nine) still resolves to real files in `.claude/rules/` |
| `docs/agent-reference.md` | every tool named in it (setup steps, boundaries) still exists and is still described accurately (e.g. what `workspace_status` signals, what a zero-hit from a semantic tool does and doesn't prove); its aspect list matches the agent file's |
| `docs/hook-reference.md` | describes exactly the hooks `hooks/hooks.json` registers and the behavior their scripts implement — matchers, allow/deny cases, fallback chain |
| `docs/skill-reference.md` | one entry per skill under `skills/`, none stale, none missing |
| `README.md`'s Features table | every tool from Step 0 appears in some row; no row names a tool that no longer exists |

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
     `Read`/`Bash`) that match what `docs/hook-reference.md` and CLAUDE.md's "Plugin packaging" section
     claim they do?
   - Any new script under `scripts/` not mentioned in `docs/hook-reference.md` or CLAUDE.md's "Plugin
     packaging" section is a finding (see Step 6).
   - `hooks/hooks.json`'s matchers key on tool name only (`Edit|Write|NotebookEdit`, `Read`), not on which
     agent issues the call — they fire identically whether the tool call comes from the main agent or a
     subagent invocation of `dotnet-code-review`. That agent is granted `Read` (for the cases
     `docs/agent-reference.md`'s Setup step 3 allows — judging specific lines `get_symbol` didn't already
     give it), so confirm two things stay true together: (a) `agents/dotnet-code-review.md`'s `tools:` list
     does not grant it `Edit`/`Write`/`NotebookEdit` at all — the guard for those exists but the agent
     should never need it, since `docs/agent-reference.md`'s Boundaries section states it never modifies
     code; and (b) `docs/agent-reference.md`'s Setup step 3 still tells it to reach for `search_index`/
     `get_symbol` first and only `Read` a file when a symbol lookup didn't give it the lines — i.e. its
     `Read` grant is a narrow, guarded fallback that `guard-cs-read.sh` still enforces on every call, not an
     unguarded escape hatch from the MCP tools. If either drifts — the agent gains `Edit`/`Write`, or
     `agent-reference.md` stops steering it toward symbol retrieval first — that's a finding here, not just
     in Step 4's table.

**6. New or modified files nothing else references.** This is the drift-detection step, not just a
per-file check: `git status`/`git diff --stat` (or `git log -p` for a stated commit range) against the
last time this audit ran, or against a stated baseline, and ask — for every added or non-trivially-modified
file under `src/`, `docs/`, `skills/`, `agents/`, `hooks/`, `scripts/`, `.claude/rules/` — does *something*
in Steps 3–5's tables now mention it? A new `Tools/*.cs` file, a new `docs/*.md` reference doc, a new
skill, a new hook script that shipped without a corresponding row anywhere is exactly the kind of gap
CLAUDE.md's own "Changing the tool surface" section warns about (it names `get_scope`, `get_call_slice`,
and `get_semantic_diff` as a real past instance of this).

**7. The skills' own instructions.** Once Steps 1–6 have surfaced concrete drift, the fix usually touches
a skill file itself, not just a table row — e.g. a new tool needs a new "when to reach for this" paragraph
in `dotnet-code-query`, not just a new line in `docs/tool-reference.md`. Update the skill body, not only
its tool list, so a caller reading the skill gets the same guidance a caller reading the code would.

**8. `CLAUDE.md` and `README.md` last.** These are the two files a fresh session or a new user reads
first, so they should reflect the *already-corrected* state of everything above, not be patched
independently of it. Update `CLAUDE.md`'s tables/sections per Step 4's table, then `README.md`'s Features
table and prose, as the final step once every other file it summarizes is already fixed.

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

Apply fixes yourself once found, in the Step 1→8 order above (code is never the thing edited by this
skill — only the docs/skills/rules/hooks that describe it). Every fix here is to a non-`.cs` file, so none
of it goes through `validate_patch`; `Edit`/`Write` apply directly. Do not silently skip a step because
"nothing looks wrong there" — state that the step was checked and came back clean, the same discipline
`dotnet-code-review` applies to a clean aspect.
