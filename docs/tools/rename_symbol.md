# `rename_symbol` — rename a symbol and every reference to it, from the semantic model

Replaces: a search-and-replace, or a chain of `validate_patch` calls hand-written one call site at a
time. Roslyn computes the edits from the compiler's own reference graph, applies them to a **forked
in-memory solution**, and runs the same validation ladder `validate_patch` runs — so a name collision is
a reported compile failure, not a broken working tree.

Why this exists as its own tool rather than as a `validate_patch` recipe: a rename's edits are
**derived**, not authored. You never write them, so there is nothing for `baseVersions`, drafts, or
amends to guard beyond the one symbol you named. Everything after the fork — the ladder, the diagnostic
distiller, the disk commit, the development-log entry — is literally the same code path as
`validate_patch`.

| Arg | Meaning |
|---|---|
| `symbol` | What to rename: a fully-qualified name, a unique suffix, or a `sym_…` id from any previous response. Append a parameter list (`Widget.Spin(int)`) to pick one overload. Several matches is `error: "ambiguous_symbol"` with the candidate list. |
| `newName` | The new identifier. Not a valid C# identifier, or a bare keyword, is `error: "invalid_name"` — rejected before any work, so you don't pay a ladder run to learn it. Prefix with `@` if a verbatim identifier is genuinely intended. |
| `baseVersion` | **Required.** The `contentVersion` `get_symbol` handed out for this symbol — a single string, not the `{id: version}` map `validate_patch` takes, because only one symbol is being named. A version that disagrees is `error: "stale_base"`. The declaration-only token from a default `get_symbol` fetch is enough; there is no `unleased_body` equivalent here, since you are not rewriting a body. |
| `applyOnSuccess` | Commit to disk when sufficient and successful (default `false`). |
| `intent` | **Required when `applyOnSuccess: true`.** One sentence of *why* — applying with one is what writes to the development log (this tool and `validate_patch` are its only writers). |
| `renameOverloads` | Also rename this method's sibling overloads (default `false`). Ignored for a non-method symbol. |
| `renameInComments` | Also rewrite the old name where it appears in comments and doc comments (default `false`). |
| `renameInStrings` | Also rewrite it inside string literals (default `false`). |
| `requestedLevel` | Optional floor, same vocabulary as `validate_patch`. The computed level is already floored at `dependent_compile` (see below), so this only ever raises past that. An unrecognized value is silently not honored, same as `validate_patch` — `ladder.requestedLevelHint` says so and names what it probably was. |
| `tags` | Optional `string[]` on the development-log entry. |

## The dry run is the point

`applyOnSuccess: false` (the default) is a complete rehearsal: the rename is computed, validated to
`dependent_compile` or higher, and reported in full — **nothing is written**. Read `files` before
applying, especially for a widely-referenced symbol.

```json
"rename":{"oldName":"Spin","newName":"Rotate","oldSymbolId":"sym_5c62…","newSymbolId":"sym_8306…",
          "kind":"Method","filesChanged":2,"occurrencesRewritten":5},
"files":[{"file":"Lib/Widget.cs","occurrences":4},{"file":"App/Program.cs","occurrences":1}],
"ladder":{"completedLevel":"dependent_compile","requiredLevel":"dependent_compile","isSufficient":true},
"succeeded":true,"applied":false
```

`occurrences` counts **whole-identifier occurrences of the old name that disappeared** in that file —
declaration, every reference, every `<see cref="…"/>`, plus comment/string hits when you opted into
them. It is not a diff-region count: Roslyn coalesces a rename's scattered edits into one text change
per file, which reports `1` for a file where four references moved.

The same fields on an applied run mean the same thing, with `applied: true`.

## Reading the verdict

Done means all of `succeeded: true`, `isSufficient: true`, `applied: true` (or a deliberate dry run).
`succeeded: false` means the rename does not compile — almost always the new name already exists in the
same scope. `diagnostics.rootCauses` has the same distilled shape `validate_patch` returns; fix the
collision with `validate_patch` first, then retry the rename. Nothing reached disk either way.

