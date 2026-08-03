# `validate_patch` — how authored `.cs` edits reach disk

> A **pure rename** is `rename_symbol`'s job, not this tool's — it derives the call-site edits from the
> compiler's reference graph instead of asking you to write them. See `rename_symbol.md`.

Replaces: `Edit`/`Write` on a `.cs` file, followed by `dotnet build` and hoping. Runs your edit against a
**forked in-memory solution** and reports honestly whether the result compiles at the level the change
actually needs — writes to disk only when it does, and only when you ask it to.

| Arg | Meaning |
|---|---|
| `baseVersions` | Required, **except with `draftId`** (a draft carries its own, and anything you send is merged into it). `{symbolId: contentVersion}` for every symbol you're changing, from a `get_symbol` you actually hold. A version that disagrees is `error: "stale_base"` — refetch and rebuild. A symbol with no entry at all is `error: "unheld_symbol"`, which keeps your text as a draft. A **body**-changing edit additionally needs a version that carries the `body` layer, which only an include serving `source`/`bodyOutline`/`mechanicalFacts` hands out; the declaration-only token from a default fetch is `error: "unleased_body"`. Any id not starting with `sym_` — `symidx_` (from `get_symbol`'s `index_only` fallback) or `symfb_` (`SymbolKey.IdOf`'s own no-doc-comment-id fallback) — is rejected outright as `error: "stale_index_only_id"` — neither was ever the live tier's id for that symbol; re-fetch via `get_symbol` once the workspace has finished loading. |
| `edits` | `[{file, startLine, endLine, newText}]` — the line span comes straight from `get_symbol`'s `declarationSites`. With `draftId`, the spans address the **draft's** proposed text instead, and the array may be empty. |
| `requestedLevel` | Optional floor: `parse` \| `semantic_bind` \| `project_compile` \| `dependent_compile` \| `targeted_tests` \| `solution_validate`. Raises, never lowers, the level the ladder runs to. |
| `applyOnSuccess` | Commit to disk when sufficient and successful (default `false`). Safe to send `true` from the start — nothing is written unless both hold. |
| `intent` | **Required when `applyOnSuccess: true`.** One sentence of *why*, in user terms — applying with one is what writes to the development log (this tool and `rename_symbol` are its only writers). |
| `tags` | Optional `string[]` stored alongside the development-log entry. Rarely used; `search_log` has no tag filter today, so a tag is descriptive metadata rather than a retrieval key. |
| `draftId` | Amend a previous unapplied patch instead of resubmitting it — see "Amending instead of resubmitting" below. |

The response carries `completedLevel`, `requiredLevel`, `isSufficient`, `succeeded`, `applied`. Done
means all of: `isSufficient: true`, `succeeded: true`, `applied: true` (or a deliberate choice not to
apply). `succeeded: true` with `isSufficient: false` is a **partial** green — the code compiles only up
to `completedLevel`, and `nextAction` says what to do next (usually resubmit with `requestedLevel`
raised). Never report a partial as done.

`detectedChanges` and, on failure, `diagnostics.rootCauses` are both plain arrays of objects. Each root
cause is pre-distilled — one entry per root cause, not one per compiler error — carrying
`suggestedInspection` (symbol ids to fetch before revising, a nested array of `{symbolId, displayString}`
objects), `suppressedDiagnostics` (downstream errors that vanish once the root cause is fixed, so don't
chase them), `fixHint`, and `locations` (up to three `{file, line, column, excerpt}` entries saying
exactly where the error landed). Fetch everything suggested and submit one revised patch; never resubmit
unchanged or fix causes one at a time.

## `checks` — what the run actually examined

Every response carries a `checks` block, pass or fail. It exists because `succeeded: true` with no
`diagnostics` is *silence*, and silence reads the same whether a check ran and found nothing or never
ran at all — so a clean result has to state its own scope.

```json
"checks":{
  "levels":[{"level":"parse","succeeded":true,"durationMs":4,"scope":"1 changed document(s)"},
            {"level":"project_compile","succeeded":true,"durationMs":611,"scope":"DotnetToolkit.McpServer"}],
  "analyzers":{"ran":true,"skipReason":null,"analyzerCount":8,"documentCount":1,"durationMs":900,
               "clean":false,"errorCount":0,
               "warnings":{"count":2,"truncated":0,"items":[{"id":"CA1822","message":"…","file":"…","line":16,"column":18}]},
               "suggestions":{"count":0,"truncated":0,"items":[]}},
  "notAssessed":["levels not run: targeted_tests, solution_validate",
                 "analyzers covered 1 changed document(s); analyzer findings in files this patch did not touch are not assessed"]
}
```

- **`levels`** — every rung that ran, with the `scope` it ran over (document count, or the project names
  compiled). A rung reporting `succeeded: true` without a scope would be an assurance it never earned.
- **`analyzers`** — the pass that runs the projects' referenced analyzers (`CA*`, and anything from a
  NuGet analyzer package) over the changed documents. `warnings` and `suggestions` are advisory and
  never block; their `count` is the untruncated total, `items` is capped at 15 per severity.
