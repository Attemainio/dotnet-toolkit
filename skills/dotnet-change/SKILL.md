---
name: dotnet-change
description: Use when changing C# code in a .NET repo - editing a method or type, changing a signature, renaming, or fixing a compile error. Validates the edit against an in-memory compilation before it touches disk, reports honestly whether that validation was sufficient for the kind of change made, and records why the change was made.
---

# Changing C# code safely

This repo has the dotnet-toolkit MCP server. For C# edits, go through
`mcp__plugin_dotnet-toolkit_dotnet__validate_patch` instead of editing the file and hoping.
It applies your edit to a **forked in-memory solution**, compiles it, and writes to disk
only if the result is genuinely sufficient. Disk is never touched otherwise.

Use `Edit`/`Write` directly only for non-C# files (csproj, json, md).

## Before the first C# edit of a session: read the standards

The canonical coding standards live in `.claude/rules/` (in a consuming repo:
`${CLAUDE_PLUGIN_ROOT}/.claude/rules/`, or the repo's own copies if `dotnet-toolkit-init` installed
them; a repo-local override at `.claude/dotnet-toolkit/<name>.md` wins per file). They are **not**
auto-loaded — this step is what loads them. Per `csharp-standards.md`'s index, read before editing:

- **always**: `naming.md`, `styling.md`, `best-practices.md`, `xml-documentation.md`;
- **when the change touches** project/namespace boundaries or a new abstraction: `architecture.md`;
  a public/internal signature: `api-design.md`; exceptions, retries, or timeouts: `error-handling.md`;
  `IDisposable`/streams/pooling: `resource-management.md`;
  endpoints/auth/SQL/config/logging/crypto: `security.md`;
  hot paths, buffers, SIMD, `unsafe`: `performance.md`; awaits/locks/tasks/shared state:
  `concurrency.md`; tests: `testing.md`;
- **skim once per session**: `antipatterns.md` — the shared catalog the other files cite by name, so a
  rule that says "avoid the X antipattern" resolves to something you've actually read.

Once per session is enough — hold them; don't re-read per edit. `dotnet-code-review` validates against
the same files afterward, but writing to the standard beats fixing to it.

## The loop

1. **Hold current content.** Fetch what you are about to change with `get_symbol`
   (`include: "all"`) and keep its `contentVersion`.
2. **Know the blast radius.** If you are changing a signature, accessibility, base type or
   interface, call `get_references` first — dependent-compile failures across
   implementations are otherwise guaranteed.
3. **Check for a summary.** If the symbol you're changing has no `<summary>`
   (`xmlDoc.summary` absent from step 1's fetch, or `search_index`'s `hasSummary` was absent) — add
   one in the *same* patch, following `.claude/rules/xml-documentation.md`'s tag rules: purpose only, 1–2
   sentences, never restate the method name, implementation/performance detail goes in
   `<remarks>` not `<summary>`. This isn't optional cleanup — an edit that leaves a touched public
   symbol undocumented is not a finished edit.
4. **Submit one patch** covering the symbol *and* every call site you already know needs
   updating, with `applyOnSuccess: true` set from the start (see below — do not dry-run first).
5. **Read the verdict** (below). Fix and resubmit, or you're done.

**Do not call `validate_patch` twice — once with `applyOnSuccess: false`, then again with
`applyOnSuccess: true` and the identical `baseVersions`/`edits` — when you already intend to make
the change.** The validation ladder (fork → compile → escalate) runs byte-for-byte identically
either way; `applyOnSuccess` only gates whether a *sufficient, successful* result is written to
disk. A dry run then an apply re-runs the same in-memory compile twice and resends the same payload
twice, for zero additional information — `applyOnSuccess: true` already reports the full verdict in
one call, and writes nothing if the result isn't sufficient. Only dry-run (`applyOnSuccess: false`)
when you are genuinely undecided whether to make the change at all and want the blast radius before
committing to it — that's the rare case, not the default path.

## Required fields

