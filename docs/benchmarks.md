# Benchmarks

How dotnet-toolkit's MCP tools compare against answering the same questions with `Read`, `Grep`,
`Glob` and `Bash` — method, full results, caveats, and the questions the raw route won.

Summary table and headline numbers are in the [README](../README.md#measured-against-raw-tools).

> This page is the maintained record. The raw run files `/dotnet-performance` writes are **not
> committed** — each measures the tools as they stood on its own date and goes stale as soon as the
> finding it raised is fixed, so everything worth keeping from them is transcribed here instead.
> Run the skill yourself to generate current ones against your own solution.

## Method

Each run is driven by the `dotnet-performance` skill:

1. The orchestrator builds a question matrix from the target solution — exact-name lookups, fuzzy
   lookups, one method inside a very large file, partial-class enumeration, call-site counting on a
   common name, interface implementers, override enumeration, dead-symbol confirmation, base chains.
2. That identical list goes **verbatim and blind** to three subagents, run sequentially:
   - `dotnet-perf-full-probe` — granted **both** tool families at once, exactly as the plugin ships.
     This is **practice**: what a real, unrestricted session with this plugin installed actually does,
     free to follow or ignore an advisory note per question. Added 2026-08-22.
   - `dotnet-perf-mcp-probe` — granted **only** the dotnet-toolkit MCP tools plus `Read`, narrowed by
     instruction to ranged reads over `declarationSites`. No `Grep`, `Glob` or `Bash` exists in its
     grant, so there is no raw shortcut to fall back on. This is the **ceiling**.
   - `dotnet-perf-raw-probe` — granted **only** `Read`/`Grep`/`Glob`/`Bash`. No MCP tool of any kind;
     told to ignore the advisory note, which it has no MCP tool to act on anyway. This is the **floor**.
3. No probe sees another's answers, or the orchestrator's own prior exploration.
4. All three receive the same `performance_protocol.md`, so the comparison isolates the tool family
   rather than the instructions.
5. **All three run at the plugin's default guard state; nothing is denied to any of them.** Whether a
   probe kept to its route is read off the meter afterwards: it records, per call, whether a compiled
   `.cs` file was put into context whole, and `get_retrieval_metrics` reports the count per agent as
   `wholeFileCsReads`. A nonzero count on the MCP probe means the ceiling is contaminated and the
   report says so. Until 2026-09-20 the same discipline was enforced up front by switching guard
   state (`suspend` around the raw probe, `block` around the MCP probe); Claude Code's auto-mode
   classifier denies that switch as self-modification, which stopped every auto-mode run, and the
   count answers the same question without it. Specimens A and B were run the old way.
6. **Ground truth for every contested answer is established independently by the orchestrator** —
   never taken from a probe's self-report.

Every MCP-granting probe also holds `ToolSearch` (added 2026-08-25). The harness defers tool schemas in
sessions carrying many tools, and a subagent without `ToolSearch` cannot load one — every MCP call comes
back `No such tool available`, so `dotnet-perf-mcp-probe` answers nothing and `dotnet-perf-full-probe`
silently degrades into a second raw probe while still producing a report-shaped result. Step 0 of the
skill now checks the grant before a run and `harness.byAgentTool` after it; **a run showing zero MCP rows
for `dotnet-perf-mcp-probe` is an instrument failure, not a result.** The specimens below predate the
grant and are unaffected — their MCP legs returned real answers, which is only possible with the schemas
loaded.

> **The results below predate the third probe.** Both published specimens used the original two-route
> methodology — `dotnet-perf-mcp-probe` against `dotnet-perf-raw-probe` only, with no `dotnet-perf-full-probe`
> leg. They still show the ceiling-vs-floor comparison honestly; a re-run with the current three-probe
> methodology would add the practice measurement rather than change these numbers, and would supersede
> this page's specific figures without changing the pattern they show.

### The instrument

All three routes are metered by **one** `PostToolUse` hook that fires on harness dispatch, so `Grep` and
`get_symbol` are counted the same way. That matters more than it sounds: on every run so far, the raw
probe has under-reported its own call count by roughly half.

| Run | Raw probe self-reported | Meter | True `tool_uses` |
|---|---:|---:|---:|
| 2026-08-13 | 35 | 49 | 49 |
| 2026-08-17 | 38 | 53 | 54 |

Scored on self-reports, the 2026-08-17 run would have looked like 38 calls against 25 — a 1.5× gap
instead of the true 2.0×. **This is why the self-report is a cross-check and not the instrument.**

Token counts are `chars4` approximations — `(length + 3) / 4` over the serialized payload — applied
identically to every route. The **ratios** are sound; the absolute figures are not exact.

## Results

### Specimen A — a private trading/ML solution

290 `.cs` files, 505 types, 3 projects. Largest file 2,362 lines (next: 1,871, 1,851, 1,613).
10 questions, 2026-08-17.

| | With the plugin | Raw tools | Ratio |
|---|---:|---:|---:|
| **Correct answers** (of 10) | **8** | 7 | |
| Tool calls | **26** | 53 | 2.04× |
| **Tool-result tokens** | **12,932** | 29,242 | **2.26×** |
| Tool-call argument tokens | **482** | 1,691 | 3.51× |
| Total agent tokens | **46,219** | 65,474 | 1.42× |

Server-side cross-check: `taskId perf_mcp_20260817` recorded 24 calls / 11,436 tokens returned against
the harness meter's 26 / 12,932. The difference is the probe's `Read` of the protocol file plus one
non-MCP call, which the server never sees. The two instruments agree.

### Specimen B — this repository

160 indexed files, 260 types, 2 projects. Largest source file 2,818 lines (`ContextTools.cs`).
11 questions, 2026-08-13.

| | With the plugin | Raw tools | Ratio |
|---|---:|---:|---:|
| **Correct answers** (of 11) | **10** | 10 | |
| Tool calls | **20** | 49 | 2.45× |
| **Tool-result tokens** | **12,650** | 37,551 | **2.97×** |
| Tool-call argument tokens | **342** | 2,648 | 7.74× |
| Total agent tokens | **40,940** | 70,931 | 1.73× |

### Specimens C and D — the navigation-only surface (2.0.0)

Both run on 2026-09-20, after `get_symbol` stopped returning source and the read guards became
advisory-only — so "with the plugin" here is a session that *locates* with the MCP tools and reads
the located span with a ranged `Read`, exactly as the plugin ships. Three probes each, run at the
default guard state; the MCP-only probe's `wholeFileCsReads` was 0 on both, so the ceiling is clean.

**Specimen C — FluidScript**, a private physics/layout engine. 316 files, 649 types, 5 projects, 78 175
lines; largest file 3 569 lines. 10 questions.

| | Both families (default) | MCP only (ceiling) | Raw tools (floor) | Default vs raw |
|---|---:|---:|---:|---:|
| **Correct answers** (of 10) | **10** | 10 | 9 | |
| Tool calls | **29** | 37 | 52 | 1.79× |
| **Tool-result tokens** | **15,789** | 16,219 | 24,185 | **1.53×** |
| Tool-call argument tokens, tools only | **704** | 1,051 | 1,806 | 2.57× |
| Total agent tokens | **57,445** | 52,424 | 64,406 | 1.12× |

**Specimen D — PandaAI**, a private trading/ML solution (Specimen A a month on: 329 files, 551 types,
6 projects; largest file 1 585 lines). 10 questions, one scored as a wash — its wording described no
type in the repo, an orchestrator error — leaving 9.

| | Both families (default) | MCP only (ceiling) | Raw tools (floor) | Default vs raw |
|---|---:|---:|---:|---:|
| **Correct answers** (of 9) | **9** | 9 | 7 | |
| Tool calls | **42** | 34 | 45 | 1.07× |
| **Tool-result tokens** | **25,662** | 28,962 | 45,357 | **1.77×** |
| Tool-call argument tokens, tools only | **1,015** | 910 | 1,585 | 1.56× |
| Total agent tokens | **79,526** | 78,695 | 87,218 | 1.10× |

"Tools only" argument tokens exclude each probe's final report (`SubagentHandback`, 1.1–2.0k tokens),
which the meter now records as a call and which is the same shape on every route.

What the two runs add to A and B:

- **The default session used the MCP route on every question, unprompted.** Neither full probe made
  a whole-file `.cs` read, so no advisory fired and none could be followed or ignored. On a wired
  repo the router rule is doing the routing; the guards are not.
- **The full probe beat the MCP-only ceiling on both.** Not because raw tools helped — it made one
  `Bash` call in twenty questions — but because two agents with the same tools spent differently:
  the MCP probe put 9 calls into one dispatch question the full probe answered in 2, and three
  150-line ranged reads into a question the full probe answered with one 100-line read. The MCP
  route is only as cheap as the ranges the agent picks; `bodyOutline` exists for that.
- **The whole-agent gap is ~10 %.** A 20–30k saving in tool results survives as 7–9k at the agent
  level, because bootstrap and reasoning dominate a 10-question subagent. The saving is real and is
  where the plugin claims it — per call — and it is proportionally larger in a long main-thread
  session that pays bootstrap once.
- **The raw route's three wrong answers are the always-loaded rule's claims, verbatim:** a
  `grep -v "<"` meant to drop `<see cref>` lines dropped every `<= Tolerances.FlowZero` site (8 of 12,
  reported `certain`); `grep ": ISolver"` cannot see a test double that inherits the interface through
  a base class (3 of 4); a method-counting regex matched a lambda in a field initialiser (23 for 22).

## Reading the numbers honestly

- **Tool-result tokens is the headline.** That is what actually lands in the context window.
- **Total agent tokens is the production number.** It includes each agent's bootstrap and reasoning,
  roughly constant across routes and invisible to the meter. 1.4–1.7× is what you would feel; 2.3–3.0×
  is the tool-traffic ratio. Both are real; they answer different questions.
- **Argument tokens are model output**, which is priced higher than input on every major provider. The
  raw route generated 3.5–7.7× more of it — grep patterns, `sed` ranges, repeated paths. This is the
  axis usually left out of "grep is free" reasoning.
- **Specimen B is a small repo, which favours the raw route.** Grep's cost scales with tree size while
  per-call MCP overhead is close to fixed, so both reports argue a ~3× saving there is nearer a floor
  than a ceiling. **That is a hypothesis, not a measurement** — a much larger solution also increases
  index size, search ambiguity and semantic-model load, none of which these runs isolate.

## Where the raw route won

Published because a benchmark that never loses is not a benchmark.

### Enumerating a 13-file partial class — an outright MCP defeat

`ValueIndicator<T>` is split across **13** partial files. The MCP probe answered **5**, and rated
itself "fairly sure". It reached for `search_index` twice and reported the files that surfaced in the
ranked hits — but a ranked symbol search is not an enumeration of declaration sites, and treating it as
one silently dropped 8 files.

One `grep "class ValueIndicator<"` got all 13 in a single call. Text search is an excellent fit
whenever the thing being enumerated is a literal, uniform token appearing once per target — which is
exactly what a partial class declaration is.

This was a **route-selection failure, not a tool failure**: `get_symbol`'s `declarationSites` returns
all 13 exactly, which is how ground truth was established. The cheap-route table in `dotnet-read` now
carries this case explicitly. The raw route still won the row.

### Partial classes generally, against a grep-first route

The always-loaded rule once said text search "returns one fragment of a partial class with no signal
the rest exists". That is true of `Read`/`cat` and **was not true** of the raw probe: opening with
`grep "class SymbolStore"` surfaced both partial declarations at once, and the trap never fired on
either question that targeted it. The wording has since been narrowed.

### Exact, distinctive names, on cost

`grep -n "class EvolutionarySolver"` plus one bounded `Read` is genuinely competitive with
`search_index` + `get_symbol`. On this question text search gives the right answer, cheaply.

### Counting text occurrences that are *not* references

A raw-text question, and no MCP tool answers it. Worse, `get_references` has a field —
`excludedTextMatches` — that *reads* like it does and does not: it counts only the comment/string
matches the reference engine itself saw and dropped, excluding the declaration and same-prefix
identifiers. A competent agent misread it in exactly that direction on first contact. That is a
naming/documentation finding worth fixing regardless of the benchmark.

### Small, well-located files

Once you know the path, `Read` on a 17-line interface is one cheap call.

## What decided the runs anyway

The *shape* of the errors, not the count.

Asked for every call site of `PythonService.Run()`, the raw probe reported **26 against a true 27**.
That near-miss hides two independent failures that partially cancelled:

- **A missed hit.** `PythonService.cs:167` contains `Run();` — an unqualified internal call from
  `PythonService.LoadModule`. The probe searched for `PythonService\.Run()`, which cannot match a call
  that does not name its own class. Roslyn resolves it; text search structurally cannot.
- **A false hit.** The probe counted `NN_DataConstructor.cs:1303` and explicitly claimed it had used a
  "grep with filter to exclude comments". That line does read `PythonService.Run();` — but a block
  comment opens 9 lines above and never closes. **The entire method is commented out.** Confirmed
  independently: the index reports `termsWithNoHits: GetSpectrum`, so the compiler does not see that
  symbol at all.

The two errors cancelled into a plausible-looking 26, and the probe rated itself **"certain"**. A
reviewer sanity-checking the number would see "about right" and move on.

The same asymmetry appears on dead-code questions. Both routes concluded `Gene.Clone()` was unreferenced
and safe to delete, and both were right — but the MCP route read `totalItems: 0` from the compiler's
model, while the raw route inferred it from a grep returning no matches. The raw probe was honest about
this, dropping to "fairly sure" and naming reflection as an unresolved gap. **The confidence gap is the
finding**: text search cannot distinguish "no references" from "no references I could see", and on a
delete decision that distinction is the whole question.

A route that fails loudly is cheaper to work with than one that fails at 26-vs-27.

## What these runs do not measure

- **`rename_symbol`**, the one tool-made edit, versus a hand-written rename. All probes are
  read-only.
- **Multi-project ripple.** Every question in Specimen A landed in one of its three projects.
- **Repos larger than ~500 types**, or monorepos.
- **Comment density**, which is under-sampled. The false hit above came from a large block-commented-out
  method; a repo with little disabled code would not produce it.
- **Wall-clock on a fast filesystem.** Both runs used WSL against `/mnt/c`, where IO is slow. That shows
  in wall time (257s vs 94s on 2026-08-13) but not in tokens, and tokens are what these reports measure.

## Run it on your own repo

```text
/dotnet-performance
```

It builds the question matrix from *your* solution, runs all three probes in sequence at the default
guard state — nothing is denied, and no permission prompt is needed in auto mode — and writes a report
to `.claude/dotnet-toolkit/perf/` with these same tables, including each probe's whole-file read count,
which questions a raw-tool route won, and which size axes the result does or does not transfer to.

What it does to your machine:

- **It suspends the guard hooks for the raw probe, and forces them to hard-deny for the MCP probe**,
  each for its own bounded window — the raw probe cannot reach a `.cs` file at their default state, and
  the forced block backstops the MCP probe's one narrow `Read` allowance. Both re-arm on a timer, and
  the skill restores them right after each probe returns, before moving to the next. The report records
  each window's start, end and restoration.
- **It never edits `.cs`.** All three probes are read-only.
- **It runs three subagents, sequentially** (guard state is one switch per repo, so they cannot run in
  parallel), which costs real tokens. The comparison is the product.

A small repo with no file over 400 lines will show a much narrower margin than the specimens above. The
report will say so rather than hide it.
