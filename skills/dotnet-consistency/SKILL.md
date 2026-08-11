---
name: dotnet-consistency
description: Use when auditing this plugin's own docs, skills, agents, rules, hooks, CLAUDE.md and README.md — three axes at once. (1) Do they match the actual MCP tool implementation under src/DotnetToolkit.McpServer? Run after adding, removing, renaming, or changing the signature/return shape/[Description] of a tool, or after adding or editing a hook. (2) Do we still follow official Claude Code and Claude API guidance — "are our tool descriptions findable", "does tool search actually find every tool", "is CLAUDE.md too long", "is anything always-loaded that shouldn't be", "check we follow Claude's guidelines", "refresh the Claude docs links". (3) What has drifted since a baseline — "what has drifted since <commit>", "what did we change and forget to document". Also owns the install-procedure audit — "audit the init skill", "does what we ship actually reach a consuming repo", "would uninstalling leave anything behind". Reports or fixes exact drift, file by file.
---

# Auditing plugin self-consistency

This plugin teaches Claude to use its own MCP tools through a scattered set of hand-maintained files —
skills, agent definitions, rules, hooks, `docs/*.md`, `CLAUDE.md`, `README.md`. None is generated, so
any of them can silently drift the moment something changes and the doc edit is skipped. This is the
audit pass that catches that, run as its own task rather than trusted to unrelated work.

## Three sources of truth, and which one wins

**The code is not the only ground truth**, and treating it that way is how a defect gets locked in:
the doc gets rewritten to match code that was itself wrong.

- **Code is ground truth for *facts*.** Tool names, signatures, arguments, defaults, return shapes,
  error codes, what a hook actually dispatches. A doc that disagrees here always loses.
- **Official Claude documentation is ground truth for *design*.** How a tool gets found, what is
  always loaded and what is not, what a skill / agent / rule may contain, the harness's own limits.
  **Code that disagrees here loses** — and the result is a `CODE FINDING`, not a doc edit.
- **Internal documents are ground truth for nothing.** They are claims, checked against both.

## The three reference files

All in `${CLAUDE_PLUGIN_ROOT}/skills/dotnet-consistency/`, read on demand — a run that only
needs drift detection should not pay for the rest.

| File | Owns | Read by |
| --- | --- | --- |
| `claude-docs.md` | the official Claude doc URLs, their must-remember shortlists, and the staleness gate | Step 1 |
| `harness-compliance.md` | the by-section checklist §A–§G with every threshold and its citation | Step 1 |
| `drift.md` | the baseline, the evidence order, and the drift-sensitivity ranking | Step 7 |

## Step 0 — enumerate the actual tool surface

Get the current, complete list from the source — don't trust any doc's tool list, including this
skill's own examples; that is exactly the kind of claim being audited. **Not `grep`**: the
`guard-cs-bash-read` hook blocks shell reads of solution `.cs` files, and rightly — this is a symbol
query, not a text search.

```
get_symbol(symbols: ["ContextTools", "FlowTools", "GraphTools", "HistoryTools",
                     "PatchTools", "RenameTools", "MetricsTools", "ServerTools"], include: "members")
```

Each `[McpServerToolType]` class's public methods **are** its tools, one per method, with every
parameter and default. Confirm the class list itself first with
`search_index(query: "Tools", pathPrefix: "src/DotnetToolkit.McpServer/Tools")`, so a **new** tool
*group* is caught rather than assumed away — a new `Tools/*.cs` file is itself a finding (Step 7).
Not every file under `Tools/` is a tool group: `ToolTelemetry.cs` is a shared internal helper with no
`[McpServerToolType]`, and belongs in `docs/design/architecture.md`'s `Tools/` table, not in any tool
list.

## The audit, step by step

**1. Harness compliance.** Read `harness-compliance.md` and run §A–§G. **First**, because it is the
only axis that can produce code findings, and because a guidance change invalidates a
previously-clean result on every other axis.

Before running it, apply the staleness gate: if `claude-docs.md`'s `last-verified` is older than its
`refresh-window`, or the user asked to refresh the links, run that file's refresh procedure and update
the shortlists before checking anything against them.

**2. Implementation.** For each tool from Step 0, read its method body (not just the `[Description]`)
via `get_symbol(include: "source")` — confirm what it actually accepts, returns, and errors on. Docs
get audited against this, not against their own prior text.

