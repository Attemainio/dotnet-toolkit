---
name: dotnet-write
description: Use when writing, adding, editing, modifying, patching, refactoring, renaming or deleting C#/.NET code — changing a method or type, changing a signature, adding a class or a file, fixing a compile error or an analyzer warning, or applying review findings. Validates every edit against an in-memory compilation before it touches disk, reports honestly whether that validation was sufficient for the kind of change made, and records why the change was made. Replaces Edit/Write on .cs files.
---

# Changing C# with dotnet-toolkit

`validate_patch` is the **only** way to change a `.cs` file, and `rename_symbol` is the only way to
rename one. Both apply your edit to a **forked in-memory solution**, compile it, and write to disk
only if the result is genuinely sufficient. Disk is never touched otherwise.

An edit that bypasses them is a change whose reasoning is gone when the conversation ends —
`search_log` cannot recover it, and the next session re-derives or silently contradicts it.

`Edit`/`Write` are for non-C# files (`.csproj`, `.json`, `.md`, `.cmd`) and for **creating** a new
`.cs` file — after which that file goes through `validate_patch` like any other.

Tool names are prefixed `mcp__plugin_dotnet-toolkit_dotnet__`.

## Step 0 — `workspace_status` before any edit

**Call it first, every session, and again before editing after any git operation.** It is free and
takes no arguments. Three things depend on it:

1. **A patch built on a `degraded` or `stale` workspace is built on the wrong content.** If
   `workspace` reports failed projects, fix the build and `reload_workspace` before patching —
   validation results from a degraded workspace may be silently wrong, not merely thin.

   `validate_patch` and `rename_symbol` now say so themselves, under `limitedBy: "degraded"`, and a
   failure there points at `workspace_status` rather than at your patch. **Believe it over the
   diagnostics**: a half-loaded compilation reports errors your change never introduced, and "revise
   the patch" would send you to rewrite code that was already correct.
2. **`pluginRoot`**, which is how you reach the coding standards below and any tool manual. Join it
   yourself: `<pluginRoot>/standards/index.md`, `<pluginRoot>/docs/tools/validate_patch.md`. **Never
   write `${CLAUDE_PLUGIN_ROOT}` into a path** — it is not expanded inside a rule or an agent
   definition, so it stays literal and the read fails.
3. **Confirmation the index is ready**, so `get_references` can tell you the blast radius rather than
   returning `workspace_loading`.

**After adding or deleting a `.cs` file, call `reload_workspace(scope: "all")`.** A new file is not in
the index or the compilation until then, so `validate_patch` cannot see it, `get_symbol` cannot find
its symbols, and a dependent compile will report it missing. `scope: "index"` re-scans files only;
`scope: "workspace"` re-opens MSBuild and rebuilds the symbol index; **`"all"` does both, and adding
or removing a file needs both.** A `PostToolUse` hook nudges you after a `Write` that creates a `.cs`
file, but the nudge is a backstop — reload deliberately.

## Step 1 — load the tools by exact name

Schemas are deferred. Load by exact name, never by describing the task:

```
ToolSearch("select:mcp__plugin_dotnet-toolkit_dotnet__validate_patch")
ToolSearch("select:mcp__plugin_dotnet-toolkit_dotnet__rename_symbol")
```

Free-text `ToolSearch` is lexical, not semantic, and fails on natural phrasing — `"edit a C# file
safely"` once returned `NotebookEdit`, `CronCreate` and `TaskOutput` and no MCP tool at all.

## Step 2 — read the standards, once per session

`<pluginRoot>/standards/index.md` is the routing table: the baseline set every C# change needs, plus a
row per aspect keyed to an observable property of the code you are about to write. Read the baseline
set plus every row that matches your change, and skim `antipatterns.md` once per session so a rule
saying "avoid the X antipattern" resolves to something you have actually read.

They are plugin-owned, never copied into a repo, and there is no per-repo override. Nothing
auto-loads them — this step is what loads them. Hold them; don't re-read per edit.

**Writing to the standard beats fixing to it.** `dotnet-code-review` checks against these same files
afterward.

