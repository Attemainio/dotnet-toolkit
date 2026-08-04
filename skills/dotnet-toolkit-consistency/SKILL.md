---
name: dotnet-toolkit-consistency
description: Use when auditing whether this plugin's own docs, skills, agent, rules, hooks, CLAUDE.md, and README.md are in sync with the actual MCP tool implementation under src/DotnetToolkit.McpServer — after adding, removing, renaming, or changing the signature/return shape/[Description] of a tool, after adding or editing a hook/script, or any time something in a doc/skill/rule looks stale, contradicts the code, or a new file was added that nothing else references. Also owns the install-procedure audit — "audit the init skill", "does what we ship actually reach a consuming repo", "would uninstalling leave anything behind" — checking dotnet-toolkit-init's claims against the plugin tree. Audits Tools/*.cs as ground truth against every file that describes the tool surface, and reports or fixes exact drift, file by file.
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
`grep`**: the `guard-cs-bash-read` hook blocks shell reads of solution `.cs` files, and rightly — this is a
symbol query, not a text search.

```
get_symbol(symbols: ["ContextTools", "FlowTools", "GraphTools", "HistoryTools",
                     "PatchTools", "RenameTools", "MetricsTools", "ServerTools"], include: "members")
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

**4. Every instruction/guideline file that tells a caller to use the MCP tools.** Each must still
list every tool from Step 0, with nothing stale (a tool it describes that no longer exists) and
nothing missing (a tool that exists but appears nowhere in it).

**The file-by-file checklist is `docs/surface-file-map.md`** — 19 rows, each naming what that file
must carry. Read it and work it; it is deliberately not restated here, because a second copy of a
19-row table is exactly the drift this skill exists to catch.

Three of its rows have produced shipped bugs and are worth holding in mind while you read the rest:
the two always-loaded rules (`tool-protocol.md`, `csharp-standards.md`) are copied verbatim into
consuming repos, so drift there *ships*; and both agent files are self-contained, so a standards
pointer that resolves only in this repo is a silent failure everywhere else.

**4b. Consumer reachability — does the plugin work out of the box?** Steps 1–4 check that the files
describing the tools are *accurate*. This step checks they are *reachable from a consuming repo*, where
this repo's `CLAUDE.md` does not exist and this repo's `.claude/rules/csharp-standards.md` was never
copied. Every operational instruction must therefore live in something that ships: a skill, an agent
file, a `.claude/rules/` standards file (copied by init), a `docs/` file reachable by
`${CLAUDE_PLUGIN_ROOT}` path **from a skill** (rules and agent files must not depend on that
variable expanding), or one of the two always-loaded rules init copies
(`.claude/rules/tool-protocol.md`, `.claude/rules/csharp-standards.md`).

**The install procedure is half of this step.** `dotnet-toolkit-init` is prose, hand-maintained, and
describes a file set that grows every time this plugin gains a tool, doc, skill, or standards file.
When it falls behind, the failure is silent in both directions: an asset ships but never reaches the
consumer, or the uninstall instructions leave files behind that keep steering a repo that no longer
has the plugin. **Read `docs/install-audit.md` and run it** — it carries the four-mechanism inventory
table, the drift catalog, and the out-of-the-box scenarios. Two findings from it are hard errors
worth stating here because both have shipped: a `${CLAUDE_PLUGIN_ROOT}` path in a *copied rule* (rule
files are delivered literally, never expanded — the path is dead in every consumer), and any claim
that mechanism-1 assets need a repo-local install or cleanup step.

Then two sweeps, both cheap:

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

**5. Hooks and launch path.** All four hooks are `hook <name>` subcommands of the published server
binary, in `src/DotnetToolkit.McpServer/Hooks/` — `HookCli.cs` dispatches, the four `Guard*`/`ReloadHint`
files carry the messages, `CsFileMembership.cs`/`BashCommandScanner.cs` are shared with no `hooks.json`
entry. Read those with `get_symbol` (not `grep`), plus `hooks/hooks.json` and `.mcp.json`:
   - Does each guard's deny/hint text still name the correct tool(s) and procedure (`validate_patch`'s
     current argument names, `search_index`/`get_symbol` for the read guards,
     `reload_workspace(scope: "all")` for the reload hint)? It is read at the exact moment a caller is
     blocked — a stale one teaches the wrong fix at the worst moment.
   - Does `hooks/hooks.json` name subcommands `HookCli` actually dispatches, with matchers
     (`Edit`/`Write`/`NotebookEdit`/`Read`/`Bash`) matching `docs/hook-reference.md` and
     `docs/architecture.md`'s "Packaging" section?
   - **Nothing shipped at runtime may require a shell.** Every `.mcp.json`/`hooks.json` command must be
     a `dotnet <dll> …` invocation; a `.sh`/`.ps1` entry point, a shebang, or a `node`/`python3`/`jq`
     dependency is a finding. An MCP stdio server is spawned directly, so a script launcher cannot run
     on Windows at all, and a Store-stubbed `python3` makes a guard fail open while looking healthy.
     `scripts/` holds developer conveniences only; anything new there or in `Hooks/` unmentioned by
     `docs/hook-reference.md` or "Packaging" is a Step 6 finding.
   - Matchers key on tool name, not on which agent calls, so they fire for both subagents too. For
     `dotnet-code-review`: (a) its `tools:` list never grants `Edit`/`Write`/`NotebookEdit` — `memory:
     project` makes the harness grant them anyway, which is why Boundaries carries the weight; (b) its
     Process step 2 still goes to `search_index`/`get_symbol` first and to `Read` only when a symbol
     lookup didn't give it the lines — a narrow fallback `guard-cs-read` still enforces, not an escape
     hatch. For `dotnet-explore` the guard is only a backstop — its own file bans `Read` on `.cs`
     outright, so a denial there means that file drifted.

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
- **`.claude/rules/tool-protocol.md` over ~6 KB** — half the always-loaded footprint init writes into
  every *consuming* repo. Procedure detail belongs behind the skills it names, since a rule cannot
  resolve a path itself.
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
