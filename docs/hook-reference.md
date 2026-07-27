# Hook reference

The plugin ships four hooks in `hooks/hooks.json`. They travel with the plugin — a consuming repo gets
the enforcement from installation alone, with nothing repo-local to set up or clean up; uninstalling the
plugin removes them. All four read their JSON payload through whichever of `node`, `python3`, or `jq` is
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

Solution membership is decided from the filesystem alone, in `scripts/lib-cs-membership.sh` (shared with
`guard-cs-bash-read.sh` below, so the two can never disagree on the answer): a hook is a separate process
with no access to the MCP stdio pipe, so it cannot ask the running server's `WorkspaceHost` whether a
file belongs to the loaded solution. What it checks statically:

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

## `guard-cs-bash-read.sh` — PreToolUse on `Bash`

Closes the gap `guard-cs-read.sh` structurally cannot: its matcher is the `Read` tool by name, so a shell
command that dumps the same file's bytes into the transcript via `Bash` (`cat`, `sed`, `head`, `tail`,
`grep`/`egrep`/`fgrep`/`rg`/`ag`, `awk`/`gawk`, `nl`, `tac`, `bat`, or `less`/`more`) is invisible to it —
a different tool name, same underlying read. This hook watches `Bash` instead and applies the identical
membership question via the same shared `scripts/lib-cs-membership.sh`.

It is not a shell parser: it splits the command on pipeline/statement separators (`| ; && ||`), takes
each segment's first word as the invoked command, and — only for a segment whose command is in the
blocklist above (overridable via `DOTNET_TOOLKIT_READ_BLOCKLIST`) — looks for a bare `.cs`-suffixed
argument token. Quoted paths with spaces, variable-expanded paths, and heredocs are not recognized; that
under-detection is deliberate (same fail-open posture as the other guards), not a security boundary meant
to be airtight. `git`, `dotnet`, `find`, and anything not in the blocklist are never touched, so
`git diff -- Foo.cs`, `git log Foo.cs`, and `find . -name '*.cs'` are all unaffected.

**The built-in `Grep` tool is not covered by either read guard.** `hooks/hooks.json` matches on tool
name, and its `PreToolUse` entries are `Edit|Write|NotebookEdit`, `Read`, and `Bash` — `Grep` and `Glob`
are matched by none of them, so `Grep` with `-A`/`-B`/`-C` or in content mode returns `.cs` source with
nothing intercepting it. This is a real hole in read enforcement, not a case the membership check
allows: `search_index` is still the right tool for finding a declared symbol, and the standing
instruction in CLAUDE.md covers it, but no hook enforces that here. It matters most for
`dotnet-code-review`, whose `tools:` list grants `Grep` and `Glob` outright.

## `hint-reload-new-cs-file.sh` — PostToolUse on `Write`

Fires when a `Write` creates a brand-new `.cs` file (the one case the edit guard allows through). Both
knowledge tiers are mtime-polling, not filesystem watchers, so a new file is invisible to the syntax
index and the MSBuild workspace until a sweep and reload complete — a `validate_patch`/`get_symbol` call
against it before then fails deterministically with `invalid_edit: file is not part of the loaded
solution`.

A hook process has no access to the MCP stdio pipe, so it cannot call `reload_workspace` through the
running session — but the server also exposes `Control/ControlServer.cs`, a loopback TCP listener on
`127.0.0.1` started alongside the other background services, whose port is published as plain text at
`CacheDir/control.port`. This hook reads that port, sends `rescan` (synchronous — a syntax-index sweep,
no MSBuild — the hook waits for the result) and then `reload` (fire-and-forget — starts the background
MSBuildWorkspace reload and returns immediately, since that can run far longer than the hook's timeout),
and reports both results in the injected `additionalContext`, telling Claude to check `workspace_status`
before the next `validate_patch`/`get_symbol` call on the new file.

Falls back to the old reminder-only text — "call `reload_workspace(scope: "all")` and wait for
`workspace_status`" — if the control channel is unreachable for any reason (a server built before this
feature existed, a missing port file, a refused connection, no response within the timeout). Fails open
the same way every other hook here does.

The JSON reply is built by the same interpreter that parsed the payload rather than hand-interpolated,
since `file_path` is caller-controlled text (a Windows path's backslashes) that has no business near
manual JSON string escaping.

## Related scripts (not hooks)

`scripts/lib-cs-membership.sh` is the shared solution-membership check sourced by `guard-cs-read.sh` and
`guard-cs-bash-read.sh` — not registered in `hooks/hooks.json` itself, since it has no `PreToolUse`/
`PostToolUse` entry of its own. `scripts/run-server.sh` launches the MCP server (registered via
`.mcp.json`), preferring a user-local `~/.dotnet` install over `dotnet` on `PATH`. `scripts/build-plugin.sh`
publishes the server to `dist/` — required after any change under `src/` for the plugin to serve the new
build.