Anything an analyzer can check mechanically, the analyzers do: `validate_patch` runs the projects'
referenced analyzers over the changed documents at the severities `.editorconfig` configures, blocking
at `error` and reporting at `warning`. That is a floor under these standards, not a replacement — the
judgment calls (which abstraction, which name, what a boundary owns) have no rule to enforce them.

### The write-time checklist

The handful of items most expensive to catch late — credential-shaped literals, concatenated SQL,
unmarked endpoints, in-memory database substitutes in tests that assert relational behavior — arrive
from the **`hint-write-checklist` hook** on the session's first `validate_patch`. They are not
repeated here: the hook reaches a caller who never loaded this skill, which is exactly the case a copy
here would miss, and two copies always drift.

## The tools

### `validate_patch` — the write path

Answers to:

- Will this C# change compile and apply safely?
- What validation level does this kind of change actually require?
- Is my edit stale, or built on content that has since moved?
- Does the edit target the correct symbol and the correct span?
- What code, API surface and dependents does it affect?
- If it failed, what is the root cause, and where?
- What should I inspect or change to fix it?
- Which checks actually ran, and over what scope?
- Can I amend just the failing part of a large patch instead of resending it?
- Is the change fully validated, or only partially verified?
- Has anything reached disk yet?
- What is the correct next action after this specific failure?

Manual: `<pluginRoot>/docs/tools/validate_patch.md` — error codes, draft lifetimes, `locations`
coordinate spaces, the `.editorconfig` severity table, and the full response contract.

### `rename_symbol` — a pure rename, everywhere

Answers to:

- What is every site that would have to change if I rename this?
- Will the new name collide with an existing one?
- Does the rename reach interface, virtual and delegate dispatch sites?
- Should comments and string literals change too?
- What about the other overloads?
- Does the containing file need renaming as well?

**A pure rename goes here, not to `validate_patch`.** It derives every call-site edit from the
compiler's own reference graph, runs the same validation ladder, and writes the same log entry;
authoring those edits yourself misses interface, virtual and delegate dispatch. It takes a single
`baseVersion` **string** rather than a `baseVersions` map — one symbol is named, the rest is derived.

Unlike `validate_patch`, **a dry run is worth it here** on a widely-referenced symbol, because the
blast radius is the thing you cannot predict. Rename *plus* other edits: rename first, then patch the
rest against freshly fetched versions. It does not rename the containing **file** — when that is
wanted the response says so under `fileRenameHint`, and the move is a `git mv` plus
`reload_workspace`.

Manual: `<pluginRoot>/docs/tools/rename_symbol.md`

### The read tools you need on the way in

Fetching what you are about to change is part of the write path, not a separate task. The `include`
grammar, the cheap-route table and the `limitedBy` semantics live in **`dotnet-read`** — invoke it if
you haven't. The write-specific rules are in the loop below.

## The loop

1. **Hold current content.** Fetch what you are about to change with `get_symbol`, using the
   *cheapest* include that serves what this edit actually needs, and keep its `contentVersion`:

   | The edit is | Fetch |
   |---|---|
   | A find/replace whose exact text you already know | `include: "bodyOutline"` — no source at all, and it leases the `body:` layer a body edit needs. Measured on a 117-line method: **192** tokens |
   | A rewrite of a body you need to read first | `source: "code"` (1,785 on that same member), or `source: "full-exact@N-M"` for one region |
   | A signature, doc comment or attribute only | The default include — those are `decl`, and no body lease is required |
   | Adding a member to a type | The type with `include: "members"`, to see where it belongs |
   | You genuinely want `usings`, `mechanicalFacts` *and* `referenceCounts` too | `include: "all"` (2,133) |

   **Every body-serving include leases the identical `body:` layer**, so `all` buys nothing a patch
   needs that `bodyOutline` does not — it is the widest include, never the required one. Reaching for
   it reflexively is the single most expensive habit on this path.

   **On a 500+ line declaration that fetch is guarded**, and comes back with `members`/`bodyOutline`
   and a `guard: large_source` block instead of the source. That is not an obstacle to route around
   — it is this same step's narrow-slice rule, enforced: take `declarationSites` from what came back
   and re-fetch the exact target with `source: "full-exact@N-M"`, which leases the body just
   as a whole fetch does. Repeat the call verbatim only when you genuinely need the whole declaration
   in front of you; that repeat is served in full.

   **Never hand-count a line number from a wider fetch.** If the edit touches a few lines inside a
   symbol you already read (or read wide to explore), do not scroll the wide response and count rows
   to find their absolute file line — that arithmetic is exactly what produces a patch against the
   wrong span, silently, with no error. Instead, **re-fetch that exact target with a narrow `@`
   slice** — `source: "full-exact@120-121"` — and take `startLine`/`endLine` straight from
   what comes back. A sliced `source` narrows `contentVersion` to `decl|body` exactly as a whole fetch
   does, so it leases the body without re-sending the member, and forcing `-exact` guarantees a number
   on every line so there is nothing left to count. Only when the edit's real boundary is ambiguous
   (does it include a leading blank line? a trailing brace?) is the narrow slice worth widening by a
   line or two — never by re-deriving the number from the original wide fetch.

   **Whatever you fetch, do not anchor a patch on a stripped one**: `-comments`, `-attributes` and
   `source: "code"` on a *type* drops lines from the response but not from the file, and `newText`
   replaces its span verbatim — so the lines you never saw are deleted. Strip on the read pass, fetch
   unstripped on the write pass.
