# Beating the raw route in every category — analysis of the 2026-08-11 paired runs

Sources: `perf/2026-08-11-190159-dotnet-toolkit.md` (7 questions, MCP swept) and
`PandaAI/.claude/dotnet-toolkit/perf/2026-08-11-191745-PandaAI.md` (8 questions, MCP 2 / raw 1 / 1
contested / 4 wash).

Every claim below marked **[verified]** was checked against the implementation in this session, not
taken from the reports. Claims marked **[unverified]** are the reports' own inferences that did not
reproduce here and need a diagnostic before anyone acts on them.

---

## 1. The reversal is not about repo size

The obvious reading — dotnet-toolkit is small so MCP swept, PandaAI is bigger so it got harder — is
backwards. PandaAI is *twice* the size (290 files / 505 types vs 148 / 239); size favours structured
retrieval, and indeed PandaAI's Q3/Q4 (buried method, 6-file partial) were MCP's most lopsided wins.

The actual variable is **dispatch density**. Measured in this repo just now **[verified]**:

- `search_index(kinds:"method", modifiers:"override")` over a broad query returns **2 overrides in
  the entire solution** (`ContentVersion.ToString()`, a test's `MutableClock.GetUtcNow()`).
- The dotnet-toolkit run itself reports one usable interface with implementers (`IKnowledgeStore`),
  and no dead private symbols anywhere.

Interface dispatch, virtual dispatch and multi-implementer hierarchies are precisely the things
Roslyn can see and grep cannot — and this repo has almost none of them. Both defects PandaAI found
(§2.1, §2.2) live in exactly those code paths, which is why they could not fire here.

> **The 7/0 sweep on this repo is evidence of a weak specimen, not a strong tool.** A benchmark that
> cannot exercise its own differentiators will report a clean sweep no matter what state they are in.

---

## 2. Verified defects — the reasons raw ever wins

### 2.1 `get_type_hierarchy` cannot answer "which of these are concrete" **[verified]**

`FlowTools.cs:585-604`. Derived items are emitted as `{symbolId, displayString, kind}`. `kind` comes
from `SymbolKey.KindOf` — Type / Interface / Record. **No modifier is carried at all**, so `abstract`
is invisible, and `FindImplementationsAsync` happily returns abstract intermediates
(`IndicatorBase<T>`, `BitMaskIndicator<T>`) alongside concrete leaves.

Raw won PandaAI Q6 by pure accident of format: `grep` for `class.*IndicatorBase` puts the declaration
line in the hit, and `abstract` is *in* that line. Raw wasn't smarter; it just couldn't avoid seeing
the modifier.

**Fix**: emit `abstract: true` / `sealed: true` on a derived item, present only when true — roughly
two tokens per affected row, zero on a hierarchy of concrete types. While there, tag interface
implementers `direct` vs inherited the way `interfaces[]` already is. This converts a raw win into an
MCP win with no added call.

### 2.2 `Callers` throws away Roslyn's own direct/indirect discriminator **[verified]**

`ContextTools.cs:2049`:

```csharp
foreach (var caller in await SymbolFinder.FindCallersAsync(sym, solution))
```

`SymbolCallerInfo` carries **`IsDirect`** — Roslyn's own flag for "this was a direct call to the
symbol" versus "this was found by cascading through a base or interface declaration". The loop reads
`CallingSymbol` and `Locations` and drops `IsDirect` on the floor. Every item is then stamped with
one `dispatchKind` computed from the *target*, so the response cannot distinguish a call that
provably lands on this symbol from one that merely could.

The tool's own `[Description]` (`ContextTools.cs:547`) promises **"Returns every real call site, no
false positives."** That is an unconditional guarantee the implementation does not make once
cascading is in play, and it is exactly the sentence that made PandaAI's probe treat "94" as ground
truth.

**Fix**: keep the flag. Emit `indirect: true` per item and split the header into `directItems` /
`indirectItems`. Then a large virtual-dispatch count is *self-describing* instead of misleading, and
the description can be narrowed to what the tool actually guarantees.

### 2.3 Resolution is invisible, so mis-binding is silent **[verified]**

`get_references` returns `targetSymbolId` — an opaque `sym_<hash>`. Nothing in the response says
*what it resolved to*. Suffix matching means `"MoveNext"` or `"EvolutionarySolver.MoveNext"` can bind
to a symbol the caller did not mean, and the answer comes back looking authoritative.

**Fix**: add `targetDisplayString` plus the resolved declaration's `file:line` to the envelope. ~10
tokens, and every mis-resolution becomes obvious on sight. This is the cheapest correctness
insurance on the whole surface, and it is my leading hypothesis for §3.

### 2.4 Two line conventions, one field name **[verified, low severity]**

`search_index.line` = signature line, doc comment excluded. `get_symbol.declarationSites.startLine` =
doc-comment-*inclusive*, because it is an edit span. Both are correct and both are needed. But agents
read `declarationSites.startLine` as "where the class is" and report it off by the length of the
doc block — this happened in PandaAI Q1, PandaAI Q4, and dotnet-toolkit Q1.

**Fix**: add `signatureLine` alongside `startLine`/`endLine` in `declarationSites`. One number,
emitted once per site, removes a documented-but-repeatedly-tripped-over footgun.

### 2.5 The Bash guard fires on other repositories **[verified, found in passing]**

A `grep` scoped to `/mnt/c/Users/atte9/source/repos/PandaAI` was blocked by `guard-cs-bash-read`,
with a message asserting the files are "compiled by
`src/DotnetToolkit.McpServer/DotnetToolkit.McpServer.csproj`". They are not — different repository.
The guard does not appear to test whether the target path is under the workspace root. Any session
that touches a sibling repo hits this, and the suggested remedy (use the MCP tools) is wrong there,
because the MCP server is not pointed at that solution.

---

## 3. The PandaAI Q5 root cause is probably wrong **[unverified — do not act on it yet]**

The PandaAI report attributes 94 bogus callers to `get_references` treating "any `foreach` over any
`IEnumerable<T>` as a possible caller of any override sharing the name". That mechanism did not
reproduce:

`get_references` on `ContentVersion.ToString()` — an `override` of `object.ToString()`, the most
cascade-prone shape that exists — returns **13 callers, all genuinely on a `ContentVersion`-typed
receiver**, with no cascade to `object.ToString()` and none of the solution's other `.ToString()`
calls **[verified]**.

So Roslyn is not cascading indiscriminately here. Two likelier explanations, in order:

1. **Mis-resolution (§2.3)** — the probe's symbol string bound to a BCL or unrelated `MoveNext`, and
   nothing in the response revealed it.
2. **A source-base override chain** — if `EvolutionarySolver.MoveNext()` overrides a *source*
   `SolverBase.MoveNext()`, cascading to the base is real and §2.2's missing `IsDirect` is exactly
   what would make it unreadable.

**Diagnostic, before any code change**: in PandaAI, call `get_references` on the `sym_...` id taken
from `search_index` (never the name string), and compare against `get_symbol` on
`EvolutionarySolver.MoveNext` to see what it overrides. If the count collapses, it is §2.3. If it
stays at 94 with the sites now attributable to a base declaration, it is §2.2. Either way the fix is
already on this list — but which one matters.

The report's Q5 finding is still directionally right and worth keeping: **a large `dispatchKind:
virtual` count should not be trusted at face value today.** Only the stated mechanism is suspect.

---

## 4. Cost — the ties are where "raw wins" perceptually, and all three collapse

MCP never actually lost on cost. It *tied*, and a tie against grep reads as a failure for a tool that
costs a server. All three ties are one avoidable round trip:

| Row | What happened | One-call route |
|---|---|---|
| PandaAI Q7 (dead code) | MCP 3 calls, raw 1 | `get_symbol(include:"referenceCounts")` — **already exists**, one call, and unlike grep it is dispatch-aware |
| PandaAI Q2 (fuzzy find) | MCP 2 calls (search → get_symbol) | `search_index(summary:"full")` — **already exists**, location + doc in the search response |
| PandaAI Q1, dotnet-toolkit Q1 | 1-vs-1 tie | already a tie; §2.4 turns it into a precision win |

Two of the three are **routing failures, not tool gaps** — the cheap route shipped and the agent
didn't take it. The cheap-route table in `skills/dotnet-read/SKILL.md` has no row for *"is this used
at all"* and no row for *"I only need the location and the summary"*. Add both; that table exists
precisely because a finding buried in a manual never gets read in time.

**One genuine feature gap**: `search_index` has no reference-count column. An optional
`refs: "counts"` (same opt-in shape as `summary`) would make *"find X and tell me whether anything
uses it"* a true single call. Raw needs a minimum of two greps for that, and gets the wrong answer on
any dispatch. That is a category where MCP could go from tie to strict dominance.

---

## 5. Measurement — the yardstick is not the same on both sides

### 5.1 The two headline numbers are not comparable **[verified]**

`TelemetryRecorder.cs:28`:

```csharp
public static int EstimateTokens(string? serialized) =>
    string.IsNullOrEmpty(serialized) ? 0 : (serialized.Length + 3) / 4;
```

Every `get_retrieval_metrics` figure in both reports is `chars / 4`, self-documented as
"approximate ... out of scope for MVP". Every `subagent_tokens` figure is an exact count from the
harness. The reports present them side by side.

chars/4 is calibrated for English prose. TOON is dense punctuation plus CamelCase identifiers, which
tokenize *worse* — realistically 2.5–3.5 chars/token. **The current estimator most likely flatters
the MCP side** on the one metric where MCP has an exclusive number.

### 5.2 On tiktoken

It would be an improvement over chars/4, with one caveat worth stating plainly: **tiktoken is
OpenAI's BPE, not Claude's.** It gives a consistent proxy, not a true count. If you go this way, use
`Microsoft.ML.Tokenizers` (maintained, ships `o200k_base`) rather than a third-party port — but
recognise you are buying *consistency*, not accuracy.

Ranked by value for effort:

1. **Calibrate the divisor.** Run a corpus of real responses through Anthropic's
   `/v1/messages/count_tokens` once, offline, and derive a ratio per response family (TOON, compact,
   json, C# source). Keep the cheap chars-based estimator with a corrected constant. No runtime
   dependency, no network in the hot path, and it fixes the actual bias.
2. **Stop leading with the estimate.** `subagent_tokens` is exact and already covers both routes. The
   estimate's job is *relative* comparison between MCP calls — say so in the report rather than
   printing it next to an exact number without qualification.
3. **Then** a real BPE, if per-call precision turns out to matter.

### 5.3 The raw route can be metered — and this is the highest-leverage change here

The reports repeatedly say Grep/Read/Bash "aren't meterable". They are.

A `PostToolUse` hook receives `tool_response`. `HookPayload.TryParse` (`HookPayload.cs:39-79`)
already parses hook JSON, but reads only `tool_name`, `tool_input.file_path`, `tool_input.command`
and `session_id` **[verified]**. The plugin already ships five hooks as subcommands of the same
binary, and already owns the telemetry store.

So: add `tool_response` to `HookPayload`, add a `hook meter-raw-read` subcommand matched on
`Read|Grep|Glob|Bash`, and write the response size into the same store the MCP tools write to. Then
`get_retrieval_metrics(groupBy:"tool")` reports `Grep` and `Read` **next to** `search_index`,
measured by the identical estimator, isolable by the same `taskId`.

That converts the benchmark from "exact on one side, nothing on the other" into a symmetric
per-call comparison. Every architectural piece already exists; this is plumbing, not new design.

### 5.4 taskId collisions **[already found by the dotnet-toolkit run]**

`perf_mcp_<date>` collided across two same-day runs and silently merged 16 unrelated calls into the
figure. Caught that time by `groupBy:"session"`; it will not always be. Time-suffix the id in
`performance_protocol.md`, and consider having the skill generate it rather than leaving the format
to the orchestrator.

---

## 6. Methodology — three things that bias the result

**6.1 Both probes run `model: haiku`** (`agents/dotnet-perf-*-probe.md` frontmatter) **[verified]**.
That is defensible — a tool that needs a large model to be used correctly is a tool with a design
problem — but it means some findings are *agent-shaped*, not *tool-shaped*. PandaAI Q6 ("didn't check
abstract before asserting concrete") and the 3× narrated-vs-actual call gap are both classic small
model behaviour. Only tool-shaped findings are fixable in this repo. **Label each finding with which
it is**, and run one matched pair at sonnet to see which survive.

**6.2 The question matrix is self-selected toward MCP strengths.** The dotnet-toolkit report admits
this outright: every question had "a real property that favoured structured retrieval". To claim
victory *in every category*, the matrix needs a deliberate band where raw should win:

- a known single file, read once, no search;
- an exact **string literal** (not a declared symbol);
- a `.csproj` / `.json` / `.md` question;
- "what files are in this folder";
- a symbol name that appears in exactly one place with no dispatch.

Expect to lose some of these, and say so — the guard message itself already concedes that arbitrary
text search has no MCP equivalent. An honest loss on raw's home turf is far more credible than a
sweep on a matrix built to be won, and it is the only way to find out where the remaining gaps are.

**6.3 Change the primary specimen.** Per §1, run the benchmark against PandaAI (or a purpose-built
fixture with deliberate dispatch density — an interface with 4+ implementers including abstract
intermediates, an override chain, a delegate, a genuinely dead private member). Keep dotnet-toolkit
as a *small-repo* control, which is the one thing it is genuinely good for.

---

## 7. Priority

**Tier 1 — correctness, and each is small:**

1. `IsDirect` on caller items (§2.2) — the one that made a wrong answer look authoritative.
2. `abstract`/`sealed` on derived items (§2.1) — deletes raw's only clean win.
3. `targetDisplayString` on `get_references` (§2.3) — makes mis-resolution visible; also the §3
   diagnostic.
4. Narrow the "no false positives" claim in the `get_references` description (§2.2).

**Tier 2 — the ties:**

5. Two cheap-route rows: dead-code → `referenceCounts`; fuzzy find → `summary:"full"` (§4).
6. `search_index(refs:"counts")` (§4) — the one real feature gap.
7. `signatureLine` in `declarationSites` (§2.4).

**Tier 3 — the benchmark itself:**

8. Raw-route metering hook (§5.3) — highest leverage of anything here; without it every future run
   repeats "not meterable".
9. Calibrate `EstimateTokens` (§5.1–5.2).
10. Time-suffixed taskId (§5.4); raw-favourable question band (§6.2); PandaAI as primary specimen
    (§6.3).

**Blocked pending diagnostic:** the PandaAI Q5 mechanism (§3). Run the two-call diagnostic first —
the fix is already in Tier 1 either way, but which one it is determines whether §2.2 is sufficient.

**Also worth a ticket:** the sibling-repo guard false positive (§2.5).

---

## 8. The honest answer to "beat raw in every category"

Achievable on correctness, calls, tokens and latency — the Tier 1+2 list closes every measured gap,
and most of it is a field or two per response.

Not achievable on **arbitrary text search**, and it should not be attempted. Finding a string literal,
a config key, or an API name this solution does not declare is grep's job; the bash guard already
says so. The right goal is not "win every question" but "win every question about *declared C#
symbols*, and route the rest to the tool that owns it without friction." A benchmark that reports an
honest loss in that band is more useful than one that never asks.
