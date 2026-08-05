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
`[McpServerToolType]`, and belongs in `docs/design/architecture.md`'s `Tools/` table rather than in any tool list.

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
*consuming* repos — check that copy separately.

**4. Every instruction/guideline file that tells a caller to use the MCP tools.** Check each one
still lists every tool from Step 0, with nothing stale (a tool it describes that no longer exists)
and nothing missing (a tool that exists but appears nowhere in it):

| File | What it must carry |
| --- | --- |
| `docs/tools/<tool>.md` (one per tool, plus `server.md`) | when to reach for it, arguments, one real example call/response, and a **Next steps** footer naming what to call with what it just returned. Every tool from Step 0 has a file; no file names a tool that no longer exists; and every file is named in `index.md`'s router — an unreferenced one is unreachable no matter how good it is. These files carry the per-tool mechanics that must **not** move into the always-loaded router |
| `CLAUDE.md`'s "Non-negotiable workflow" and "Where to read what" | that they still route to `.claude/rules/index.md` and the maintainer-facing files rather than carrying a copy of any table. A per-tool table, architecture rundown, or skill catalog reappearing here is drift — each was moved out deliberately, and two copies always diverge |
| `docs/design/architecture.md`'s `Tools/` table | every `Tools/*.cs` file and the tool names it groups — a new file here (Step 0) needs a new row. Also its Id-namespace table, subsystem list, and "Changing the tool surface" consequences (positional test callers, `Contracts/Contract.cs` bump) |
| `.claude/rules/index.md` | **the only always-loaded rule** — here *and* copied verbatim into consuming repos by init, so drift ships and is paid by every session and every subagent. It must carry only *when* and *where*: (a) **the router** — one row per tool mapping the wrong path to the right tool **and naming its `docs/tools/<tool>.md` in a third column**, covering every tool from Step 0, with no `docs/tools/` file left unnamed and no row naming a file that no longer exists. This is the only router; there is no second copy in `docs/`. Plus workspace readiness (`limitedBy`) and the response conventions, documented once here rather than per tool; (b) an exploring section naming `dotnet-explore` and when to skip it; (c) a write path matching `validate_patch`'s current arguments; (d) a standards table listing exactly the files present in `standards/`, whose "When" column doubles as the **reviewer's trigger table** — so each condition must be an *observable property of the code* (awaits/locks, hot path, public surface change), not a topic a reviewer can't match against retrieved source, and the always-loaded core it names must match `agents/dotnet-code-review.md`'s exactly; (e) the skill-invocation mandates. **No `paths:` frontmatter**, and `${CLAUDE_PLUGIN_ROOT}` appears **only** in the sentence forbidding its use — a rule is delivered literally, never expanded, so a real path built from it would be dead text. Plugin files are instead reached by the documented join: `workspace_status`'s `pluginRoot:` + `docs/tools/<name>` or `standards/<name>`. That resolution section must be present and must match what `workspace_status` actually returns, and must name **exactly one** location per file — a per-repo override tier reappearing anywhere is the drift to flag, since two copies of a standard put the writer and the reviewer on different text. A write-procedure bullet or a standards *body* reappearing here is the drift to flag |
| `skills/dotnet-change/SKILL.md` | (a) its pre-edit standards step enumerates every file in `index.md`'s standards table under the right trigger (always / conditional / skim), nothing stale — this list drifts independently of the table and of the agent's list, and a file present in two of the three and missing from the one is the usual shape of the bug; (b) it resolves standards as `<pluginRoot>/standards/<name>.md` via `workspace_status`, with no override tier and no `${CLAUDE_PLUGIN_ROOT}` written into a path; (c) **it owns the write-time checklist and the write-failure modes** (`unleased_body`, decomposition, `.editorconfig`, scope honesty, the `draftId` amend) — these were moved out of the always-loaded layer and must not drift back |
| `skills/dotnet-review/SKILL.md` | it still tells the caller to pass an **expanded** `Standards root:` into every spawn. Without it the agent reports all standards-derived aspects not-assessed, which silently guts a review — and a skill is the only thing here that can expand `${CLAUDE_PLUGIN_ROOT}` |
| every file in `standards/` | **no frontmatter on any of them** (it would do nothing outside a rules directory and signals a partial revert to the old layout); every MCP tool named in them (e.g. `get_references` in `testing.md`'s calibration, `get_symbol` in `xml-documentation.md`'s) still exists with the described behavior; cross-file pointers still resolve; and **none of them offers a per-repo override path** — that tier was removed, and a reintroduced sentence would promise a mechanism nothing implements |
| `skills/dotnet-toolkit-init/SKILL.md`'s rule template | its own embedded copy of the tool table and its standards-file list, written into consuming repos — both drift independently. Also the "what is deliberately *not* written" table and the uninstall list: every asset the plugin ships is in exactly one of write / not-written / uninstall, and the write and uninstall lists name the same files |
| `docs/install/audit.md` | the install-procedure audit this skill runs in Step 4b (maintainer-facing; the consumer never reads it). Its four-mechanism inventory must cover every top-level directory the plugin actually ships (`skills/`, `agents/`, `hooks/`, `docs/`, `docs/tools/`, `.claude/rules/`, `scripts/`, `.mcp.json`), and its reachability scenarios must resolve to files that exist |
| `docs/install/{install,verify,uninstall}.md` | the three paths `dotnet-toolkit-init` routes to. Check they name the files init actually writes (including the settings allowlist and `install.json`) and no file it doesn't, and that the skill's router table points at all three |
| `skills/dotnet-toolkit-selfeval/SKILL.md` + `skills/dotnet-toolkit-selfeval/analyses.md` (the four analyses, read on demand) | the efficiency probe matrix. Check that its Step 2 families still cover every tool from Step 0; that the tools it lists as recording **no** telemetry (`ping`, `workspace_status`, `set_output_format`, `reload_workspace`, `get_retrieval_metrics`) are still exactly the ones with no `ToolTelemetry.Record`/`RecordPatch` call; and that its `taskId`/`taskIds`/`groupBy:"task"` measurement recipe still matches `MetricsTools`/`MetricsReader`. A tool newly instrumented but still listed there as unmeasurable understates what the evaluation can see |
| `agents/dotnet-code-review.md` | the agent's **complete, self-contained** instructions. Check: `tools:` frontmatter matches Step 0's read-side subset (nothing stale, and still excluding `get_project_graph`/`detect_circular_dependencies`, withheld as out of a scope slice's reach); every tool it names in Process, evidence bars, and Boundaries is granted there *and* behaves as described; its standards core and per-aspect fold-ins resolve to real `standards/` filenames, reached through the injected `Standards root:` and **never** by constructing a `${CLAUDE_PLUGIN_ROOT}` path itself (expansion inside an agent definition is not guaranteed); the no-root fallback still reports standards-derived aspects as not-assessed rather than reviewing from memory; it still requires the `Standards:` line; and it has **not** re-acquired a `skills:` grant or a directive to read `docs/design/agents.md` — both removed to hold the per-instance token baseline down |
| `agents/dotnet-explore.md` | the navigator's **complete, self-contained** instructions. Its router must name no write tool as callable, and three properties are load-bearing (rationale in `docs/design/agents.md`, not here): **no writer and no `memory:` key** in `tools:`, `Read` whitelisted to `docs/tools/<tool>.md` only, and no relaying of a `contentVersion` |
| `docs/design/agents.md` | covers **both** agents; **human-facing only — neither agent is told to read it.** Check it does not contradict the agent file (authoritative), that its tool-grant and token-budget sections match the agent's actual frontmatter and loading rule, and that every tool it names still exists. Anything here duplicating the agent file is drift waiting to happen — prefer a pointer |
| `docs/design/hooks.md` | maintainer-facing design notes; nothing routes to it. Describes exactly the hooks `hooks/hooks.json` registers and the behavior their scripts implement — matchers, allow/deny cases, fallback chain |
| `README.md`'s Features table | every tool from Step 0 appears in some row; no row names a tool that no longer exists |

**4b. Consumer reachability — does the plugin work out of the box?** Steps 1–4 check that the files
describing the tools are *accurate*. This step checks they are *reachable from a consuming repo*, where
this repo's `CLAUDE.md` does not exist. Every operational instruction must therefore live in
something that ships: the one rule init copies (`.claude/rules/index.md`), a skill, an agent file, or
a `docs/`/`standards/` file reachable by `${CLAUDE_PLUGIN_ROOT}` path **from a skill** — rules and
agent files must not depend on that variable expanding, which is why `dotnet-review` resolves the
standards root and hands it to each reviewer.

**The install procedure is half of this step.** `dotnet-toolkit-init` is prose, hand-maintained, and
describes a file set that grows every time this plugin gains a tool, doc, skill, or standards file.
When it falls behind, the failure is silent in both directions: an asset ships but never reaches the
consumer, or the uninstall instructions leave files behind that keep steering a repo that no longer
has the plugin. **Read `docs/install/audit.md` and run it** — it carries the four-mechanism inventory
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
     (`Edit`/`Write`/`NotebookEdit`/`Read`/`Bash`) matching `docs/design/hooks.md` and
     `docs/design/architecture.md`'s "Packaging" section?
   - **Nothing shipped at runtime may require a shell.** Every `.mcp.json`/`hooks.json` command must be
     a `dotnet <dll> …` invocation; a `.sh`/`.ps1` entry point, a shebang, or a `node`/`python3`/`jq`
     dependency is a finding. An MCP stdio server is spawned directly, so a script launcher cannot run
     on Windows at all, and a Store-stubbed `python3` makes a guard fail open while looking healthy.
     `scripts/` holds developer conveniences only; anything new there or in `Hooks/` unmentioned by
     `docs/design/hooks.md` or "Packaging" is a Step 6 finding.
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
shipped without a row anywhere is the gap `docs/design/architecture.md`'s "Changing the tool surface" warns about;
`get_scope`, `get_call_slice`, and `get_semantic_diff` were a real instance — shipped in the code, named
in none of the docs.

**7. The skills' own instructions.** Once Steps 1–6 have surfaced concrete drift, the fix usually touches
a skill file itself, not just a table row — e.g. a new tool needs a new row in `.claude/rules/index.md`'s
router *and* its own `docs/tools/<tool>.md`, and a new "when to reach for this" line in
`dotnet-code-query` only if it changes which tool a caller should pick. Update the skill body, not only
its tool list, so a caller reading the skill gets the same guidance a caller reading the code would.

**7b. Always-loaded budget, layout invariants, and scatter.** Three different checks; don't conflate
them.

```bash
for f in CLAUDE.md .claude/rules/index.md; do
    printf "%-50s %6d B  ~%.1fk tok\n" "$f" $(wc -c < "$f") $(echo "$(wc -c < "$f")/3800" | bc -l)
done

# exactly one always-loaded rule, and it is index.md
awk 'FNR==1 && !/^---/ {print "unfrontmattered rule: " FILENAME}' .claude/rules/*.md

# no standard may carry frontmatter
awk 'FNR==1 && /^---/ {print "FINDING: frontmatter on " FILENAME}' standards/*.md
```

**The budget applies only to the two always-loaded files** — this repo's `CLAUDE.md` (~5 KB) and
`index.md` (~6 KB), the latter paid again by every consuming repo. **Both are also inherited by every
subagent, with no opt-out**, so a seven-way parallel review pays them eight times; that multiplier is
the reason the budget is tight. They are declarations of *when* and *where*; an architecture rundown,
tool catalog, or per-tool procedure reappearing in one is the drift. Treat the numbers as targets to
argue with, not walls, and never fix an overage by deleting guidance — move it behind a pointer, or
the next session loses the rule.

**Two layout invariants, both silent when broken:**

- `.claude/rules/` must contain **exactly one file with no frontmatter**, and it must be `index.md`.
  A second one is an always-loaded rule nobody costed, charged to every session and every subagent.
- **`workspace_status` must still report `pluginRoot`, and `docs/tools/server.md` must document the
  join.** It is the only route from an always-loaded rule or a subagent to the plugin's own files;
  dropping it silently strands every standards read and every tool-manual read. Contract 3.50.
- **No file under `standards/` may have frontmatter.** A `paths:` key there does nothing (it is
  outside a rules directory) and signals a partial revert toward the old layout — where it was
  actively harmful, since the read guard allows `Read` on `.cs` files no project compiles, so the
  glob fired unpredictably instead of on demand. Rationale in `docs/design/architecture.md`.

**Skills, `standards/`, and `docs/` files have no size budget.** They are read on demand and cost nothing until
invoked. Do not report a long skill as a finding; a single-purpose skill should carry its whole
procedure inline.

**Scatter is the finding to look for instead**, in both directions:

- A `docs/` file that exists only because some skill got long — one job split across two files, so
  both must be updated and the pointer can go stale. Fix: fold it back.
- A skill with **several** named responsibilities carrying all of them inline, where a reader on one
  path pays for the other two. Fix: one file per path, as `dotnet-toolkit-init` does.
- A `docs/` file nothing reads (Step 6 catches this) — the terminal form of the first case.

**A `[Description]` attribute that has grown into a manual** (Step 2) is still a finding — that one is
paid per tool call.

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