2. **Know the blast radius.** If you are changing a signature, accessibility, base type or interface,
   call `get_references` first — dependent-compile failures across implementations are otherwise
   guaranteed.

   **Don't know which symbols the task even touches? Invoke `dotnet-explore`** rather than fanning out
   here: it spends the wide `search_index`/`get_references` responses in its own context and returns
   only `symbolId`s, use sites and affected files. It cannot edit and relays no `contentVersion`, so
   step 1 stays yours. Skip it when you already know the symbol.
3. **Check for a summary.** If the symbol you're changing has no `<summary>` (`xmlDoc.summary` absent
   from step 1's fetch, or `search_index`'s `hasSummary` was absent) — add one in the *same* patch,
   following `xml-documentation.md`'s tag rules: purpose only, 1–2 sentences, never restate the method
   name, implementation detail goes in `<remarks>`. An edit that leaves a touched public symbol
   undocumented is not a finished edit.
4. **Submit one patch** covering the symbol *and* every call site you already know needs updating,
   with `applyOnSuccess: true` set from the start.
5. **Read the verdict** (below). Fix and resubmit, or you're done.

**"Too large or interleaved to decompose" is not a reason to reach for `Edit`.** Split it into more
`validate_patch` calls, one per touched symbol, sharing one `intent`.

## The cheap-route table

| Anti-pattern (route taken) | Cheap route |
|---|---|
| `validate_patch(applyOnSuccess: false)` then an identical resubmission with `true` | Set `applyOnSuccess: true` from the start whenever the change is already decided — the ladder runs byte-for-byte identically either way, so a dry run then an apply re-compiles and resends the same payload for zero new information |
| Resubmitting a whole 200-line `newText` to fix one missing brace | Amend: send the response's `draftId` back with only the lines you are correcting |
| A fresh patch to satisfy `unheld_symbol` | Amend with the missing `baseVersions` entry and an **empty** `edits` array — `baseVersions` is inherited and merged |
| A fresh patch just to raise `requestedLevel` on a partial green | The same empty-`edits` amend |
| Fixing reported root causes one `validate_patch` call at a time | Fetch everything `suggestedInspection` names, then submit **one** revised patch covering all of them |
| One `validate_patch` per call site of a renamed symbol | `rename_symbol` — every reference derived from the compiler's graph |
| Search-and-replace over the tree to rename something | `rename_symbol` |
| One edit spanning lines 20–65 to change 20–25 and 60–65 | Two hunks — `newText` replaces its span verbatim, so the other 34 lines are resent for nothing |
| Hand-computing a line span for a "replace this exact text" change | Find/replace mode: `{symbolId, find, replace}` — no line arithmetic to get wrong |
| Patching a member's body from a `contentVersion` taken from `include: "members"` or the default | Fetch that member itself with an include that actually **serves the body** — those leases carry `decl` only, and a body edit against them is rejected as `unleased_body` |
| Reaching for `include: "all"` purely to obtain a body lease | Any body-serving include leases the identical `body:` layer, and `all` is the most expensive one. Measured on a 117-line method: `all` 2,133 tokens, `source: "code"` 1,785, `include: "bodyOutline"` **192** — same `body:` hash, and a find/replace patch built on the 192-token fetch validated clean. Take `bodyOutline` when you already know the text to change (find/replace mode needs no source at all), `source: "code"` when you need to read the body, and `all` only when you genuinely also want `usings`, `mechanicalFacts` and `referenceCounts` |
| Editing a new `.cs` file straight after `Write` | `reload_workspace(scope: "all")` first — it isn't in the compilation yet |
| `get_symbol` to refetch a symbol you just successfully patched | The applied response already returns its `newVersion` and refreshed `declarationSites` |
| A `pragma` suppression to get past an analyzer rule | Fix the code, or raise lowering that rule's `.editorconfig` severity **with the user** — never suppress silently |

