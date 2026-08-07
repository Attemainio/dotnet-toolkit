# Harness compliance checklist

Read by `SKILL.md` Step 1. Every threshold here cites a row in `claude-docs.md` — if a citation is
missing, the threshold is folklore and should be argued or deleted, not enforced.

**This is the axis that can produce code findings.** Steps 2–6 of the audit ask "does the doc match
the code?"; this file asks "does the *code* match the guidance?", and when it doesn't, the code is
what moves. The skill still never edits `.cs` — see `SKILL.md`'s *Fixing vs. reporting* for the
`CODE FINDING` format and the hand-off.

Work the sections in order. State each one checked, clean or not; a silently skipped section reads
identically to a clean one and that is the failure mode this whole skill exists to prevent.

---

## §A — No duplication across instruction files

**Rule: one owner per fact, everyone else points at it.** Two copies always diverge, and the copy
that diverges is never the one you are reading.

For each pair below, confirm the second file **points** rather than restates:

| Fact | Sole owner | Must only point |
| --- | --- | --- |
| Which standards apply when | `standards/index.md` | `skills/dotnet-write/SKILL.md`; `skills/dotnet-review/SKILL.md`; `agents/dotnet-code-review.md`'s load rule |
| The write-time checklist | the `hint-write-checklist` hook | `skills/dotnet-write/SKILL.md` |
| `validate_patch` error codes, draft lifetime, severity table | `docs/tools/validate_patch.md` | `skills/dotnet-write/SKILL.md` |
| The intent→**skill** router | `.claude/rules/index.md` | `skills/dotnet-toolkit-init/SKILL.md` (copies the rule verbatim; never embeds its own); `CLAUDE.md`; `README.md` |
| The intent→**tool** router, and the **cross-tool** cheap-route (anti-pattern) table | `skills/dotnet-read/SKILL.md` for reads, `skills/dotnet-write/SKILL.md` for writes | `.claude/rules/index.md` (names skills, never tools). A `docs/tools/<tool>.md` **Next steps** footer is a *different* fact — what to call next with the response in hand — and stays; a general "instead of X do Y" catalogue reappearing in one is the duplicate |
| Per-tool mechanics, arguments, examples | `docs/tools/<tool>.md` | `skills/dotnet-read/SKILL.md`; `skills/dotnet-write/SKILL.md`; tool `[Description]`s |
| Guard denial messages' remedy | the skill each guard names (`dotnet-read`/`dotnet-write`) | `Hooks/GuardCs*.cs` (name the skill; never restate its tool usage) |
| Agent instructions | `agents/<name>.md` | `docs/design/agents.md` (human-facing; neither agent reads it) |
| Hook behaviour | `src/DotnetToolkit.McpServer/Hooks/*` | `docs/design/hooks.md`, `hooks/hooks.json` |
| Always-loaded size policy | **this file, §D** | `CLAUDE.md` |

Each of these has drifted at least once. A reappearing copy is the finding — report the *duplicate*
for deletion, not the owner for correction.

**Method.** For a suspected duplicate, grep the distinctive phrase across `skills/`, `agents/`,
`docs/`, `standards/`, `.claude/rules/`, `CLAUDE.md`, `README.md` (all non-`.cs`, so ordinary `Grep`
is fine). Two hits outside the owner means a copy, not a coincidence.

---

## §B — `[Description]` attribute budget in `.cs`

**Threshold — house rule, not an Anthropic cap.** The official guidance (`claude-docs.md` →
*Defining tools*) pushes the *other* way: detailed, 3–4 sentences minimum, no stated maximum.
Anthropic does not publish a character limit. Say so plainly wherever this is cited; a future reader
must not be able to attribute the number to Anthropic.

The number is ours, and the argument for it is tool-search economics: a deferred schema is re-paid in
full every time the tool is fetched, so a `[Description]` that has grown into a manual is a per-call
tax on top of the `docs/tools/<tool>.md` that already says it better.

| Check | Threshold | Action when exceeded |
| --- | --- | --- |
| Per-tool `[Description]` | ≤ 2,000 chars | `CODE FINDING` — propose the compacted text |
| Single parameter `[Description]` | ≤ 500 chars | `CODE FINDING` — move the detail to `docs/tools/<tool>.md` |
| Same parameter text repeated verbatim | across ≥ 3 tools, **in source** | `CODE FINDING` — extract to a shared const, then shorten once. Check the source, not just the rendered schema, before flagging this: the schema will always show the full text at every site regardless, since each deployed tool must carry its own complete `[Description]` — that repetition is inherent to MCP and not evidence of a source-level duplicate |
| Tool description *shorter* than one sentence | — | also a finding, the opposite direction: too thin to rank |

