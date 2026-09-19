# `rename_symbol` — rename a symbol and every reference to it, from the semantic model

Replaces: a search-and-replace, or a pile of `Edit` calls hand-written one call site at a time.
Roslyn computes the edits from the compiler's own reference graph — interface, virtual and delegate
dispatch included, unrelated text that shares the name excluded — and writes them to disk when asked.

This is the one edit the toolkit still makes itself. Everything else is `Edit`/`Write` on text you
have read, with `dotnet build` as the gate; a rename is the case a string replacement cannot do
safely, because the set of sites is a semantic question. **Nothing here compiles the result** —
`dotnet build` is the gate for this edit too. What the tool adds is the rewrite and the blast radius
before it.

| Arg | Meaning |
|---|---|
| `symbol` | What to rename: a fully-qualified name, a unique suffix, or a `sym_…` id from any previous response. Append a parameter list (`Widget.Spin(int)`) to pick one overload. Several matches is `error: "ambiguous_symbol"` with the candidate list. |
| `newName` | The new identifier. Not a valid C# identifier, or a bare keyword, is `error: "invalid_name"` — rejected before any work. Prefix with `@` if a verbatim identifier is genuinely intended. |
| `apply` | Write to disk (default `false`). `false` is a complete dry run. |
| `renameOverloads` | Also rename this method's sibling overloads (default `false`). Ignored for a non-method symbol. |
| `renameInComments` | Also rewrite the old name where it appears in comments and doc comments (default `false`). |
| `renameInStrings` | Also rewrite it inside string literals (default `false`). |

## The dry run is the point

`apply: false` (the default) computes the rename in a forked in-memory solution and reports it in
full — **nothing is written**. Read `files` before applying, especially for a widely-referenced
symbol: the blast radius is the thing you cannot predict.

```json
"rename":{"oldName":"Spin","newName":"Rotate","oldSymbolId":"sym_5c62…","newSymbolId":"sym_8306…",
          "kind":"Method","filesChanged":2,"occurrencesRewritten":5},
"files":[{"file":"Lib/Widget.cs","occurrences":4},{"file":"App/Program.cs","occurrences":1}],
"applied":false,
"notChecked":"Dry run: nothing was written. Pass apply:true to write these files."
```

`occurrences` counts **whole-identifier occurrences of the old name that disappeared** in that file —
declaration, every reference, every `<see cref="…"/>`, plus comment/string hits when you opted into
them. It is not a diff-region count: Roslyn coalesces a rename's scattered edits into one text change
per file, which reports `1` for a file where four references moved.

The same fields on an applied run mean the same thing, with `applied: true` and `notChecked` saying
to build.

## Applying

`apply: true` writes every changed file back in the encoding it was read in, adopts the new text
into the live workspace, and **refreshes the symbol index for those files in the same call** — so
`newSymbolId` resolves and the new name is searchable immediately, without a `reload_workspace`.

`newSymbolId` differs from `oldSymbolId` by construction: a symbol id hashes the fully-qualified
name. Anything you hold under the old id is now stale; use the new one.

The rewritten solution is **not compiled**. Run `dotnet build` afterwards, as after any edit. Two
things the response does tell you ahead of the build:

- **`nameAlreadyExists`** — the same container already declares a member with the new name. A
  same-signature clash will not compile (CS0102/CS0229); a different-signature one compiles
  perfectly, because C# has simply gained an overload — you asked for a rename and got an overload
  set. The note fires on both, because the tool cannot tell them apart without compiling; the build
  can. Decide, don't skim past it.
- **`limitedBy: "degraded"`** — projects failed to load, so the reference graph this rename derived
  its edits from is incomplete: call sites in those projects are silently missing, which is a wrong
  rename rather than a thin one. Fix the load failure, `reload_workspace`, and rerun before trusting
  the result.

## What this tool deliberately does not do

**It does not rename the file.** When the symbol is a type whose file is named after it, the response
carries a `fileRenameHint` naming the paths and the `git mv` to run. Renaming the file inside the
workspace would leave this server holding a document whose `FilePath` no longer exists, and change
detection here is an mtime poll that cannot reconcile that; the refresh hook picks up the `git mv`.

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
| `stale_workspace` | The workspace's copy of a file the rename touches is behind disk, usually mid-reload. `workspace_status`, wait for `loaded`, retry — applying would revert whatever else changed in that file. Only on `apply: true`; a dry run reports from whatever the workspace holds. |
| `rename_rejected` | Roslyn itself refused the rename; the message says why. |
| `no_changes` | The rename produced no text changes at all. |
| `workspace_loading` | Semantics not ready. Check `workspace_status`, retry. |

## Next steps

- To see the blast radius first, or to check whether a name is even free: this tool with the
  default `apply: false`.
- After applying: `dotnet build`. If `fileRenameHint` is present: `git mv`.
- Rename plus other edits: rename first, then `Edit` the rest against a fresh read — the text moved.