**3. `[Description]` attributes vs. documented behavior and return shape.** Compare each tool's
`[Description]` (on the method and on each parameter) against:
   - `docs/tools/<tool>.md` — its "When to reach for it" guidance, arguments, defaults, the example
     call/response, and its **Next steps** footer. If you can, call the tool with the documented
     example arguments and confirm the response still matches what's printed; an example that no
     longer matches the current return shape is worse than no example.
   - Any wording that has drifted from what the code now does — e.g. a default named in the attribute
     that the method body no longer honors.
   - Whether it works as a **search target** — that criterion, its thresholds and the live probe are
     `harness-compliance.md` §B and §C, already run in Step 1. Don't re-derive them here.

**4. Skills vs. tool set.** For `skills/dotnet-read/SKILL.md`, `skills/dotnet-write/SKILL.md`,
`skills/dotnet-explore/SKILL.md` and `skills/dotnet-review/SKILL.md`, confirm every tool they describe
still exists (Step 0) with the arguments they claim, and that every tool relevant to that skill's
subject actually appears in it — a tool added to `Tools/*.cs` that fits an existing skill's scope but
isn't mentioned there is a finding, not just a missing doc row.

**`dotnet-read` and `dotnet-write` together must name all 18 tools**, because
`.claude/rules/dotnet-index.md` no longer names any. A tool that appears in neither is unreachable:
nothing always-loaded points at it, and free-text `ToolSearch` does not reliably find it (§C). Split
by side — retrieval and the four server/meta tools in `dotnet-read`, `validate_patch` and
`rename_symbol` in `dotnet-write` — with `workspace_status` and `reload_workspace` legitimately in
both, since each skill's step 0 depends on them.

**5. Every instruction/guideline file that tells a caller to use the MCP tools.** Check each still
lists every tool from Step 0, with nothing stale (a tool it describes that no longer exists) and
nothing missing (a tool that exists but appears nowhere in it):