- **`baseVersions`** — a map of `symbolId → contentVersion` for every symbol you are
  changing, using the versions you hold. This is what proves your patch was built against
  current content. A mismatch returns `error: "stale_base"` with the current versions;
  refetch those symbols, rebuild the edit, resubmit.

  **A body edit needs a version that carries the body layer.** `get_symbol` narrows its
  `contentVersion` to the layers it actually served, so the *default* include leases the declaration
  only (`decl`, plus `refs`) — and a patch that rewrites a body against it is rejected with
  `error: "unleased_body"`, because the body it overwrites was never checked for a concurrent edit.
  Step 1's `include: "all"` is what avoids this; `source`, `bodyOutline` or `mechanicalFacts` in the
  include list does too. The rejection keeps your text as a draft and lists the current versions, so the
  fix is one amend: resend the `draftId` with the body-carrying version in `baseVersions` and an empty
  `edits` array.

  A `symbolId` not starting with `sym_` — `symidx_` (from `get_symbol`'s syntax-tier fallback,
  `limitedBy: "index_only"`) or `symfb_` (from `SymbolKey.IdOf`'s own no-doc-comment-id fallback) —
  is provisional and never equal to the live semantic tier's id for the same symbol. `validate_patch`
  rejects it outright with `error: "stale_index_only_id"` rather than letting it cascade into a
  confusing `stale_base` mismatch across every symbol in the file. Fix: call `get_symbol` again once
  the workspace has finished loading (check `workspace_status`) and rebuild `baseVersions` from that
  response's `sym_`-prefixed id.

  `baseVersions` covers the symbols you are changing, **not the rest of the file**. An apply
  writes the whole document text back, so a file that moved on disk since the workspace read
  it is refused outright with `error: "stale_workspace"` — otherwise the patch would revert
  every other change in that file while reporting success. Recover with `reload_workspace`
  (`scope: "all"` also rebuilds the SQLite symbol index, so `search_index`/`get_references`
  reflect the new state too, not just the live workspace), then re-read the symbol (its line
  spans will have moved) and rebuild the patch. Expect this after a `git checkout`, a `git pull`,
  a rebase, or any `.cs` edit made with `Edit`.
- **`edits`** — an array of `{ file, startLine, endLine, newText }`, not a single edit. Like
  `search_index`'s multi-term query or `get_symbol`'s `symbols` batch, it takes as many hunks
  as the task actually needs in one call, so a known multi-edit task never gets split into one
  `validate_patch` call per edit. That's different from a task that genuinely needs only one
  hunk — call it once for a single-line addition or removal, because there's only one hunk to
  send, not because "once per hunk" is the rule. The actual rule: know the full set of edits
  before calling, and send that whole set in one call — never discover edit 2 only after
  submitting edit 1 as its own call, when both were already known upfront.

  **Split into multiple tight hunks instead of one wide span.** `newText` replaces the whole
  span verbatim, so a single edit covering "first changed line" through "last changed line"
  resends every genuinely unchanged line in between too — pure waste when an untouched method
  or block sits between two real changes. Draw the box around only what actually changed: if
  lines 20-25 and lines 60-65 changed but 26-59 didn't, submit two edits (20-25 and 60-65) in
  the same `edits` array, not one edit spanning 20-65. This is still one `validate_patch` call
  either way — the array is what makes several tight hunks cost the same round trip as one
  wide one. Don't overcorrect into single-line micro-hunks where changes genuinely cluster
  (e.g. a rewritten method body) — split at real unchanged-content boundaries, not for its
  own sake.

  The line span comes straight from `declarationSites` in the `get_symbol` response.
- **`intent`** — REQUIRED when `applyOnSuccess: true`. One sentence of *why*, in user
  terms ("Add cancellation support to training"), not *what* (the diff already says that).
  Reuse the task's intent across its patches. Omitting it is rejected before validation
  even runs.
- **`taskId`** — optional, and the one argument that outlives the call. It attributes the
  validation to a caller you name in telemetry, **and an applied patch stamps it onto the
  development-log entry as that entry's task id**, so several patches sharing one `taskId`
  read back as one piece of work rather than unrelated edits. Omit it and both fall back to
  the ambient session id, which is what every entry carried before. Worth passing when
  several agents are editing against the same server, or when you want one task's patches
  grouped in `get_retrieval_metrics(groupBy: "task")`.

## Reading the verdict — the only definition of "done"

The response carries `completedLevel`, `requiredLevel`, `isSufficient`, `succeeded` and
`applied`. A change is DONE only when:

```
isSufficient: true  AND  succeeded: true  AND  (applied: true OR you deliberately chose not to apply)
```

No other combination is done. In particular:

- **`succeeded: true` with `isSufficient: false`** is a *partial* green. The code is
  healthy only up to `completedLevel`; the change needs more. Do what `nextAction` says —
  usually resubmit with `requestedLevel` raised to `requiredLevel`. **Never report this as
  complete.**
- **`applied: false`** means the file on disk is unchanged, whatever else the response says.

Report status with the fields, not a vibe: *"compiles at project level; dependent compile
still required because the public signature changed"* — never just "it builds".

Signature, accessibility, inheritance, interface, attribute, generic-constraint and public
nullability changes must show `requiredLevel` of at least `dependent_compile`. If you see
less, escalate explicitly with `requestedLevel`.

**An empty `detectedChanges` is not a free pass.** The required level is the maximum over the
symbols the classifier attributed a change to, so an edit it attributes to *no* symbol — a `using`
directive, a file-scoped namespace, an assembly attribute, or a comment-only change (fingerprints
are trivia-blind) — would otherwise floor at `parse`. Parse cannot tell a harmless reformat from
`using Nope.Missing;`, which is syntactically perfect and fails to bind. So a patch that changed
text but no symbol is floored at `project_compile` instead. If you see `detectedChanges: []` with
`requiredLevel: parse`, that is a bug, not a cheap green.

## When validation fails

`diagnostics.rootCauses` is already distilled — one entry per root cause, not one per
compiler error. For each:

- **`suggestedInspection` is your fetch plan.** Fetch those symbols (their `symbolId`s work
  directly as `get_symbol` targets) before revising. Don't re-guess from the summary.
- **`suppressedDiagnostics`** counts downstream errors that will vanish once the root cause
  is fixed. Do not chase them.
- **`fixHint`** says what the fix shape is.

- **`locations`** gives up to three `{file, line, column, excerpt}` entries saying exactly
  where the error landed — in the coordinate space of the text you *proposed*, not the file on
  disk. This is what you aim the correction at.

Then **batch**: fetch everything suggested, and submit ONE revised patch covering all of it.
Never resubmit an identical patch, and don't fix root causes one call at a time.

## Amend the draft — do not resend the patch

**Every unapplied response also carries `draft: {draftId, expiresAt, files}`.** The server kept
the exact text your patch proposed. Send `draftId` back with **only the lines you are
correcting**:

```
validate_patch(draftId: "draft_01KYH...",
  edits: [{file: "...", startLine: 43, endLine: 43, newText: "        return 0;"}],
  applyOnSuccess: true, intent: <the same intent as before>)
```

- `baseVersions` is **inherited** from the draft, and anything you send alongside a `draftId` is
  **merged into** it rather than replacing it. That is the fix for `unheld_symbol` (below).
- The edits' line spans address the **draft's** text, which is the same coordinate space
  `locations` reports in. `files` lists which files are in that space; a diagnostic in any
  other file carries ordinary on-disk line numbers.
- `intent` is still required to apply. Amending does not weaken the log contract.

Reach for this whenever the correction is small relative to the patch — a missing brace in a
200-line `newText` costs one line, not 200. **It also replaces the resubmit in the partial-green
case**: for `succeeded: true, isSufficient: false`, send the `draftId` with `requestedLevel`
raised and an **empty** `edits` array, since the text has not changed. (An empty `edits` array is
legal only with a `draftId`; without one it is `error: "no_edits"`.)

Each amend that still fails mints a new `draftId` — drafts are immutable. They live 15 minutes
and only the 8 most recent are kept; `error: "unknown_draft"` means yours aged out, so refetch
with `get_symbol` and send a full patch. `error: "draft_stale"` means the file moved in the
workspace underneath the draft, so its line numbers no longer mean anything — rebuild from a
fresh `get_symbol`.

## `unheld_symbol` — a missing version, not a stale patch

`baseVersions` must cover every symbol the **classifier** attributes a change to, which is not always
the set you edited. The usual surprise: **adding a member anchors the change to its containing type**,
so inserting a method into `Foo` requires `Foo`'s version, not just the neighbouring member's.

When an entry is missing you get `error: "unheld_symbol"` listing the `{symbolId, currentVersion}`
you're short — **and a draft**, because nothing is wrong with your text. Fix it without resending a
line:

```
validate_patch(draftId: "draft_01KYH...",
  baseVersions: {"sym_thecontainingtype": "decl:..."},   // merged into the draft's own map
  edits: [],                                             // the text was never the problem
  applyOnSuccess: true, intent: <same intent>)
```

Contrast with `stale_base`, which means a version you sent **disagrees** with the current one. There
your text was built on content that has since moved, so it gets no draft and must be rebuilt.

Rebuild from scratch, not by amending, after `stale_base`, `invalid_edit`, or `stale_workspace` —
those responses carry no draft, because the patch itself is built on something that moved.

## Editing the same symbol again — don't refetch it

An applied response gives each changed symbol both halves the next patch needs:

- **`newVersion`** — the `baseVersions` entry for the next edit to that symbol.
- **`declarationSites`** — `[{file, startLine, endLine}]`, where the declaration now sits, in the
  same shape and with the same bounds `get_symbol` returns.

So a second edit to a symbol you just changed goes straight into another `validate_patch`. Calling
`get_symbol` again only to recover the shifted line span is a round trip the response already paid
for. (Refetch normally when you need *content* you no longer hold, or for a symbol this patch did
not change.)

## What gets recorded

An applied patch appends one development-log entry: your `intent`, the symbols changed,
their old and new versions, and the API impact of each. That is why `intent` is required —
the diff records *what* changed; only you can record *why*.

Read it back with `search_log` — before proposing a design, to find out whether the
approach was already tried and rejected, and why.