## The arguments, and the judgment in each

- **`baseVersions`** — `symbolId → contentVersion` for every symbol you are changing. This is what
  proves the patch was built against current content.

  **A body edit needs a version that carries the body layer.** `get_symbol` narrows `contentVersion`
  to the layers it served, so the *default* include leases the declaration only and a body rewrite
  against it is rejected. Step 1's table is what avoids this — any of `bodyOutline`, `source` or
  `mechanicalFacts` serves the layer, and they are not equally priced. Two shapes catch people out:
  **a comment-only edit inside a body still counts as a body edit**, and **a member row from
  `include: "members"` leases `decl` only** — patch a member's body from a `get_symbol` on that
  member, not from its row.

  `baseVersions` covers the symbols you are changing, **not the rest of the file** — but an apply
  writes the whole document back, so a file that moved on disk underneath the workspace is refused
  outright rather than reverting everyone else's changes while reporting success. Expect that after a
  `git checkout`, a `git pull`, a rebase, or any `.cs` edit made with `Edit`.
- **`edits`** — an array where each entry is *either* a line-range edit `{file, lines, newText[,
  symbolId]}` *or* a symbol-scoped find/replace `{symbolId, find, replace[, replaceAll]}` — never both
  shapes on one entry. Know the full set of edits before calling and send that whole set in one call;
  never discover edit 2 only after submitting edit 1 when both were already known.

  **Line-range mode.** `lines` is `"N-M"` (or a bare `"N"`), 1-based inclusive, from
  `declarationSites` — never from `search_index`'s narrower `line`/`endLine`, and never hand-counted
  (see step 1). Draw each hunk around only what actually changed, splitting at real unchanged-content
  boundaries rather than into micro-hunks where changes genuinely cluster. Pass `symbolId` alongside
  `lines` on a fresh (non-amend) patch and the server cross-checks that the range actually falls
  inside that symbol's own declaration span — a free catch for exactly the wrong-span mistake above;
  it does nothing on an amend, since a draft's coordinates address its own proposed text. **Hunks may
  not overlap** — two spans sharing any line are refused as `invalid_edit`, since the second would
  address line numbers the first has already moved. Adjacent is fine; two changes inside one span go
  in one hunk's `newText`.

  **Find/replace mode.** `{symbolId, find, replace}` locates literal `find` text inside that symbol's
  *own* declaration span and replaces it — no line numbers at all. Errors if `find` occurs zero times
  (`find_not_found`) or more than once without `replaceAll: true` (`ambiguous_find_match`); set
  `replaceAll` to fix the same typo everywhere inside one symbol in a single edit. Resolves against
  the **live workspace only** — rejected (`find_replace_requires_fresh_patch`) alongside a `draftId`,
  so amend a find/replace failure by resending a fresh patch, not by amending the draft. Several
  find/replace edits may name the **same** `symbolId`; they apply in the order sent, each against what
  the previous one left, so a `find` an earlier edit already rewrote is `find_not_found` rather than a
  silent no-op. Prefer this mode whenever the change is "replace this exact text" rather than
  "rewrite this span".
