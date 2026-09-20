# Why dotnet-toolkit is navigation-only

dotnet-toolkit 1.x shipped a write path of its own: a `validate_patch` tool that took line-anchored
edits against a symbol the caller had "leased", compiled them through a ladder of checks, ran the
analyzers, wrote the files and appended to a development log — and two `PreToolUse` guards that
hard-denied `Edit`/`Write` on any compiled `.cs` file so that every change went through it. `get_symbol`
returned source text, because a patch needed current line numbers to anchor against.

2.0.0 removed all of it. The toolkit now **locates**: it answers where a symbol is, who references it,
what implements it, and how far a change ripples — and hands the text back to `Read`, the edit to
`Edit`/`Write`, and the verdict to `dotnet build`. This page is the record of why.

## What the write path was measured to do

The decision came from a defect sweep run on a real consuming repo (FluidScript, 2026-09-19), not from
a design review: roughly 420 `validate_patch` calls and 360 `get_symbol` calls over four tiers of
increasingly hard edits, with every failure classified against the server's own source.

- **Fewer than half of the calls did what they were sent to do.** 127 of 382 (33 %) carried no edit at
  all: they were refused pre-flight — `unheld_symbol`, `unleased_body`, `stale_base` — and each refusal
  handed back the version numbers the caller was then asked to resend. Those refusals were not bugs.
  They were the lease protocol working exactly as designed.
- **Line anchoring was a failure class no fix could remove.** Eight failed rounds in tiers 3–4 alone
  were a range one line short of the closing brace or one past it. `declarationSites.endLine` never
  stated whether it *was* the brace. An edit anchored on a line number is an edit anchored on
  something every previous edit moves; `Edit` is anchored on the text itself and has no such class.
- **The success response was ~200 lines** of `detectedChanges[]`, `notAssessed[]`, per-level ladder
  results and generic `nextAction`/`fixHint` text — by design, on every successful call.
- **`get_symbol` had become a line-numbered file reader.** Most of the 693 K characters it returned in
  the sweep were source fetched so a patch could be positioned. The rendering subsystem, its `source`
  grammar (`:code`, `@lines`, `-modifiers`), and the `ResponseGuard` that capped it all existed to
  serve that demand.
- **The symbol store did not follow edits.** After a plain `Edit`, the live Roslyn solution picked up
  the new text within two seconds, but `search_index`, every `sym_` id, `get_references` edges and the
  hierarchies stayed frozen at the last full rebuild — which only `validate_patch`'s apply step,
  `reload_workspace` or a restart triggered. Several "cache corruption" recoveries in the sweep were
  this. The write path had been masking a gap in the read path.

Two genuine defects were also found (a trailing `\n` in `newText` inserted a blank line; edits added
to an existing draft mis-anchored). Both were in code the cut deletes.

## The principles the decision rests on

1. **A tool that duplicates a harness capability has to beat it, not match it.** Claude Code and Codex
   already ship a string-anchored `Edit`. A patch tool wins only if it removes a failure class `Edit`
   has; the measured result was that it *added* three (line drift, lease refusal, stale base) and
   removed none.
2. **The compiler is the gate, and it already exists.** `dotnet build` reports every dependent break in
   every project, uncapped, with the repo's own analyzers and `TreatWarningsAsErrors`. The compile
   ladder re-implemented a subset of that per call, at 6–7 s per call. One build after a burst of edits
   costs less and says more.
3. **Never hand back text the caller can read for the same price.** Once the edit is string-anchored,
   nothing needs a line-numbered rendering of a symbol. `get_symbol` answers *where*
   (`declarationSites`: `file`, `signatureLine`, `startLine`, `endLine`, one row per part of a partial),
   and a ranged `Read` of that span is the text. That pair costs less than the rendered source did and
   misses no part of a partial.
4. **A read-only locator must never block a read.** With no write path to protect, the read guards
   have nothing to enforce. They became advisory in 1.0.1 and stayed advisory in 2.0.0: a whole-file
   `Read` or `cat` of a compiled `.cs` file draws a note stating its cost and the cheaper pair above;
   a ranged read draws nothing. A wrong advisory is a bug report; a denial is a wall.
5. **The index has to follow every edit, whoever made it.** This was the prerequisite, not the
   follow-up. A `PostToolUse` hook now sends every file an `Edit`, `Write`, `NotebookEdit`,
   `apply_patch` or `Bash` call wrote to the running server, which re-indexes just those documents;
   the mtime sweep catches what the hook does not see. `search_index` sees the edit before the next
   call.

## What was kept, and what was deferred

- **`rename_symbol` stays**, stripped to "Roslyn rename → write files → refresh the index". A
  solution-wide semantic rename reaches interface, virtual and delegate dispatch sites that share no
  text with the declaration — it is the one edit a string replacement cannot make safely. It needs
  none of the validation stack once `dotnet build` is the gate; its result is not compiled, and the
  response says so.
- **A `get_diagnostics` tool is deferred, not rejected.** Dependent breaks reported from the in-memory
  compilation before a build were the one thing the ladder was genuinely valued for. It would be one
  call, not a protocol. It waits on metrics from the navigation-only surface.
- **A post-write `dotnet build` hook was rejected.** Firing on every `Edit` costs ~6.6 s each — 33
  edits is three and a half minutes of hook time for information one build at the end provides.
- **The development log (`search_log`) went with the write path.** Nothing writes it any more, and a
  read-only log whose last entry is the day of the cut would be answered with authority forever.

Net: roughly a fifth of `src/` and a quarter of the tests were removed (`PatchTools`, `Validation/`,
source rendering, `ResponseGuard`, `SourceQuery`, `ContentVersion`, `ReadAdvice`, two guards,
`Devlog/`, patch telemetry). One capability was added: document-scoped index refresh on file change.

## What it looks like from a session

Before: `get_symbol(include: "source")` → `validate_patch(draft, lease, line range)` → refusal or
200-line success → often `reload_workspace`. After: `search_index` or `get_symbol` → `Read` of the
returned span → `Edit` anchored on that text → `dotnet build` once. The plugin's tools are all
read-only except `rename_symbol`, `dotnet-init` adds only read-only tools to a repo's allowlist, and
nothing the plugin does needs a permission prompt in auto mode — including the benchmark, which used to
switch guard state and now reads a per-agent whole-file-read count from the meter instead.

The first measurements on the navigation-only surface are in [benchmarks.md](../benchmarks.md)
(FluidScript and PandaAI, 2026-09-20): with both tool families available, a session reached for the MCP
route on every question without a nudge, injected 1.5–1.8× less context than the raw route, and was
right on every question the raw route got wrong.

The full decision record, with the per-finding source references, is the plan this was executed from:
`.claude/dotnet-toolkit/plans/2026-09-19-navigation-only-cut.md`.
