# `get_call_hierarchy` — who eventually calls this, up to the entry points

## When to reach for it

An open-ended multi-level call tree from one symbol — Visual Studio's *View Call Hierarchy*,
which `get_call_slice` structurally cannot answer (it needs a known `to`). `direction:
"callers"` (default) walks upward toward entry points; `"callees"` walks downward into what
the symbol invokes. Every node carries `symbolId`, plus `displayString` (the containing type and member
name, parameter list dropped — the same compact form `get_references` rows use, and overloads still
disambiguate via `symbolId`) whenever the index can name it; add `kind`/`file`/`line`/`signature` (the
full parameter-list form) via `fields`.

Call it to answer "if I change this, how much does it ripple" — `includeTree: false` returns
only the `blastRadius` summary (unique nodes reached, per depth) for the cheapest possible
version of that question, without paying for the full tree. `blastRadius` counts every symbol
**reached** from the root, including the children a per-node cap left unexpanded, and reports that cap
as `truncated`/`omittedChildren` — so the summary-only answer is never smaller than the number the
tree printed. **With the tree included**, `blastRadius` states its own `truncated`/`omittedChildren`
only when they would say something the tree does not already show — a single truncated node (the root
itself, the common shallow/high-fan-in case) already carries the identical number on `tree`, so
`blastRadius` stays quiet rather than repeating it; a total spread across several truncated nodes
deeper in the tree still gets stated here, since no single node's own field carries the sum. It is not,
however, cap-independent: an unexpanded node's *own* callers are never visited, so past `maxDepth: 1` a
lower `maxChildrenPerNode` genuinely finds less. Compare totals only at equal caps.

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

A symbol reached through two different branches (a diamond) appears twice in the tree — the
second route in is real and is not hidden — but is **expanded** only once, and counts once in
`blastRadius`. The later occurrence carries `repeated: true` with no `children` of its own;
follow its `symbolId` to the branch that did expand. True recursion (a symbol reappearing on its
own root-to-node path) stops as a leaf marked `recursive: true` rather than looping. `maxDepth` defaults to 3 and clamps to
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
| `direction` | `callers` (default) \| `callees`. An unrecognized value falls back to `callers` and the response carries `directionHint` naming what it probably was. |
| `maxDepth` | Default 3, clamped 1-8 — a well-connected graph grows fast past that. |
| `maxChildrenPerNode` | Default 25, clamped 1-200. A node past the cap keeps its own entry but stops expanding, marked `truncated:true` with `omittedChildren`. The children it left out are still **counted** in `blastRadius`, so the cap never hides a node at the depth it was found; but their own callers go unvisited, so at `maxDepth` above 1 a tighter cap does reduce the total. |
| `includeTree` | Default `true`. Set `false` for just `blastRadius` — the cheapest possible answer to "how much does changing this ripple." That shape is also the only one carrying a separate `root` block: with a tree, its head node **is** the root, and emitting both repeated every root field. |
| `fields` | Comma list adding `kind`, `file`, `line`, or `signature` (the full parameter-list `displayString` instead of the default type-and-member name) to every node beyond `symbolId` and, where the index can name the symbol, `displayString`. |

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
`truncated:true, omittedChildren:6` — under `includeTree:false`, since the shape whose entire purpose
is this number is not the shape that gets a worse one. **With the tree included**, this same root is
the tree's only truncated node, so `tree.truncated`/`tree.omittedChildren` already carry `true`/`6` and
`blastRadius` omits its own copies rather than repeating them.

Past depth 1 the equality stops holding, and that is a property of the graph walk rather than a
reporting gap: a node the cap did not expand contributes itself to the count but contributes none of
**its** callers, so the frontier is narrower at every level below. Measured on `Formats.Render`
(`maxDepth: 3`): `maxChildrenPerNode: 200` reaches 126 nodes, `maxChildrenPerNode: 2` reaches 23 and
reports `omittedChildren: 18`. Read a capped total as a floor, and never diff two runs whose caps
differ.

`depthCapped:true` means **the walk stopped short of exhausting the graph**, so depths past the stopping
point are unexplored. Two things cause that, and either sets the flag:

- a node at `maxDepth` still had neighbours — here, `Append` has callers beyond `maxDepth:1`; raising it
  reaches `PatchTools.ValidatePatch` (the actual MCP tool entry point) three levels up from this root;