**Measuring.** `get_symbol(include: "attributes")` truncates attribute text at ~120 characters, so it
proves *which* attributes exist but cannot measure them. For full text, load the tool's schema —
`ToolSearch("select:mcp__plugin_dotnet-toolkit_dotnet__<tool>")` — and measure what the server
actually registered. That is also the exact string tool search ranks against, which makes it the
right thing to measure rather than the source literal.

**Baseline measured 2026-08-06** — re-measure, don't trust:

| | |
| --- | --- |
| Tools | 18 |
| Longest description | `get_references`, 1,850 chars |
| Descriptions in the 1,490–1,850 band | 5 (`get_references`, `get_symbol`, `get_call_hierarchy`, `get_scope`, `search_index`) |
| All tool descriptions | ≈ 16,100 chars |
| All parameter descriptions | ≈ 15,900 chars |
| `taskId` parameter blurb | 396 chars × 13 tools = **5,148 chars — 32% of all parameter text** |

Nothing exceeded 2,000, so §B's per-tool rule is currently a **guard**, not a repair. The `taskId`
blurb's 5,148 rendered chars are **not** a live finding under the third row: the source already holds
the text once, as `ToolTelemetry.TaskIdParam`, referenced by `[Description(ToolTelemetry.TaskIdParam)]`
at all 13 sites (confirmed 2026-08-06 by reading each site's source, not just the compiled schema).
The rendered-schema repetition is unavoidable MCP protocol overhead, not source duplication — don't
re-flag it without checking the source first.

---

## §C — Tool-search discovery

Tool schemas are deferred by default on the models this plugin targets (`claude-docs.md` → *Tool
search*). The model sees **names only** until it fetches. Two consequences the audit must check:

**C1 — the searchable index is real text.** Names, descriptions, argument names, argument
descriptions. Check each tool:

- Name matches `^[a-zA-Z0-9_-]{1,64}$`, including the `mcp__plugin_dotnet-toolkit_dotnet__` prefix the
  harness prepends — the prefix is 34 characters, so a bare tool name over 30 is a finding.
- The description **leads with the question the tool answers**, in the vocabulary a caller would
  actually use, before any mechanics.
- It names its own `docs/tools/<tool>.md`, since that pointer is what a caller sees without the rule.
- Neighbouring tools use **distinct** vocabulary. `get_references` / `get_call_hierarchy` /
  `get_call_slice` are the standing risk: three tools about "who calls what". If their descriptions
  converge, all three rank for every query and none wins.

**C2 — the live test.** Search returns at most ~5 matches, so ranking sixth is the same as not
existing. For a sample of tools, run a *natural-language intent* phrase through `ToolSearch` — the
phrasing a caller would use, not the tool's name — and record whether the right tool appears.

The reason `.claude/rules/index.md` mandates loading by exact `select:` name: **"who calls this
method" used to never surface `get_references` at all — confirmed fixed 2026-08-06** (description
edits in `8b8f506`, re-probed fresh in this session: `get_references` now ranks 3rd of 5). Re-run the
probe anyway on every audit — a description edit can regress it — and if it fails again that is a
`CODE FINDING` against `get_references`'s description. The router in `index.md` stays load-bearing
regardless of this result: it is strictly cheaper than a search round trip, which is why §E keeps it
even with the description fix in place.

**Prior measurement: `docs/design/route-table-findings.md`** (2026-08-05) — how the two selection
mechanisms interact, the measured `ToolSearch` probes, and what the eval corpus says is fixed versus
still open. Read it before re-running C2 so a probe that already failed once is recognised as a
regression rather than reported as new. It is the only record of these measurements; if a probe result
here contradicts it, update that file in the same pass.

---

## §D — The always-loaded budget

**This section is the owner of the size policy.** `CLAUDE.md` points here and must not re-derive it.

Exactly two files are always loaded:

| File | Official | Repo target | Why it is paid more than once |
| --- | --- | --- | --- |
| `CLAUDE.md` | under 200 lines | ~5 KB | every session; **inherited by every subagent, no opt-out** |
| `.claude/rules/index.md` | under 200 lines | ~6 KB | the same, **plus** every session in every consuming repo `dotnet-toolkit-init` has touched |

A seven-way parallel review pays both files eight times. That multiplier, not the raw size, is the
argument.