| File | What it must carry |
| --- | --- |
| `docs/tools/<tool>.md` (one per tool, plus `server.md`) | when to reach for it, arguments, one real example call/response, and a **Next steps** footer naming what to call with what it just returned. Every tool from Step 0 has a file, and no file names a tool that no longer exists — **the filename is the reachability contract** now that `dotnet-index.md` derives it (`<tool>.md`, the four server/meta tools sharing `server.md`) rather than tabulating it, so a manual named anything else is unreachable no matter how good it is. Each tool's `[Description]` should also name its own file. These carry the per-tool mechanics that must **not** move into the always-loaded router |
| `CLAUDE.md`'s "Non-negotiable workflow" and "Where to read what" | that they still route to `.claude/rules/dotnet-index.md` and the maintainer-facing files rather than carrying a copy of any table. A per-tool table, architecture rundown, skill catalog, or size policy reappearing here is drift — each was moved out deliberately (`harness-compliance.md` §A) |
| `docs/design/architecture.md`'s `Tools/` table | every `Tools/*.cs` file and the tool names it groups — a new file (Step 0) needs a new row. Also its Id-namespace table, subsystem list, and "Changing the tool surface" consequences (positional test callers, `Contracts/Contract.cs` bump) |
| `.claude/rules/dotnet-index.md` | **the only always-loaded rule** — here *and* copied verbatim into consuming repos by init, so drift ships and is paid by every session and every subagent. **What it may and may not contain is `harness-compliance.md` §E**, and §D owns its size. It is a **pure skill router**: check that it names **no MCP tool at all** (one appearing is the finding), that its four rows still match the skills that exist, and that the "agents are launched by skills" mandate survives. A tool table, a standards table, a `limitedBy` section or a `pluginRoot` join reappearing here is drift — each was moved out deliberately |
| `standards/index.md` | the **shared** standards routing table. It lists exactly the files present in `standards/`; its stated `dotnet-code-review` core matches `agents/dotnet-code-review.md`'s six exactly; every "When" cell states an **observable property of the code** rather than a topic; and the `pluginRoot` join matches what `workspace_status` actually returns, naming **exactly one** location per file — a per-repo override tier reappearing is the drift to flag |
| `skills/dotnet-read/SKILL.md` | (a) it names every retrieval tool from Step 0, each with a **Manual:** pointer whose filename is derived (`<tool>.md`, the four server/meta tools sharing `server.md`); (b) its per-tool "Answers to" lists stay questions, never argument grammar — that is the manual's job; (c) it owns the **cheap-route table** and the `limitedBy`/TOON response conventions, which must not drift back into the always-loaded rule; (d) its step 0 mandates `workspace_status` before any read, and the `${CLAUDE_PLUGIN_ROOT}`-is-not-expanded warning is intact |
| `skills/dotnet-write/SKILL.md` | (a) its pre-edit standards step **defers to `standards/index.md` rather than re-listing the files**; (b) it resolves standards as `<pluginRoot>/standards/<name>.md` via `workspace_status`, with no override tier; (c) **it owns the write-*decisions*** — when to dry-run, when to amend versus rebuild, the definition of done, raising `.editorconfig` severity with the user rather than unasked — which were moved out of the always-loaded layer and must not drift back; (d) its step 0 mandates `workspace_status` before an edit and `reload_workspace(scope: "all")` after a `.cs` file is added or deleted. Its three known duplication risks (standards list, write-time checklist, `validate_patch` error codes) are `harness-compliance.md` §A |
| `skills/dotnet-explore/SKILL.md` | it is the **only** sanctioned launcher of the `dotnet-explore` agent. It must check workspace readiness before spawning, must state that the agent relays no `contentVersion`, and must not restate the agent's own router — `agents/dotnet-explore.md` is self-contained and authoritative |
| `skills/dotnet-review/SKILL.md` | it does **not** claim to inject a `Standards root:` into the spawn — the agent resolves its own `pluginRoot` from its own `workspace_status` call (§below), so the skill's job is only to state each instance's scope/mode/focus, never a path. It must also check workspace readiness **before** spawning: launching parallel instances against a degraded workspace is a parallel waste, and it points at `standards/index.md` rather than restating the table |
| every file in `standards/` | every MCP tool named in them (e.g. `get_references` in `testing.md`'s calibration, `get_symbol` in `xml-documentation.md`'s) still exists with the described behavior; cross-file pointers still resolve; **none offers a per-repo override path** — that tier was removed, and a reintroduced sentence would promise a mechanism nothing implements. The no-frontmatter invariant is `harness-compliance.md` §G |
| `skills/dotnet-init/SKILL.md` | that it still **copies `.claude/rules/dotnet-index.md` verbatim** rather than embedding its own tool table or standards list. Also the "what is deliberately *not* written" table and the uninstall list: every asset the plugin ships is in exactly one of write / not-written / uninstall, and the write and uninstall lists name the same files |
| `docs/install/audit.md` | the install-procedure audit this skill runs in Step 5b (maintainer-facing; the consumer never reads it). Its four-mechanism inventory must cover every top-level directory the plugin actually ships (`skills/`, `agents/`, `hooks/`, `docs/`, `docs/tools/`, `.claude/rules/`, `scripts/`, `.mcp.json`), and its reachability scenarios must resolve to files that exist |
| `docs/install/{install,verify,uninstall}.md` | the three paths `dotnet-init` routes to. They name the files init actually writes (including the settings allowlist and `install.json`) and no file it doesn't, and the skill's router table points at all three |
| `skills/dotnet-selfeval/SKILL.md` + `analyses.md` | the efficiency probe matrix. Its Step 2 families still cover every tool from Step 0; the tools it lists as recording **no** telemetry (`ping`, `workspace_status`, `set_output_format`, `reload_workspace`, `get_retrieval_metrics`) are still exactly the ones with no `ToolTelemetry.Record`/`RecordPatch` call; its `taskId`/`taskIds`/`groupBy:"task"` recipe still matches `MetricsTools`/`MetricsReader`. A tool newly instrumented but still listed as unmeasurable understates what the evaluation can see |
| `agents/dotnet-code-review.md` | the agent's **complete, self-contained** instructions. `tools:` frontmatter matches Step 0's read-side subset (nothing stale, still excluding `get_project_graph`/`detect_circular_dependencies`, withheld as out of a scope slice's reach); every tool it names in Process, evidence bars and Boundaries is granted there *and* behaves as described; its standards core and per-aspect fold-ins resolve to real `standards/` filenames, reached through the `pluginRoot` it resolves itself from its own `workspace_status` call and **never** by constructing a `${CLAUDE_PLUGIN_ROOT}` path itself; the no-root fallback still reports standards-derived aspects as not-assessed rather than reviewing from memory; it still requires the `Standards:` line; and it has **not** re-acquired a `skills:` grant or a directive to read `docs/design/agents.md` — both removed to hold the per-instance token baseline down |
| `agents/dotnet-explore.md` | the navigator's **complete, self-contained** instructions. Its router must name no write tool as callable, and three properties are load-bearing (rationale in `docs/design/agents.md`, not here): **no writer and no `memory:` key** in `tools:`, `Read` whitelisted to `docs/tools/<tool>.md` only, and no relaying of a `contentVersion` |
| `docs/design/agents.md` | covers **both** agents; **human-facing only — neither agent is told to read it.** It must not contradict the agent file (authoritative); its tool-grant and token-budget sections match the agents' actual frontmatter and loading rule; every tool it names still exists |
| `docs/design/hooks.md` | maintainer-facing design notes; nothing routes to it. Describes exactly the hooks `hooks/hooks.json` registers and the behavior they implement — matchers, allow/deny cases, fallback chain |
| `README.md`'s Features table | every tool from Step 0 appears in some row; no row names a tool that no longer exists |

**5b. Consumer reachability — does the plugin work out of the box?** Steps 2–5 check that the files
describing the tools are *accurate*. This checks they are *reachable from a consuming repo*, where
this repo's `CLAUDE.md` does not exist. Every operational instruction must live in something that
ships: the one rule init copies (`.claude/rules/dotnet-index.md`), a skill, an agent file, or a
`docs/`/`standards/` file reachable by `${CLAUDE_PLUGIN_ROOT}` path **from a skill** — rules and agent
files must not depend on that variable expanding, which is why `agents/dotnet-code-review.md` resolves
its own `pluginRoot` from its own `workspace_status` call instead of expecting a path handed to it.

**The install procedure is half of this step.** `dotnet-init` is prose, hand-maintained, and
describes a file set that grows every time this plugin gains a tool, doc, skill, or standard. When it
falls behind, the failure is silent in both directions: an asset ships but never reaches the consumer,
or the uninstall instructions leave files behind that keep steering a repo that no longer has the
plugin. **Read `docs/install/audit.md` and run it.** Two of its findings are hard errors worth naming
here because both have shipped: a `${CLAUDE_PLUGIN_ROOT}` path in a *copied rule*, and any claim that
mechanism-1 assets need a repo-local install or cleanup step.

Then two sweeps, both cheap:

- **This repo's `CLAUDE.md`, paragraph by paragraph.** For each instruction, decide: is this *about
  maintaining the plugin* (correct — it belongs only here), or *about using the tools*? Anything in
  the second category must also exist in a shipped file, with `CLAUDE.md` carrying a pointer rather
  than a second copy. The write-path discipline is the canonical example — the `validate_patch`
  argument in "Non-negotiable workflow" is the same argument the init template and `dotnet-write`
  must both make, because a consumer never sees `CLAUDE.md`.
- **The maintainer's memory directory** (`~/.claude/projects/<repo-slug>/memory/`, indexed by its
  `MEMORY.md`). Read the index, then each entry, and sort every one into: **(a)** operational for
  consumers → must be embedded in a shipped file; **(b)** operational for this repo only → belongs in
  `CLAUDE.md` or `.claude/rules/`; **(c)** personal or environment-specific → belongs in neither, and
  is correctly only in memory. Memory is user-local and does not travel with the plugin, so anything
  in (a) or (b) that exists *only* there is a finding — silently absent for every other user, and for
  this repo after a memory reset.

