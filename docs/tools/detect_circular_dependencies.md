# `detect_circular_dependencies` — a real dependency loop, not just deep nesting

## When to reach for it

Cycles in the solution's project reference graph. `scope: "project"` (default, and for now the
only supported value) reports one representative cycle per strongly-connected component found
— not every distinct cycle within it, which can be combinatorial. `scope: "type"` returns
`error: "unsupported_scope"` rather than a partial answer: it would need collapsing
member-level call edges up to their containing type, which this server does not do today.

```
detect_circular_dependencies()
```

```json
{"scope": "project", "cycles": [], "totalCycles": 0}
```

An empty `cycles` array is a checked "found none," not silence — this repo has no known
project cycles today.

## Reference

Replaces: manually tracing project references looking for a loop. Cycles in the solution's project
reference graph via Tarjan's SCC.

| Arg | Meaning |
|---|---|
| `scope` | `project` (default, and for now the only supported value) \| `type` — returns `error:"unsupported_scope"` rather than a partial answer; type-level cycle detection would need collapsing member-level call edges up to their containing type, which this server does not do today. Any other value is also `unsupported_scope`, but the `message` tells the two apart: `type` gets the "not yet implemented" text above, while a typo or plural of `project` (e.g. `"projects"`) gets a message naming the value that wasn't recognized, plus a `didYouMean` toward `project` — it isn't asking for a feature that doesn't exist, so it doesn't get told it is. |

Reports one representative cycle per strongly-connected component found — not every distinct cycle
within it, which can be combinatorial.

```
detect_circular_dependencies()
```

```json
{"scope":"project","cycles":[],"totalCycles":0}
```

An empty `cycles` array is a checked "found none," not silence — this repo has no known project
cycles today.

## Next steps

- **See the whole reference graph** → `get_project_graph` — `get_project_graph.md`
- **Trace the members creating an edge** → `get_call_slice` between the two projects' types — `get_call_slice.md`