```bash
for f in CLAUDE.md .claude/rules/index.md; do
    printf "%-30s %4d lines  %6d B  ~%.1fk tok\n" \
        "$f" $(wc -l < "$f") $(wc -c < "$f") $(echo "$(wc -c < "$f")/3800" | bc -l)
done
```

**Baseline measured 2026-08-07**, after `index.md` was reduced to a pure skill router and its tool
table, `limitedBy` section and standards table moved into `skills/dotnet-read`, `skills/dotnet-write`
and `standards/index.md`: `.claude/rules/index.md` fell from 161 lines / 10,590 B to roughly a third
of that, inside the byte target. `CLAUDE.md` was 127 lines / 8,293 B — inside the official line
limit, over the repo byte target. Re-measure before reporting either.

Three rules for acting on an overage:

1. **Never close an overage by deleting guidance.** Move it behind a pointer into a skill, a
   `docs/` file, or a `standards/` file. Deleting it means the next session simply doesn't know.
2. **The targets are arguments, not walls.** If the guidance genuinely must be known before a caller
   can ask a question (§E), it stays and the target loses. Say which one you chose and why.
3. **Content that reappears here is the finding**, not the byte count: an architecture rundown, a tool
   catalogue, a per-tool procedure, or a standards body in an always-loaded file is drift regardless
   of size.

**Skills, `standards/` and `docs/` have no size budget.** They load on demand and cost nothing until
invoked (`claude-docs.md` → *Skills authoring*). **Do not report a long skill as a finding.** A
single-purpose skill should carry its whole procedure inline.

### Scatter — the finding to look for instead of length

- A `docs/` file that exists only because some skill got long: one job across two files, so both must
  be updated and the pointer can go stale. **Fix: fold it back.**
- A skill with **several** named responsibilities carried inline, where a reader on one path pays for
  the other two. **Fix: one file per responsibility**, as `dotnet-toolkit-init` does — and as this
  skill now does with `claude-docs.md` / `harness-compliance.md` / `drift.md`.
- A `docs/` file nothing reads — the terminal form of the first case. `drift.md` catches these.

The test is **responsibility, not byte count**. Splitting a single-purpose file to hit a number
produces scatter, which is strictly worse than the length it fixed.

---

## §E — What `.claude/rules/index.md` may contain

**Admission test: does Claude need this *before* it can ask the right question?** If the answer can
be fetched once the need is recognised, it belongs downstream and `index.md` carries only the pointer.

**`index.md` is a pure skill router.** It names **no MCP tool at all**. A tool name appearing in it
is the finding, not a detail to correct.

**Admitted** — each because nothing in context would otherwise reveal it:

- **The intent→skill mandate**: read C# → `dotnet-read`; write C# → `dotnet-write`; survey an unknown
  symbol set → `dotnet-explore`; review → `dotnet-review`. Triggers only, never a summary of what
  each skill carries — that is in its always-loaded description already (§F).
- **That the MCP tools, not Grep/Read/Edit, are the C# path at all**, with the one-line reason (text
  search is wrong on C#, not merely slower). Without this a caller never recognises there is a skill
  to invoke.
- **That agents are launched by skills, never directly** — otherwise a caller spawns
  `dotnet-explore` or `dotnet-code-review` raw and skips the readiness check and the brief.
- **The non-C# escape hatch** — shell, git, and Markdown/JSON/csproj editing are unaffected — so the
  rule is not read as blocking ordinary work.

**Excluded** — all fetchable the moment the need is recognised, because the *skill descriptions that
route to them are themselves always loaded* (§F):

- **The intent→tool router and the `select:`-by-exact-name discipline** → `dotnet-read` /
  `dotnet-write`. This moved out deliberately: it was ~100 always-loaded lines paid by every session
  and every subagent, including the ones that never touch C#.
- **The cheap-route / anti-pattern table** → the same two skills. It used to sit only inside
  individual `docs/tools/<tool>.md` files, which are not loaded unless something already went
  looking — so the anti-pattern was committed before the note preventing it was ever read.
- **`limitedBy` semantics and the response conventions** → `dotnet-read` (`dotnet-write` points at it).
- **The standards trigger table** → `standards/index.md`, shared by `dotnet-write` and
  `dotnet-code-review`.
- **The `pluginRoot` join** → step 0 of `dotnet-read` and `dotnet-write`.
- Per-tool arguments, defaults, response fields, examples → `docs/tools/<tool>.md`.
- Standards *bodies* → `standards/`.
- The write *procedure* and write *decisions* → `skills/dotnet-write/SKILL.md`.
- Size policy → **this file, §D**.