- **`intent`** — REQUIRED to apply. One sentence of *why*, in user terms ("Add cancellation support to
  training"), not *what* — the diff already says that. Reuse the task's intent across its patches.
- **`runAnalyzers`** — leave at its default. Pass `false` only when a call genuinely needs nothing past
  compile/semantic correctness (re-validating an amend where the analyzer verdict is already known
  unchanged); skipping it silently narrows the floor under the standards.
- **`taskId`** — optional, and the one argument that outlives the call: an applied patch stamps it onto
  the development-log entry, so several patches sharing one `taskId` read back as one piece of work.
  Worth passing when several agents edit against the same server, or to group one task's patches in
  `get_retrieval_metrics(groupBy: "task")`.
- **`tags`** — optional `string[]` stamped onto the log entry alongside `intent`, for grouping under a
  label. Rarely worth setting.

## Reading the verdict — the only definition of "done"

```
isSufficient: true  AND  succeeded: true  AND  (applied: true OR you deliberately chose not to apply)
```

No other combination is done. In particular:

- **`succeeded: true` with `isSufficient: false`** is a *partial* green. The code is healthy only up
  to `completedLevel`; the change needs more. Do what `nextAction` says — usually resubmit with
  `requestedLevel` raised to `requiredLevel`. **Never report this as complete.**
- **`applied: false`** means the file on disk is unchanged, whatever else the response says.

Report status with the fields, not a vibe: *"compiles at project level; dependent compile still
required because the public signature changed"* — never just "it builds". The same honesty applies to
the **`checks`** block, returned on every `validate_patch` and `rename_symbol` call: it names which
rungs ran, the `scope` each ran over, and an explicit `notAssessed` list. **Report the scope it
names** — "no analyzer warnings" means *in the changed documents*, never repo-wide, and analyzer
**suggestions** are narrower still: only those on lines the patch actually rewrote, with the number
withheld as pre-existing stated in `notAssessed`. An empty `suggestions` is never "the file is clean".

Signature, accessibility, inheritance, interface, attribute, generic-constraint and public nullability
changes must show `requiredLevel` of at least `dependent_compile`. If you see less, escalate
explicitly with `requestedLevel`.

**An empty `detectedChanges` is not a free pass.** A patch that changed text but no symbol — a `using`
directive, a file-scoped namespace, an assembly attribute, a comment-only change — is floored at
`project_compile`, because parse cannot tell a harmless reformat from `using Nope.Missing;`. If you
see `detectedChanges: []` with `requiredLevel: parse`, that is a bug, not a cheap green.

## When it fails

`diagnostics.rootCauses` is already distilled — one per root cause, not one per compiler error, with a
`suggestedInspection` fetch plan and a count of downstream errors that vanish once the cause is fixed.
Don't chase those. **Batch the recovery**: fetch everything suggested and submit ONE revised patch
covering all of it. Never resubmit an identical patch.

**Amend the draft rather than resending the patch.** Every unapplied response carries a `draft`
holding the exact text you proposed. Send its `draftId` back with only the lines you are correcting —
a missing brace in a 200-line `newText` costs one line, not 200 — and `baseVersions` is inherited,
with anything you send merged in. That merge is also the fix when the classifier wants a version you
didn't send (adding a member anchors the change to its containing type, so inserting a method into
`Foo` needs `Foo`'s version): resend the missing entry with an **empty** `edits` array.

**A response with no draft means rebuild, not amend** — the patch was built on something that has
since moved, so re-derive the text from a fresh `get_symbol`.

One decision is yours rather than the tool's: when a failure came from an analyzer, you can fix the
code **or** lower that rule's severity in `.editorconfig` if it is wrong for this repo. The second is a
real answer, not a workaround — but it is the *user's* call, so raise it rather than editing
`.editorconfig` unasked. Never suppress with a pragma to get past a rule the repo deliberately turned
on.

## Editing the same symbol again — don't refetch it

An applied response gives each changed symbol both halves the next patch needs: **`newVersion`** (the
`baseVersions` entry for the next edit) and **`declarationSites`** (where the declaration now sits, in
the same shape `get_symbol` returns). So a second edit to a symbol you just changed goes straight into
another `validate_patch`. Refetch only when you need *content* you no longer hold, or for a symbol
this patch did not change.

## What gets recorded

An applied patch or rename appends one development-log entry: your `intent`, the symbols changed,
their old and new versions, and the API impact of each. That is why `intent` is required — the diff
records *what* changed; only you can record *why*.

Read it back with `search_log` — before proposing a design, to find out whether the approach was
already tried and rejected, and why.