Report each finding as: the memory or `CLAUDE.md` paragraph, its category, and the shipped file that
should carry it. Do not copy a category-(c) fact into a shipped file to close a finding —
categorising it correctly *is* closing it.

**6. Hooks and launch path.** All five hooks are `hook <name>` subcommands of the published server
binary, in `src/DotnetToolkit.McpServer/Hooks/` — `HookCli.cs` dispatches, the
`Guard*`/`ReloadHint`/`WriteChecklistHint` files carry the messages,
`CsFileMembership.cs`/`BashCommandScanner.cs` are shared with no `hooks.json` entry. Read those with
`get_symbol` (not `grep`), plus `hooks/hooks.json` and `.mcp.json`:
   - Does each guard's deny/hint text still name the correct tool(s) and procedure (`validate_patch`'s
     current argument names, `search_index`/`get_symbol` for the read guards,
     `reload_workspace(scope: "all")` for the reload hint)? It is read at the exact moment a caller is
     blocked — a stale one teaches the wrong fix at the worst moment.
   - **`hint-write-checklist` owns the write-time checklist**, and is the only hook matching an MCP
     tool rather than a built-in. Its text must still match the standards it summarizes
     (`security.md`, `testing.md`), and it must stay once-per-session and fail-quiet — a version that
     fires on every patch, or that denies, is a finding. Matching an MCP tool name in `hooks.json` is
     verified working; don't "fix" it back to a built-in.
   - Does `hooks/hooks.json` name subcommands `HookCli` actually dispatches, with matchers
     (`Edit`/`Write`/`NotebookEdit`/`Read`/`Bash`, plus the fully-qualified `validate_patch` MCP tool
     name) matching `docs/design/hooks.md` and `docs/design/architecture.md`'s "Packaging" section?
     The shell-free and output-cap invariants are `harness-compliance.md` §G. `scripts/` holds
     developer conveniences only; anything new there or in `Hooks/` unmentioned by
     `docs/design/hooks.md` or "Packaging" is a Step 7 finding.
   - Matchers key on tool name, not on which agent calls, so they fire for subagents too. For
     `dotnet-code-review`: (a) its `tools:` list never grants `Edit`/`Write`/`NotebookEdit` —
     `memory: project` makes the harness grant them anyway, which is why Boundaries carries the
     weight; (b) its Process step 2 still goes to `search_index`/`get_symbol` first and to `Read` only
     when a symbol lookup didn't give it the lines — a narrow fallback `guard-cs-read` still enforces,
     not an escape hatch. For `dotnet-explore` the guard is only a backstop — its own file bans `Read`
     on `.cs` outright, so a denial there means that file drifted.

