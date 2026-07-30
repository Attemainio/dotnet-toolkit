# `get_call_hierarchy` — who eventually calls this, up to the entry points

## When to reach for it

An open-ended multi-level call tree from one symbol — Visual Studio's *View Call Hierarchy*,
which `get_call_slice` structurally cannot answer (it needs a known `to`). `direction:
"callers"` (default) walks upward toward entry points; `"callees"` walks downward into what
the symbol invokes. Every node carries `symbolId` + `displayString` (the containing type and member
name, parameter list dropped — the same compact form `get_references` rows use, and overloads still
disambiguate via `symbolId`); add `kind`/`file`/`line`/`signature` (the full parameter-list form) via
`fields`.

Call it to answer "if I change this, how much does it ripple" — `includeTree: false` returns
only the `blastRadius` summary (unique nodes reached, per depth) for the cheapest possible
version of that question, without paying for the full tree. `blastRadius` counts every symbol
**reached** from the root, including the children a per-node cap left unexpanded, and reports that cap
as `truncated`/`omittedChildren` in both shapes — so the summary-only answer is never smaller than the
number the tree printed. It is not, however, cap-independent: an unexpanded node's *own* callers are
never visited, so past `maxDepth: 1` a lower `maxChildrenPerNode` genuinely finds less. Compare totals
only at equal caps.

```
get_call_hierarchy(symbol: "FeatureLogStore.Append", direction: "callers", maxDepth: 1)
```

```json
{"direction": "callers",
 "tree": {"symbolId": "sym_c25d...", "displayString": "FeatureLogStore.Append",
   "children": [
     {"symbolId": "sym_0e0e...", "displayString": "DevlogMigration.Run"},
     {"symbolId": "sym_c3fc...", "displayString": "FeatureLogStoreTests.ResolveIdChain_SingleHop_ReturnsBothIds"},
     {"symbolId": "sym_2b15...", "displayString": "PatchTools.AppendLog"}, "...4 more"]},
 "blastRadius": {"totalUniqueNodes": 8, "perDepth": [1, 7], "depthCapped": true}}
```

`depthCapped: true` here means `Append` has callers beyond `maxDepth: 1` — raise it to see further up the
chain (a real 3-level pull from this same root reaches `PatchTools.ValidatePatch`, the actual MCP tool
entry point, at depth 3).

A symbol reached through two different branches (a diamond) legitimately appears twice in the
tree — that isn't deduped, since collapsing it would hide a real second route in — but counts
once in `blastRadius`. True recursion (a symbol reappearing on its own root-to-node path) stops
as a leaf marked `recursive: true` rather than looping. `maxDepth` defaults to 3 and clamps to
8; a well-connected graph grows fast, so start shallow and increase only if the answer needs
it, or lean on `blastRadius.depthCapped` to see whether a branch was still expanding when the
cap hit.

## Reference

