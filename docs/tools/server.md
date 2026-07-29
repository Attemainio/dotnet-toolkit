# Server tools

Four tools that answer about the server itself rather than the code. None record telemetry.

# `ping`

Health check. `Ping()` → `"pong dotnet-toolkit/0.1.0"`. No arguments.

# `set_output_format` — change how responses are encoded

| Arg | Meaning |
|---|---|
| `format` | `json` (pretty-printed) \| `compact` (minified JSON) \| `toon` (default). |

Takes effect immediately and persists for the rest of the session (until changed again or the server
restarts). Returns a plain confirmation string, not a JSON/TOON envelope — e.g.
`set_output_format(format: "json")` → `"output format set to json"`. An unrecognized `format` is reported
back rather than silently defaulting: `"unknown format: yaml (use json|compact|toon)"`.

# `workspace_status` — is the index/workspace warm

Call this when a semantic tool reports the workspace isn't ready, or before trusting a `0` reference
count.

Real response, this repo:

```
root: /path/to/dotnet-toolkit
solution: dotnet-toolkit.slnx
index: ready 83 files, 134 types
workspace: loaded 2 projects in 2.6s
  loaded: DotnetToolkit.McpServer, DotnetToolkit.McpServer.Tests
```

A degraded workspace names the failing project — reference edges from a project MSBuild couldn't
evaluate contribute nothing, and semantic results from it are incomplete or wrong, not just thin.

# `reload_workspace` — force a re-scan

| Arg | Meaning |
|---|---|
| `scope` | `index` (re-scan file index) \| `workspace` (re-open the MSBuild solution, then rebuild the SQLite symbol index/project graph) \| `all` (default). |

Call after a large external change the mtime-poller might not have caught yet in time — a `git
checkout`, a `git pull`, a rebase, or any `.cs` edit made outside `validate_patch`. `workspace`/`all`
queues a background `SymbolIndexBuilder` rebuild after reopening the solution (the same mechanism a
successful `validate_patch` apply triggers), so `search_index`/`get_references` catch up on the new
symbols and edges without waiting for the next periodic full sweep. That rebuild runs in the
background — `reload_workspace` returns before it finishes; check `workspace_status` if you need to
confirm it's done.

## Next steps

- **`workspace_status` says `index_only` or `stale`** → `reload_workspace`, then re-fetch.
- **`degraded`** → fix the build first; results may be silently wrong, not just thin.
