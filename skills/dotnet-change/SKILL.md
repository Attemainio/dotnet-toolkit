---
name: dotnet-change
description: Use when changing C# code in a .NET repo - editing a method or type, changing a signature, renaming, or fixing a compile error. Validates the edit against an in-memory compilation before it touches disk, reports honestly whether that validation was sufficient for the kind of change made, and records why the change was made.
---

# Changing C# code safely

This repo has the dotnet-toolkit MCP server. For C# edits, go through
`mcp__plugin_dotnet-toolkit_dotnet__validate_patch` instead of editing the file and hoping. It applies
your edit to a **forked in-memory solution**, compiles it, and writes to disk only if the result is
genuinely sufficient. Disk is never touched otherwise.

Use `Edit`/`Write` directly only for non-C# files (csproj, json, md), and for creating a new `.cs`
file — after which that file goes through `validate_patch` like any other.

This skill carries the **procedure and the judgment calls**. Argument grammar, every error code and the
full response contract live in `<pluginRoot>/docs/tools/validate_patch.md`; read it when you hit
something this file names but does not spell out.

**A pure rename goes to `rename_symbol`, not here.** If the whole change is "call this something else",
it derives every call-site edit from the compiler's own reference graph, validates through this same
ladder, and writes the same log entry — authoring those edits yourself misses interface, virtual and
delegate dispatch. It takes a single `baseVersion` string rather than a `baseVersions` map, and a dry
run **is** worth it there on a widely-referenced symbol, because the blast radius is the thing you
cannot predict. Rename *plus* other edits: rename first, then patch the rest against freshly fetched
versions. Details: `docs/tools/rename_symbol.md`.

## Before the first C# edit of a session: read the standards

The canonical coding standards are plugin-owned and never copied into a repo, so they are always
current. **Call `workspace_status` and take its `pluginRoot:` line**, then read
`<pluginRoot>/standards/<name>.md`. That is the only location, and there is no per-repo override tier.
Never write `${CLAUDE_PLUGIN_ROOT}` into the path — it is not expanded here.

They are **not** auto-loaded and no `paths:` trigger reaches them, so this step is what loads them.
**Which ones to read is the standards table in `.claude/rules/index.md`** — always-loaded, so it is
already in front of you. Read the baseline set plus every row whose "When" column matches the change
you are about to make, and skim `antipatterns.md` once per session so a rule that says "avoid the X
antipattern" resolves to something you have actually read.

Once per session is enough — hold them; don't re-read per edit. `dotnet-code-review` validates against
the same files afterward, but writing to the standard beats fixing to it.

### Write-time checklist

The handful of items most expensive to catch late — credential-shaped literals, concatenated SQL,
unmarked endpoints, in-memory database substitutes in tests that assert relational behavior — arrive
from the **`hint-write-checklist` hook** on the session's first `validate_patch`. They are not repeated
here: the hook reaches a caller who never loaded this skill, which is exactly the case a copy here
would miss, and two copies would drift. The standards named above are where each item is argued.

Anything an analyzer can check mechanically, the analyzers do: `validate_patch` runs the projects'
referenced analyzers over the changed documents at the severities `.editorconfig` configures, blocking
at `error` and reporting at `warning`. That is a floor under these standards, not a replacement — the
judgment calls above (which abstraction, which name, what a boundary owns) have no rule to enforce them.
Leave `runAnalyzers` at its default; pass `false` only when a call genuinely needs nothing past
compile/semantic correctness (e.g. re-validating an amend where the analyzer verdict is already known
unchanged) — skipping it silently narrows this floor.

## The loop

1. **Hold current content.** Fetch what you are about to change with `get_symbol` (`include: "all"`)
   and keep its `contentVersion`.

   **Two refinements once you know the symbol.** Coming back into a long member you have already read,
   `include: "source@120-160"` is a complete pre-edit fetch — a sliced `source` narrows
   `contentVersion` to `decl|body` exactly as a whole fetch does, so it leases the body without
   re-sending the member. And whatever you fetch, **do not anchor a patch on a stripped one**:
   `-comments`, `-attributes` and `source:code` on a *type* drop lines from the response but not from
   the file, and `newText` replaces its span verbatim — so the lines you never saw are deleted. Strip
   on the read pass, fetch unstripped on the write pass.
2. **Know the blast radius.** If you are changing a signature, accessibility, base type or interface,
   call `get_references` first — dependent-compile failures across implementations are otherwise
   guaranteed.

   **Don't know which symbols the task even touches? Delegate the sweep to the `dotnet-explore`
   agent** rather than fanning out here: it spends the wide `search_index`/`get_references` responses
   in its own context and returns only `symbolId`s, use sites and affected files. It cannot edit and
   relays no `contentVersion`, so step 1 stays yours. Skip it when you already know the symbol.
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

**Don't dry-run first when you already intend to make the change.** The ladder runs byte-for-byte
identically either way; `applyOnSuccess` only gates whether a *sufficient, successful* result reaches
disk. A dry run then an apply re-runs the same compile and resends the same payload for zero additional
information. Dry-run only when you are genuinely undecided whether to make the change at all and want
the blast radius before committing — the rare case, not the default.

## The four arguments, and the judgment in each

