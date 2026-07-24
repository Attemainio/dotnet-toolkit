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
- **when the change touches** endpoints/auth/SQL/config/logging/crypto: `security.md`;
  hot paths, buffers, SIMD, `unsafe`: `performance.md`; awaits/locks/tasks/shared state:
  `concurrency.md`; tests: `testing.md`.

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

## When validation fails

`diagnostics.rootCauses` is already distilled — one entry per root cause, not one per
compiler error. For each:

- **`suggestedInspection` is your fetch plan.** Fetch those symbols (their `symbolId`s work
  directly as `get_symbol` targets) before revising. Don't re-guess from the summary.
- **`suppressedDiagnostics`** counts downstream errors that will vanish once the root cause
  is fixed. Do not chase them.
- **`fixHint`** says what the fix shape is.

Then **batch**: fetch everything suggested, and submit ONE revised patch covering all of it.
Never resubmit an identical patch, and don't fix root causes one call at a time.

## What gets recorded

An applied patch appends one development-log entry: your `intent`, the symbols changed,
their old and new versions, and the API impact of each. That is why `intent` is required —
the diff records *what* changed; only you can record *why*.

Read it back with `search_log` — before proposing a design, to find out whether the
approach was already tried and rejected, and why.