**7. Internal drift.** Read `drift.md` and run it. This is the step that starts from *what changed*
rather than from a file list: `get_semantic_diff` against a baseline, then `search_log` for the
recorded intent, then `git` for everything the development log cannot see, checked against the
drift-sensitivity ranking. It is how a shipped-but-undocumented tool gets found — nothing in a
per-file check ever will, because the files that should mention it are internally consistent.

**8. The skills' own instructions.** Once Steps 1–7 have surfaced concrete drift, the fix usually
touches a skill body, not just a table row — e.g. a new tool needs a section in `dotnet-read` or
`dotnet-write` (whichever side it belongs to), a row in that skill's cheap-route table if it
displaces an existing route, *and* its own `docs/tools/<tool>.md`. **It needs nothing in
`.claude/rules/dotnet-index.md`**, which names no tools: a new row there is only warranted by a new
*skill*. Update the skill body, not only its tool list, so a caller reading the skill gets the same
guidance a caller reading the code would.

**9. `CLAUDE.md` and `README.md` last.** The two files a fresh session or a new user reads first, so
they should reflect the *already-corrected* state of everything above rather than being patched
independently of it. Update `CLAUDE.md` per Step 5's table, then `README.md`'s Features table and prose.

## Output format

Report drift the same way `dotnet-code-review` reports findings — concrete and file-anchored, not a
narrative:

- **File:line** of the stale claim.
- **What it claims** vs. **what is actually true**, naming which source of truth decided it — the code
  (Steps 0/2) or the official guidance (`claude-docs.md`, via Step 1).
- **Exact fix** — the replacement text or the specific file to add a row to. Point at the file; don't
  describe the fix in the abstract.

A finding driven by guidance rather than by code, where the **code** is what must change, uses a
distinct form so it is never mistaken for something this skill already fixed:

> **CODE FINDING** — `src/…/Tools/X.cs:NN` · guideline: `harness-compliance.md §B` ·
> current: *<measurement or text>* · required: *<threshold>* · proposed replacement: *<exact text>* ·
> apply via: `dotnet-write` → `validate_patch`.

Group findings by file, in Step 5's table order, then hooks, then `CLAUDE.md`/`README.md` last, with
`CODE FINDING`s collected together at the end. State the baseline Step 7 used. If a section is in
sync, say so in one line — don't manufacture findings to justify the run.

## Fixing vs. reporting

- **Doc findings: fix them yourself**, in Step 1→9 order. Every such fix is to a non-`.cs` file, so
  `Edit`/`Write` apply directly, not `validate_patch`.
- **`CODE FINDING`s: report only, never apply.** This skill does not edit `.cs`, even when the fix is
  a one-line attribute change with no behavior impact — an audit that can change the tool contract
  can no longer be run freely. Hand it off with the exact replacement text; the user or a follow-up
  `dotnet-write` task applies it.
- **Never silently skip a step** because "nothing looks wrong there" — state it was checked and came
  back clean. A skipped step and a clean step read identically in a report, and only one of them is
  true.