- **`baseVersions`** — `symbolId → contentVersion` for every symbol you are changing. This is what
  proves the patch was built against current content.

  **A body edit needs a version that carries the body layer.** `get_symbol` narrows `contentVersion` to
  the layers it served, so the *default* include leases the declaration only and a body rewrite against
  it is rejected. Step 1's `include: "all"` is what avoids this. Two shapes catch people out: **a
  comment-only edit inside a body still counts as a body edit**, and **a member row from
  `include: "members"` leases `decl` only** — patch a member's body from a `get_symbol` on that member,
  not from its row.

  `baseVersions` covers the symbols you are changing, **not the rest of the file** — but an apply writes
  the whole document back, so a file that moved on disk underneath the workspace is refused outright
  rather than reverting everyone else's changes while reporting success. Expect that after a
  `git checkout`, a `git pull`, a rebase, or any `.cs` edit made with `Edit`.
- **`edits`** — an array of `{file, startLine, endLine, newText}`. Know the full set of edits before
  calling and send that whole set in one call; never discover edit 2 only after submitting edit 1 when
  both were already known. **Draw each hunk around only what actually changed** — `newText` replaces its
  span verbatim, so one edit spanning lines 20–65 to change 20–25 and 60–65 resends 34 unchanged lines
  for nothing. Split at real unchanged-content boundaries, not into micro-hunks where changes genuinely
  cluster. Spans come from `declarationSites`, never from `search_index`'s narrower `line`/`endLine`.
- **`intent`** — REQUIRED to apply. One sentence of *why*, in user terms ("Add cancellation support to
  training"), not *what* — the diff already says that. Reuse the task's intent across its patches.
- **`taskId`** — optional, and the one argument that outlives the call: an applied patch stamps it onto
  the development-log entry, so several patches sharing one `taskId` read back as one piece of work.
  Worth passing when several agents edit against the same server, or when you want one task's patches
  grouped in `get_retrieval_metrics(groupBy: "task")`.
- **`tags`** — optional `string[]` stamped onto the development-log entry alongside `intent`, for
  grouping related patches under a label `search_log` can later match on. Rarely worth setting; skip it
  unless the change is part of a labeled effort worth finding again as a group.

## Reading the verdict — the only definition of "done"

```
isSufficient: true  AND  succeeded: true  AND  (applied: true OR you deliberately chose not to apply)
```

No other combination is done. In particular:

- **`succeeded: true` with `isSufficient: false`** is a *partial* green. The code is healthy only up to
  `completedLevel`; the change needs more. Do what `nextAction` says — usually resubmit with
  `requestedLevel` raised to `requiredLevel`. **Never report this as complete.**
- **`applied: false`** means the file on disk is unchanged, whatever else the response says.

Report status with the fields, not a vibe: *"compiles at project level; dependent compile still required
because the public signature changed"* — never just "it builds". The same honesty applies to `checks`:
it names the `scope` each rung ran over and lists what was `notAssessed`, so "no analyzer warnings"
means *in the changed documents*, never repo-wide.

Signature, accessibility, inheritance, interface, attribute, generic-constraint and public nullability
changes must show `requiredLevel` of at least `dependent_compile`. If you see less, escalate explicitly
with `requestedLevel`.

**An empty `detectedChanges` is not a free pass.** A patch that changed text but no symbol — a `using`
directive, a file-scoped namespace, an assembly attribute, a comment-only change — is floored at
`project_compile`, because parse cannot tell a harmless reformat from `using Nope.Missing;`. If you see
`detectedChanges: []` with `requiredLevel: parse`, that is a bug, not a cheap green.

## When it fails

`diagnostics.rootCauses` is already distilled — one per root cause, not one per compiler error, with a
`suggestedInspection` fetch plan and a count of downstream errors that vanish once the cause is fixed.
Don't chase those. **Batch the recovery**: fetch everything suggested and submit ONE revised patch
covering all of it. Never resubmit an identical patch, and never fix root causes one call at a time.

**Amend the draft rather than resending the patch.** Every unapplied response carries a `draft` holding
the exact text you proposed. Send its `draftId` back with only the lines you are correcting — a missing
brace in a 200-line `newText` costs one line, not 200 — and `baseVersions` is inherited, with anything
you send merged in. That merge is also the fix when the classifier wants a version you didn't send
(adding a member anchors the change to its containing type, so inserting a method into `Foo` needs
`Foo`'s version): resend the missing entry with an **empty** `edits` array. The same empty-`edits` amend
raises `requestedLevel` on a partial green.

**A response with no draft means rebuild, not amend** — the patch was built on something that has since
moved, so re-derive the text from a fresh `get_symbol`.

Error codes, draft lifetimes, `locations` coordinate spaces and the `.editorconfig` severity table:
`docs/tools/validate_patch.md`. One decision there is yours rather than the tool's: when a failure came
from an analyzer, you can fix the code **or** lower that rule's severity in `.editorconfig` if it is
wrong for this repo. The second is a real answer, not a workaround — but it is the *user's* call, so
raise it rather than editing `.editorconfig` unasked. Never suppress with a pragma to get past a rule
the repo deliberately turned on.

## Editing the same symbol again — don't refetch it

An applied response gives each changed symbol both halves the next patch needs: **`newVersion`** (the
`baseVersions` entry for the next edit) and **`declarationSites`** (where the declaration now sits, in
the same shape `get_symbol` returns). So a second edit to a symbol you just changed goes straight into
another `validate_patch`. Refetch only when you need *content* you no longer hold, or for a symbol this
patch did not change.

## What gets recorded

An applied patch appends one development-log entry: your `intent`, the symbols changed, their old and
new versions, and the API impact of each. That is why `intent` is required — the diff records *what*
changed; only you can record *why*.

Read it back with `search_log` — before proposing a design, to find out whether the approach was
already tried and rejected, and why.