The response also carries the same **`checks`** block `validate_patch` returns — which rungs ran and over
what, the analyzer pass's findings by severity, and what went unassessed. See
`docs/tools/validate_patch.md` for the shape and for how `.editorconfig` severity decides what blocks.

One failure mode is specific to renaming: the new name can trip a **naming or documentation analyzer**
the old one satisfied. If that rule's effective severity is `error`, the rename is blocked even though
every call site rewrote correctly, and `nextAction` points at the rule's severity rather than at a
collision.

**`dependent_compile` is the floor**, not the computed level. A rename alters an existing contract by
definition and its call sites can sit in any project that depends on this one, so a rename is never
signed off by one project's compile — even when the only detected change in a referencing file reads as
a body edit.

### `membersRekeyed` — mechanical churn, collapsed

Renaming a type re-keys the `symbolId` of every member it contains, so the raw change list reports each
one as an `added`/`removed` pair — a 20-member type renders 40 entries that say nothing beyond "the type
was renamed", which the response already states. Those pairs (solely-`added` matched against
solely-`removed` by bare name) are collapsed out of the **reported** list and counted instead:

```
membersRekeyed: 2
```

This is a **reporting** change only. The full detected set still drives ladder escalation, targeted test
selection, and diagnostics, so nothing about what gets validated depends on it. A member that genuinely
changed — not merely re-keyed — does not pair off and stays in the reported list.

## What this tool deliberately does not do

**It does not rename the file.** When the symbol is a type whose file is named after it, the response
carries a `fileRenameHint` naming the paths and the `git mv` to run, followed by `reload_workspace`.
Renaming the file inside the workspace would leave this server holding a document whose `FilePath` no
longer exists, and change detection here is an mtime poll that cannot reconcile that.

**Comments and strings are opt-in, and they are textual guesses.** The reference rewrite is semantic and
complete; `renameInComments`/`renameInStrings` are ordinary word-boundary matching, with all the
false-positive risk that implies. Leave them off unless you have a reason — a name genuinely reflected
over, or prose that would read as wrong afterwards.

## Errors

| `error` | Means |
|---|---|
| `symbol_not_found` / `ambiguous_symbol` | Nothing matched, or several did. `symbol_not_found` carries `didYouMean` — the same ranked-then-edit-distance candidate list `get_symbol` returns, described in `get_symbol.md`. The ambiguous payload is byte-for-byte the one `get_symbol` returns (same renderer): up to ten candidates with their `symbolId`s, plus `totalCandidates` and, when the cap bit, `truncated: true` — re-call with one exactly, or narrow the name if the intended one was cut. |
| `invalid_name` | Not a valid identifier, or a bare keyword. |
| `unchanged_name` | The symbol already has that name. |
| `external_symbol` | Declared outside this solution's source (a BCL/NuGet symbol) — not renameable here. |
| `missing_base_version` / `stale_base` | No `baseVersion`, or one that no longer matches. Refetch with `get_symbol`. |
| `stale_workspace` | The workspace's copy of a file the rename touches is behind disk. `reload_workspace` and retry — applying would revert whatever else changed in that file. |
| `intent_required` | `applyOnSuccess: true` without an `intent`. |
| `rename_rejected` | Roslyn itself refused the rename; the message says why. |
| `no_changes` | The rename produced no text changes at all. |
| `workspace_loading` | Semantics not ready. Check `workspace_status`, retry. |

Not an error, but read it the same way: **`limitedBy: "degraded"`** on a successful response means projects
failed to load, so the reference graph this rename derived its edits from is incomplete — call sites in
those projects are silently missing, which is a wrong rename rather than a thin one. On a failed one,
`ladder.nextAction` points at `workspace_status` instead of claiming the new name collides. Either way:
fix the load failure, `reload_workspace`, and rerun before trusting the result.

## Next steps

- Before calling: `get_symbol` for the `contentVersion` (a default fetch is enough).
- To see the blast radius first, or to check whether a name is even free: this tool with
  `applyOnSuccess: false`.
- After applying, if `fileRenameHint` is present: `git mv`, then `reload_workspace`.
- To fix a collision the ladder reported: `validate_patch`, then retry the rename.
