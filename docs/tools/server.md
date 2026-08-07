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

Call this when a semantic tool reports the workspace isn't ready, before trusting a `0` reference
count, **or to resolve where the plugin's own files live** — see `pluginRoot` below.

Real response, this repo:

```
root: /path/to/dotnet-toolkit
pluginRoot: /path/to/dotnet-toolkit
solution: dotnet-toolkit.slnx
index: ready 133 files, 220 types
workspace: loaded 2 projects in 2.3s
  loaded: DotnetToolkit.McpServer, DotnetToolkit.McpServer.Tests
```

`root` is the **target repository** being analysed. `pluginRoot` is where **the plugin itself** is
installed; in this repo they coincide, because the plugin analyses its own source tree.

## `pluginRoot` — the only way to reach the files that ship with the plugin

`${CLAUDE_PLUGIN_ROOT}` is substituted by the harness into `.mcp.json` args, hook commands and skill
content — but **not** into a rule file or an agent definition, and it is not exported as an
environment variable a shell can read. So an always-loaded rule cannot name a path to the plugin, and
neither can a subagent. `workspace_status` reports the value instead, derived at runtime
(`CLAUDE_PLUGIN_ROOT` if the harness set it, otherwise the directory above the running assembly —
both the server and the hooks run from `<pluginRoot>/dist/`). That makes it correct on any machine
and any plugin version, with nothing stored to go stale.

Join it to reach either shipped tree:

| Want | Path |
|---|---|
| A tool manual | `<pluginRoot>/docs/tools/<tool>.md` |
| A coding standard | `<pluginRoot>/standards/<name>.md` |

Each is the single location for that file. Nothing is copied into a consuming repo and there is no
per-repo override tier, so a resolved path is the answer — there is no second place to look.

Added in contract **3.50**. A server that omits the line predates it and needs republishing to
`dist/`; the `dotnet-read`/`dotnet-write` skills and `agents/dotnet-code-review.md` all treat its absence as a
reason to report standards-derived work as not-assessed rather than to guess a path.

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