Replaces: chaining `get_references(direction: "callers")` by hand, one level at a time, and assembling
the tree yourself. This is what `get_call_slice` cannot do — it needs a known destination; this tool
needs only a root. `direction: "callers"` (default, Visual Studio's *View Call Hierarchy*) walks
upward toward entry points; `"callees"` walks downward into what the symbol invokes.

| Arg | Meaning |
|---|---|
| `symbol` | Required. Same addressing as `get_symbol`. |
| `direction` | `callers` (default) \| `callees`. |
| `maxDepth` | Default 3, clamped 1-8 — a well-connected graph grows fast past that. |
| `maxChildrenPerNode` | Default 25, clamped 1-200. A node past the cap keeps its own entry but stops expanding, marked `truncated:true` with `omittedChildren`. The children it left out are still **counted** in `blastRadius`, so the cap never hides a node at the depth it was found; but their own callers go unvisited, so at `maxDepth` above 1 a tighter cap does reduce the total. |
| `includeTree` | Default `true`. Set `false` for just `blastRadius` — the cheapest possible answer to "how much does changing this ripple." That shape is also the only one carrying a separate `root` block: with a tree, its head node **is** the root, and emitting both repeated every root field. |
| `fields` | Comma list adding `kind`, `file`, `line`, or `signature` (the full parameter-list `displayString` instead of the default type-and-member name) to every node beyond the always-present `symbolId`/`displayString`. |

Real call and response (trimmed to 4 of 7 children):

```
get_call_hierarchy(symbol: "FeatureLogStore.Append", direction: "callers", maxDepth: 1)
```

```json
{"direction":"callers",
 "tree":{"symbolId":"sym_c25d...","displayString":"FeatureLogStore.Append",
   "children":[
     {"symbolId":"sym_0e0e...","displayString":"DevlogMigration.Run"},
     {"symbolId":"sym_c3fc...","displayString":"FeatureLogStoreTests.ResolveIdChain_SingleHop_ReturnsBothIds"},
     {"symbolId":"sym_2b15...","displayString":"PatchTools.AppendLog"}]},
 "blastRadius":{"totalUniqueNodes":8,"perDepth":[1,7],"depthCapped":true}}
```

`displayString` is the containing type and member name with the parameter list dropped — the namespace
in front of it was a third of a 25-node tree, repeated once per sibling, and `symbolId` still
disambiguates overloads. (A member of a *nested* type keeps the inner type and loses the outer one.)
Pass `fields:"signature"` for the full parameter-list form (e.g.
`"string FeatureLogStore.Append(LogEntry entry)"`) when the signature itself is what's needed.

`blastRadius` counts reached nodes, not rendered ones: at `maxDepth: 1` on this same root,
`maxChildrenPerNode: 1` reports the same `totalUniqueNodes` as `maxChildrenPerNode: 200` and adds
`truncated:true, omittedChildren:6`. That holds with `includeTree:false` too — the shape whose entire
purpose is this number is not the shape that gets a worse one.

Past depth 1 the equality stops holding, and that is a property of the graph walk rather than a
reporting gap: a node the cap did not expand contributes itself to the count but contributes none of
**its** callers, so the frontier is narrower at every level below. Measured on `Formats.Render`
(`maxDepth: 3`): `maxChildrenPerNode: 200` reaches 126 nodes, `maxChildrenPerNode: 2` reaches 23 and
reports `omittedChildren: 18`. Read a capped total as a floor, and never diff two runs whose caps
differ.

`depthCapped:true` means `Append` has callers beyond `maxDepth:1` — raising `maxDepth` reaches
`PatchTools.ValidatePatch` (the actual MCP tool entry point) three levels up from this root.

A caller resolved through the edge cache but absent from the `symbols` table — a synthesized entry
point like C#'s top-level-statements `Main`, which `get_references` renders as
`<top-level-statements-entry-point>` via live Roslyn but the cache never stored a row for — falls back
to its bare `symbolId` as `displayString` rather than failing the whole call. Rare in practice (this
repo hits it once, at `DevlogMigration.Run`'s own caller), but worth recognizing if a leaf's
`displayString` looks like a `sym_...` id instead of a real signature.

A symbol reached through two different branches (a diamond) legitimately appears twice in the tree —
not deduped, since collapsing it would hide a real second route in — but counts once in `blastRadius`.
True recursion (a symbol reappearing on its own root-to-node path) stops as a leaf marked
`recursive:true` rather than looping. Internally capped at a few thousand total nodes as a safety net
against pathological fan-out, independent of `maxChildrenPerNode`.

## Next steps

- **Need file/line/snippet per call site** → `get_references` — `get_references.md`
- **One hop on a symbol with only a handful of callers** → `get_references` is both cheaper and richer there. This tool's fixed overhead only pays off as fan-in grows: measured at 105 callers it cost 637 tokens against `get_references`' 5,266, but at a single caller 139 against 100 — and the 100 included file, line, snippet and `dispatchKind`.
- **Have a specific destination in mind** → `get_call_slice` is cheaper — `get_call_slice.md`
- **Judging a removal** → check `blastRadius`, then `get_symbol` on the survivors — `get_symbol.md`