- **a neighbour `maxChildrenPerNode` left out**, below `maxDepth`. Such a node is counted but never
  expanded, so everything past it is unexplored in exactly the way everything past `maxDepth` is.

The second used to be missing, so a walk that hid whole subtrees behind a tight `maxChildrenPerNode`
answered `depthCapped:false` — read as "complete to `maxDepth`", the one thing it was not. Read
`depthCapped:false` as "the graph was exhausted"; check `truncated`/`omittedChildren` to tell the two
causes apart when it is `true`.

A caller resolved through the edge cache but absent from the `symbols` table — a synthesized entry
point like C#'s top-level-statements `Main`, which `get_references` renders as
`<top-level-statements-entry-point>` via live Roslyn but the cache never stored a row for — has no
name to render, so that node **omits `displayString`** rather than failing the whole call. It used to
fall back to the bare `symbolId`, which stated one string under two keys and named nothing. Read a
node carrying `symbolId` and no `displayString` as exactly that case; `get_symbol` on the id is how to
find out what it is. Rare in practice — this repo hits it once, at `DevlogMigration.Run`'s own caller.

`get_call_slice`'s `forwardFrontier`/`backwardFrontier` are the one exception, and deliberately so:
they are bare strings with no `symbolId` field beside them, so an entry that dropped its unresolved
name would lose the node entirely. Those keep rendering the id.

A symbol reached through two different branches (a diamond) appears twice in the tree — the second
route in is real, so the node is not dropped — but its subtree is **expanded once**. The first
encounter renders it; every later one carries `repeated: true` and no `children`, and points back by
`symbolId` to the copy that did expand. Read `repeated: true` as "already stated above", never as a
leaf: a well-connected graph converges constantly, and re-printing the same child list verbatim under
each branch was the single largest avoidable cost in a deep tree. `blastRadius` is unaffected — it
counts what the walk *reached*, which is a property of the walk rather than of what survived
rendering, so the numbers are identical either way. True recursion (a symbol reappearing on its own
root-to-node path) stops as a leaf marked `recursive:true` rather than looping. Internally capped at a
few thousand total nodes as a safety net against pathological fan-out, independent of
`maxChildrenPerNode`.

### A named type as the root

A class, record, interface or delegate has no call sites of its own, so a type root's **depth-1 children
are the members that reference it** — the same set `get_references(direction: "callers")` returns on
that type — and the walk continues upward from those. This makes `includeTree: false` a one-call blast
radius for "how much does changing this type ripple", where before it reported
`totalUniqueNodes: 1` — the root and nothing else — for a type with dozens of referencing members:

```
get_call_hierarchy(symbol: "Indexing.TypeReferenceScan", maxDepth: 2, includeTree: false)
```

```
root:
  symbolId: sym_a25a...
  displayString: Indexing.TypeReferenceScan
direction: callers
blastRadius:
  totalUniqueNodes: 6
  perDepth[3]: 1,3,3
  depthCapped: true
```

The type seeds are resolved through Roslyn and cost a little more than the cached edge walk, so they are
computed **only** when the cheap walk found nothing — a member root never pays for them.

### A typo'd `direction`

`direction` accepts only `"callers"`/`"callees"`, and a value that matches neither falls back to
`"callers"` silently — the same failure mode `search_index`'s `kinds`/`modifiers` used to have,
except here a typo doesn't just find nothing, it finds the *opposite* of what was asked. When that
happens the response carries `directionHint`, e.g. for `direction: "callee"`:

```
directionHint: direction:'callee' was not recognized and defaulted to 'callers'. Did you mean 'callees'?
```

Absent whenever `direction` matched, so an ordinary call carries no extra field.

## Next steps

- **Need file/line/snippet per call site** → `get_references` — `get_references.md`
- **One hop on a symbol with only a handful of callers** → `get_references` is both cheaper and richer there. This tool's fixed overhead only pays off as fan-in grows: measured at 105 callers it cost 637 tokens against `get_references`' 5,266, but at a single caller 139 against 100 — and the 100 included file, line, snippet and `dispatchKind`.
- **Have a specific destination in mind** → `get_call_slice` is cheaper — `get_call_slice.md`
- **Judging a removal** → check `blastRadius`, then `get_symbol` on the survivors — `get_symbol.md`
