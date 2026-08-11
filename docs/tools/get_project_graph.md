# `get_project_graph` — which project references which

## When to reach for it

Which `.csproj` references which, and the reverse (`referencedBy`) — computed live from the
loaded solution every call, no caching (project counts are small). Pass `project` to scope to
one project's direct references and dependents instead of the whole graph.

```
get_project_graph()
```

```json
{"projects": [
   {"name": "DotnetToolkit.McpServer", "references": [], "referencedBy": ["DotnetToolkit.McpServer.Tests"]},
   {"name": "DotnetToolkit.McpServer.Tests", "references": ["DotnetToolkit.McpServer"], "referencedBy": []}],
 "totalProjectsInSolution": 2}
```

## Reference

Replaces: opening every `.csproj` and reading `<ProjectReference>` entries by hand. Computed live from
the loaded solution on every call, no caching — project counts are always small (tens, not thousands).

| Arg | Meaning |
|---|---|
| `project` | Optional project name to scope to one project's direct references + dependents. Omit for the whole graph. |

Real call and response:

```
get_project_graph()
```

```json
{"projects":[
   {"name":"DotnetToolkit.McpServer","references":[],"referencedBy":["DotnetToolkit.McpServer.Tests"]},
   {"name":"DotnetToolkit.McpServer.Tests","references":["DotnetToolkit.McpServer"],"referencedBy":[]}],
 "totalProjectsInSolution":2}
```

A project named in `workspace_status`'s load diagnostics carries `degraded:true` on its own entry, in
addition to the envelope-level `limitedBy:"degraded"`.

### An unknown `project`

A `project` that names nothing in the solution is `error: "project_not_found"`, alongside `projects` —
every project name the graph actually knows, the same closed set `refs.Keys` already holds for the
whole-graph response — and `didYouMean` naming the nearest one when exactly one is a close match:

```json
{"error": "project_not_found", "project": "DotnetToolkit.McpServr",
 "projects": ["DotnetToolkit.McpServer", "DotnetToolkit.McpServer.Tests"],
 "didYouMean": "DotnetToolkit.McpServer"}
```

## Next steps

- **Check for loops** → `detect_circular_dependencies` — `detect_circular_dependencies.md`
- **Scope a search to one project** → `search_index(pathPrefix: "...")` — `search_index.md`