- **`notAssessed`** — the gaps, in plain language. Always non-empty in practice: the analyzer pass only
  looks at files the patch touched, which is stated on every run.

## `.editorconfig` decides severity, and severity decides blocking

The repo's `.editorconfig` and `TreatWarningsAsErrors` are honored, because `Diagnostic.Severity` from
the workspace is already the **effective** severity — MSBuildWorkspace builds a `SyntaxTreeOptionsProvider`
from the `.editorconfig` chain. The grading rule is exactly `dotnet build`'s:

| Effective severity | Effect |
|---|---|
| `error` — including a warning promoted by `dotnet_diagnostic.XXNNNN.severity = error` or `TreatWarningsAsErrors` | **Blocks.** `succeeded: false`, nothing applied, reported under `diagnostics`. |
| `warning`, `suggestion` | Reported under `checks.analyzers`, never blocks. |
| `none`/`silent` | Not reported at all. |

So a rule your `.editorconfig` calls an error fails the patch here for the same reason it would fail the
build — and lowering that rule's severity in `.editorconfig` is a legitimate response to the failure, not
a workaround. `nextAction` says so when the failure came from an analyzer rather than the compiler.

Analyzers run **after** the compile rungs pass, never before: analyzing code that does not bind buries
the real error under cascade findings. A run that fails a rung reports `analyzers.ran: false` with a
`skipReason`, not a clean analyzer result.

## Amending instead of resubmitting

Every response that was **not applied** also carries a `draft`:

```json
"draft":{"draftId":"draft_01KYH…","expiresAt":"2026-07-27T14:31:07+00:00",
         "files":[{"file":"src/DotnetToolkit.McpServer/Tools/ServerTools.cs","lineCount":142}]}
```

