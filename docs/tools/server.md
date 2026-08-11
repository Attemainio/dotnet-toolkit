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

# `set_hook_guards` — suspend the C# guards, briefly

| Arg | Meaning |
|---|---|
| `state` | `suspend` \| `restore`. |
| `minutes` | How long to suspend for. Default 30, capped at 240. Ignored when restoring. |

The `PreToolUse` guards block `Read`, `Edit`/`Write` and shell reads (`cat`/`grep`/`sed`/…) on a
compiled `.cs` file. This turns them off for a bounded window.

**It is not the way past a guard that is in your way.** A guard's denial names the skill covering what
you were trying to do, and that route is both cheaper and recorded; suspending the guards to do the
same work by hand trades a better tool for a worse one. The legitimate uses are the ones where the
unguarded path *is* the subject: measuring these tools against `grep`/`Read`, or reproducing what a
repo without the plugin does.

What a suspension actually costs, and why it is worth stating before taking one: an edit made through
raw `Edit`/`Write` reaches disk **without compiling**, without a dependent-compile check against its
callers, and **without a development-log entry** — so `search_log` cannot recover why it was made once
the session ends.

A suspension is stored as an **expiry, not a flag**. The state file under
`.claude/dotnet-toolkit/cache/` names the instant the guards resume, any read past that instant deletes
it, and the cap bounds what can be asked for — so there is no way to disable the guards indefinitely
from here, and nothing has to remember to undo it. While one is in force, `workspace_status` prints a
`hookGuards: SUSPENDED` line with the time remaining; that line is absent when the guards are active,
so it costs nothing in the normal case and is impossible to miss in the abnormal one.

`DOTNET_TOOLKIT_DISABLE_HOOKS` is the separate, non-expiring escape hatch, for a harness that owns the
whole process lifetime (CI, a benchmark runner) where "until this process exits" is already a bound.
`restore` cannot clear it — it lives in the server's own environment — and says so rather than
reporting a restore it did not perform.

## Next steps

- **`workspace_status` says `index_only` or `stale`** → `reload_workspace`, then re-fetch.
- **`degraded`** → fix the build first; results may be silently wrong, not just thin.
- **`hookGuards: SUSPENDED` and you did not ask for it** → `set_hook_guards(state: "restore")`; an
  earlier session in this repo left one running, and until it lapses your `.cs` edits are unrecorded.
