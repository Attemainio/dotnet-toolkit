# Hook reference

The plugin ships three hooks in `hooks/hooks.json`. They travel with the plugin — a consuming repo gets
the enforcement from installation alone, with nothing repo-local to set up or clean up; uninstalling the
plugin removes them. All three read their JSON payload through whichever of `node`, `python3`, or `jq` is
present (none is guaranteed — `jq` is absent on this repo's own dev box, and Claude Code's native
installer means `node` cannot be assumed) and **fail open** (allow the call) if none is available.

## `guard-cs-edit.sh` — PreToolUse on `Edit`/`Write`/`NotebookEdit`

Blocks `Edit`/`Write`/`NotebookEdit` on an **existing** `.cs` file and returns the `validate_patch`
procedure in the deny message instead. This enforces the write path: applying through `validate_patch`
with an `intent` is the only thing that appends to the development log, so an edit made with `Edit` is a
change whose reasoning is unrecoverable once the conversation ends. A blocked edit is the hook working,
not a bug — rebuild the change as `validate_patch` calls.

Creating a **new** `.cs` file with `Write` is allowed, because `validate_patch`'s `baseVersions` needs a
`symbolId` that does not exist yet; change the file through `validate_patch` after creation.

The deny message restates the current `validate_patch` call procedure — when that procedure changes, this
script's message must change with it (see CLAUDE.md's "Changing the tool surface" table).

## `guard-cs-read.sh` — PreToolUse on `Read`

Blocks `Read` on a `.cs` file that a project actually compiles, in favor of `search_index`/`get_symbol` —
the read-side counterpart of the edit guard.

Solution membership is decided in the hook itself, from the filesystem alone: a hook is a separate
process with no access to the MCP stdio pipe, so it cannot ask the running server's `WorkspaceHost`
whether a file belongs to the loaded solution. What it checks statically:

- Walk upward from the file for the nearest `.csproj`, watching for a `*.sln`/`*.slnx` at a level
  **strictly between** the file and the repo root — finding one there means the file belongs to its own
  independent, nested solution (a test fixture's throwaway sample project, for example), so the read is
  **allowed**. Reaching the repo root itself (where this repo's own top-level `.slnx` lives) is the
  ordinary case and is not treated as nested.
- If a governing `.csproj` is found, its `<Compile Remove>` globs are checked too — a file excluded from
  compilation (the way `DotnetToolkit.McpServer.Tests.csproj` excludes `fixtures/**`) is **allowed**.

This is a heuristic, not an MSBuild evaluation: conditions, multi-targeting, and unusual glob forms
aren't handled, and it cannot see runtime state — a file genuinely governed by a project is still blocked
even while the server's workspace is `index_only`/degraded, because that is state a static check has no
way to observe.

## `hint-reload-new-cs-file.sh` — PostToolUse on `Write`

Fires when a `Write` creates a brand-new `.cs` file (the one case the edit guard allows through). Both
knowledge tiers are mtime-polling, not filesystem watchers, so a new file is invisible to the syntax
index and the MSBuild workspace until a sweep and reload complete — a `validate_patch`/`get_symbol` call
against it before then fails deterministically with `invalid_edit: file is not part of the loaded
solution`. The hook cannot call `reload_workspace` itself (no MCP pipe access), so it injects an
`additionalContext` reminder telling Claude to call `reload_workspace(scope: "all")` before the next
call touches the new file — the reminder lands at file-creation time rather than after a confusing
failure.

The JSON reply is built by the same interpreter that parsed the payload rather than hand-interpolated,
since `file_path` is caller-controlled text (a Windows path's backslashes) that has no business near
manual JSON string escaping.

## Related scripts (not hooks)

`scripts/run-server.sh` launches the MCP server (registered via `.mcp.json`), preferring a user-local
`~/.dotnet` install over `dotnet` on `PATH`. `scripts/build-plugin.sh` publishes the server to `dist/` —
required after any change under `src/` for the plugin to serve the new build.