The server has kept the exact text your patch proposed. Pass that `draftId` back with **only the lines
you are correcting** rather than resending the whole patch — `baseVersions` is inherited (anything you
send alongside a `draftId` is **merged into** the draft's map, which is how `unheld_symbol` is fixed), and the edits'
line spans address the draft's proposed text, which is the same coordinate space `locations` reports in.
The `files` array is what tells you which files are in draft coordinates; a diagnostic in any other file
reports ordinary on-disk line numbers.

Two things this is for:

- **A small mistake in a large patch.** A missing brace in a 300-line `newText` costs one line to fix,
  not 300.
- **A `succeeded: true, isSufficient: false` verdict.** Resend the `draftId` with `requestedLevel` raised
  and an **empty** `edits` array — the text is unchanged, so there is no reason to transmit it again. An
  empty `edits` array is legal only with a `draftId`; without one it is `error: "no_edits"`.

Each amend that still fails mints a **new** `draftId`; drafts are immutable. They live 15 minutes and
only the 8 most recent are kept.

| Error | Meaning |
|---|---|
| `unknown_draft` | Expired, evicted, or never existed. Refetch with `get_symbol` and submit a full patch. |
| `unheld_symbol` | The patch changes a symbol no `baseVersions` entry covers — an added member anchors to its **containing type**, which is the usual cause. Nothing is wrong with the text, so a draft **is** issued: resend its `draftId` with the reported versions in `baseVersions` and an empty `edits` array. |
| `unleased_body` | The patch rewrites a **body** against a `contentVersion` that carries no `body` layer, so staleness was only ever verified for the declaration and a concurrent edit to that body would have been overwritten silently. `get_symbol` narrows its token to the layers it served, so the default fetch leases `decl` (+`refs`) only. Same fix shape as `unheld_symbol`, and it likewise keeps a draft: refetch with an include that serves the body (`all`, `source`, `bodyOutline` or `mechanicalFacts`), then resend the `draftId` with that version and an empty `edits` array. The versions the error reports already carry the layer. |
| `draft_stale` | A file moved in the workspace since the draft forked from it, so its line numbers no longer mean anything. The draft is dropped; rebuild from a fresh `get_symbol`. |

A draft is **not** issued for `stale_base`, `invalid_edit`, or `stale_workspace` — nor when the patch
applied, since there is then nothing left to correct. The distinction is whether the **text** is still
trustworthy:

- `invalid_edit` / `stale_workspace` — the fork was never built, so there is no proposed text to keep.
- `stale_base` — a version you sent **disagrees** with the current one. Your text was built on content
  that has since moved, so it must be rebuilt; making the retry cheap would only tempt you to re-apply
  reasoning that no longer holds.
- `unheld_symbol` / `unleased_body` — a version is simply **absent**, or present but narrower than the
  change needs. Nothing moved and the text is fine, so it is kept.

That last split is the useful one: a missing map entry is a metadata gap, not a stale patch, and it used
to cost a full resend to fix.

Real call and response — an intentionally broken addition, `applyOnSuccess: false`:

```
validate_patch(baseVersions: {"sym_7a9d22ff3b68f4ee": "decl:7c76e9eba9da|body:2bac28c29969"},
  edits: [{file: "src/DotnetToolkit.McpServer/Tools/ServerTools.cs", startLine: 16, endLine: 16,
           newText: "    public static string Ping() => ThisTypeDoesNotExist.Value;"}])
```

```json
{"detectedChanges":[
   {"symbolId":"sym_7a9d22ff3b68f4ee","changeKinds":["body"],
    "oldVersion":"decl:7c76e9eba9da|body:2bac28c29969","newVersion":null,
    "apiImpact":"non-breaking",
    "declarationSites":[{"file":"src/DotnetToolkit.McpServer/Tools/ServerTools.cs",
                         "startLine":14,"endLine":16}]}],
 "ladder":{"completedLevel":"semantic_bind","requiredLevel":"project_compile","isSufficient":false,
   "reason":"Validation failed at semantic_bind.",
   "nextAction":"Fetch the suggested symbols, revise the patch, and resubmit."},
 "succeeded":false,"applied":false,
 "diagnostics":{"rootCauses":[
   {"diagnostic":"CS0103",
    "summary":"CS0103: 1 occurrence(s) — The name 'ThisTypeDoesNotExist' does not exist in the current context",
    "affectedSymbolId":"sym_7a9d22ff3b68f4ee",
    "fixHint":"A name is not in scope here; check the identifier or add the missing member.",
    "locations":[{"file":"src/DotnetToolkit.McpServer/Tools/ServerTools.cs","line":16,"column":36,
                  "excerpt":"public static string Ping() => ThisTypeDoesNotExist.Value;"}],
    "suggestedInspection":[{"symbolId":"sym_7a9d22ff3b68f4ee","displayString":"string ServerTools.Ping()"}],
    "suppressedDiagnostics":0}],
   "totalRaw":1,"totalSuppressed":0},
 "draft":{"draftId":"draft_01KYHQZHXXKMF2XQE0AHS2KWNJ","expiresAt":"2026-07-27T12:29:48.2540023+00:00",
   "files":[{"file":"src/DotnetToolkit.McpServer/Tools/ServerTools.cs","lineCount":126}]}}
```

Note `declarationSites` reports 14–16 while the edit targeted only line 16: the span covers the whole
declaration including its attributes, exactly as `get_symbol` reports it.

`newVersion` is `null` here because nothing was applied — it only describes reality once the patch is
actually on disk.

Each `detectedChanges` entry also carries `declarationSites` — `[{file, startLine, endLine}]`, the same
shape and the same bounds `get_symbol` returns, describing where that declaration sits **in the text this
call produced**: the file itself once applied, otherwise the draft. Together with `newVersion` that is
everything a follow-up edit to the same symbol needs, so **editing a symbol twice in a row costs one
`validate_patch` call, not a `validate_patch` and then a `get_symbol` to recover the shifted span**. It is
`null` for a removal, which has no new declaration to point at.

The fix then costs one line, not the whole patch — `locations[0].line` says where, and `draftId` says
against what:

```
validate_patch(draftId: "draft_01KYHQZHXXKMF2XQE0AHS2KWNJ",
  edits: [{file: "src/DotnetToolkit.McpServer/Tools/ServerTools.cs", startLine: 16, endLine: 16,
           newText: "    public static string Ping() => \"pong\";"}])
```

```json
{"detectedChanges":[
   {"symbolId":"sym_7a9d22ff3b68f4ee","changeKinds":["body"],
    "oldVersion":"decl:7c76e9eba9da|body:2bac28c29969","newVersion":null,
    "apiImpact":"non-breaking",
    "declarationSites":[{"file":"src/DotnetToolkit.McpServer/Tools/ServerTools.cs",
                         "startLine":14,"endLine":16}]}],
 "ladder":{"completedLevel":"project_compile","requiredLevel":"project_compile","isSufficient":true},
 "succeeded":true,"applied":false,
 "draft":{"draftId":"draft_01KYHR09F8TVXC81ZRE493X5VX","expiresAt":"2026-07-27T12:30:12.3601156+00:00",
   "files":[{"file":"src/DotnetToolkit.McpServer/Tools/ServerTools.cs","lineCount":126}]}}
```

One line in, green out — the other 125 lines of the file and the rest of the patch were never
retransmitted. `applied` is `false` only because this run passed `applyOnSuccess: false`; add
`applyOnSuccess: true` and an `intent` to commit it. A fresh `draftId` comes back because the result
still was not applied.

See `skills/dotnet-change/SKILL.md` for the full write loop.

## Next steps

- **Failed with diagnostics** → amend through `draftId` with only the corrected lines (above). Do not rebuild the whole patch.
- **`unheld_symbol`** → merge that one entry into `baseVersions` via the same `draftId`.
- **`unleased_body`** → refetch that symbol with a body-serving include (`all`/`source`/`bodyOutline`/`mechanicalFacts`) and merge the wider version in via the same `draftId`.
- **`stale_workspace`** → `reload_workspace`, re-fetch `get_symbol` (spans move), then resubmit.
- **The change is only a rename** → `rename_symbol` — `rename_symbol.md`. Don't author the call-site edits.
- **Need the callers before changing a signature** → `get_references` — `get_references.md`