---

## §F — What is always in context, and what that forces into the router

The loading model is the reason `index.md` exists in its current shape. Making it explicit here means
§E's admission test has a mechanism behind it rather than taste.

| Always in context | Only after a fetch | What that forces |
| --- | --- | --- |
| Agent `description` frontmatter, every agent | Agent body / prompt | Agent **selection is description-driven**. The router needs only the *mandate* — delegate the sweep, never review C# inline — never a summary of what the agent does. A summary here is pure duplication paid every session. |
| Skill `description` frontmatter, every skill | `SKILL.md` body and its reference files | Same: **triggers only**. Re-explaining what a skill carries is duplication charged to every session and every subagent. |
| MCP tool **names** | Tool **descriptions** and full schemas | Tool **usage cannot be description-driven**. This is the asymmetry: agents and skills advertise themselves, tools do not. So the intent→tool mapping *and* the `select:`-by-name rule have to be written down somewhere — but **not** always-loaded. A skill's description *is* always loaded, so `dotnet-read`/`dotnet-write` can be selected the same way an agent is, and the tool router rides in on their bodies. That is what lets `index.md` name no tools while the mapping still reaches every caller who needs it. |
| `CLAUDE.md` and `.claude/rules/index.md`, in full | `standards/`, `docs/`, skill reference files | The only two files under §D's budget. |
| — | Hook messages, shown at deny time | Never in context, but read at the exact moment a caller is blocked. A stale guard message teaches the wrong fix at the worst possible moment, which is why they rank Tier 1 in `drift.md`. |

Two checks fall out of the table:

- **Every agent and skill description must be written as a trigger** — the conditions under which it
  should be picked — because that text is always resident and is the entire basis for selection.
- **No agent or skill description may be a summary of its own body.** The body is fetched on
  selection; a summary is paid always and read never.

---

## §G — Skill / agent / rule / plugin file conformance

Structural checks against `claude-docs.md`. Cheap, and each has a silent failure mode.

- **Frontmatter completeness.** Every `SKILL.md` has `name` + `description`; every `agents/*.md` has
  `name` + `description`. A missing `description` means the file is never selected, with no error.
- **`.claude/rules/` holds exactly one file with no frontmatter, and it is `index.md`.** A second
  unfrontmattered rule is an always-loaded file nobody costed, charged to every session and every
  subagent.

  ```bash
  awk 'FNR==1 && !/^---/ {print "unfrontmattered rule: " FILENAME}' .claude/rules/*.md
  ```

- **No file under `standards/` carries frontmatter.** `paths:` is meaningful only inside a rules
  directory; elsewhere it does nothing and signals a partial revert toward the old layout, where it
  was actively harmful — the read guard permits `Read` on `.cs` files no project compiles, so the glob
  fired unpredictably instead of on demand. Rationale in `docs/design/architecture.md`.

  ```bash
  awk 'FNR==1 && /^---/ {print "FINDING: frontmatter on " FILENAME}' standards/*.md
  ```

- **`workspace_status` still reports `pluginRoot`, and `docs/tools/server.md` still documents the
  join.** It is the only route from an always-loaded rule or a subagent to the plugin's own files;
  dropping it silently strands every standards read and every manual read. Contract 3.50.
- **`${CLAUDE_PLUGIN_ROOT}` never appears in a rule or an agent definition** except in the sentence
  forbidding it. The harness expands it in `.mcp.json` args, hook commands and skill content — not in
  a rule (delivered literally into a consuming repo) and not in an agent definition. A path built
  from it there is dead text.
- **Plugin layout.** `skills/`, `agents/`, `hooks/`, `.mcp.json`, `scripts/` at the plugin root; only
  the manifest inside `.claude-plugin/`. Confirm with `claude plugin validate .` if the CLI is
  available; report unavailability rather than assuming clean.
- **Hook output stays under 10,000 characters** and every hook exits 0 or 2 deliberately — a guard
  that exits 1 fails open while looking healthy.
- **Nothing shipped at runtime requires a shell.** Every `.mcp.json` / `hooks.json` command is a
  `dotnet <dll> …` invocation. A `.sh`/`.ps1` entry point, a shebang, or a `node`/`python3`/`jq`
  dependency is a finding: an stdio MCP server is spawned directly, so a script launcher cannot run on
  Windows at all, and a Store-stubbed `python3` makes a guard fail open.
