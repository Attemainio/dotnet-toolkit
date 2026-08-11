# Hook design notes

> **Maintainer-facing. Nothing routes here, and nothing should** — hooks fire from
> `hooks/hooks.json` and their deny messages carry the instruction at the moment it is needed. This
> page exists for the engineering rationale: why the guards are .NET subcommands rather than shell,
> how membership is decided statically, and where enforcement has a known hole.

The plugin ships five hooks in `hooks/hooks.json`. They travel with the plugin — a consuming repo gets
the enforcement from installation alone, with nothing repo-local to set up or clean up; uninstalling the
plugin removes them.

Every hook is a subcommand of the published server binary, invoked as
`dotnet "${CLAUDE_PLUGIN_ROOT}/dist/DotnetToolkit.McpServer.dll" hook <name>`, implemented under
`src/DotnetToolkit.McpServer/Hooks/`. That is the whole cross-platform story: the plugin already
requires the .NET runtime, so a hook needs no shell, no shebang, and no JSON interpreter beyond
`System.Text.Json`.

> **Why they aren't shell scripts any more.** They were, until the plugin was tested against a
> Windows-installed Claude Code. Two failures, both structural: a `.sh` file with a shebang cannot be
> executed where `bash` is not the shell, and the scripts' `node` → `python3` → `jq` JSON-extraction
> chain found none of the three — `python3` resolved to the Microsoft Store alias stub, which exits 0
> and prints nothing, so extraction "succeeded" with empty fields and every guard fell through to its
> allow branch. The guards were designed to fail open on a *missing* interpreter; a *stubbed* one made
> them fail open silently while looking healthy.

They still **fail open** by design: an unparseable payload, an unresolvable project root, or an
unexpected exception exits 0 (allow). These are workflow guards, not a security boundary, and must
never wedge the user's editing. Denial is exit code 2 plus stderr, so no guard needs a JSON *writer* on
the path that has to be reliable.

Each hook adds roughly 70ms per matched tool call — one short-lived .NET process that returns before
MSBuild discovery or host startup runs.

## The off-switch, and why it expires

`HookCli` reads `GuardSuspension` before dispatching to any of the three **blocking** guards; when a
suspension is in force they return allow without evaluating. The two hints are unaffected — they add
context rather than withhold a call, so silencing them would cost the caller information without
buying back any of the freedom a suspension is asked for.

The reason this exists at all is `dotnet-performance`: the claim that these tools beat `grep`/`Read`
cannot be *measured* without running `grep`/`Read` in the same repo, and the guards make that route
unreachable by construction. A benchmark that cannot run the baseline is not a benchmark.

Three properties carry the risk; the first two are deliberate inversions of what the rest of this page
says:

- **State is an expiry, not a flag.** The file under `.claude/dotnet-toolkit/cache/` holds the instant
  the guards resume; any read past it deletes the file, and requests are capped at 4 hours. So there is
  no way to disable the guards indefinitely through `set_hook_guards`, and nothing has to remember to
  undo it. This matters more than it would for most switches: a guard left off does not fail loudly,
  it just stops recording work, and the repo then looks exactly as it does when the plugin was never
  installed.
- **Reading the state fails *closed*.** An unreadable or unparseable state file leaves the guards on,
  which is the opposite of the fail-open rule above. Both are the same principle applied to different
  risks — failing open keeps a broken guard from wedging the user's editing; failing closed keeps a
  broken state file from silently disarming a guard that works.
- **State is scoped to the calling Claude Code session, when one is visible.** `set_hook_guards` reads
  `CLAUDE_CODE_SESSION_ID` from its own process environment and writes
  `guards-suspended-until.<sessionId>` instead of the bare `guards-suspended-until` file; `HookCli`
  checks that scoped file first (via `HookPayload.SessionId`, the same field `hint-write-checklist`
  already reads) and falls back to the unscoped file. This exists so two *unrelated* Claude Code
  sessions pointed at the same repo root — two terminals, two worktrees — cannot silently share or
  clobber each other's suspension window. It does **not** isolate a subagent from its own parent
  session: `CLAUDE_CODE_SESSION_ID` and `CLAUDE_CODE_CHILD_SESSION` were confirmed by direct
  observation to hold the identical value for a subagent and the session that spawned it, so scoping
  lands at the top-level session, not at the individual agent. A caller with no session id in its
  environment reads and writes the unscoped file exactly as every caller did before this existed, and
  every scoped check still falls back to that same file, so an older unscoped suspension stays honoured
  rather than going silently invisible.

`workspace_status` prints a `hookGuards: SUSPENDED` line with the time remaining, and nothing when the
guards are active — checking its own session's scoped state the same way `HookCli` does. It carries
this rather than `set_hook_guards` alone because it is the call every skill makes first, so a session
inheriting a suspension it did not start finds out before it edits rather than after.

`DOTNET_TOOLKIT_DISABLE_HOOKS` remains the separate, non-expiring hatch for a harness that owns the
process lifetime. `set_hook_guards(state: "restore")` cannot clear it — it lives in the server's own
environment — and reports that rather than claiming a restore it did not perform.

## `hook guard-cs-edit` — PreToolUse on `Edit`/`Write`/`NotebookEdit`

Blocks `Edit`/`Write`/`NotebookEdit` on an **existing** `.cs` file and returns the `validate_patch`
procedure in the deny message instead. This enforces the write path: applying through `validate_patch`
with an `intent` is the only thing that appends to the development log, so an edit made with `Edit` is a
change whose reasoning is unrecoverable once the conversation ends. A blocked edit is the hook working,
not a bug — rebuild the change as `validate_patch` calls.

Creating a **new** `.cs` file with `Write` is allowed, because `validate_patch`'s `baseVersions` needs a
`symbolId` that does not exist yet; change the file through `validate_patch` after creation.

**A `.cs` file no project compiles is also allowed**, decided by the same `Hooks/CsFileMembership.cs`
the read guards use (below). `validate_patch` resolves edits through the loaded solution and answers
`file_not_in_solution` for anything outside it, so denying the plain tool there left **no write path at
all** — the guard was pure obstruction, and its own escape hatch ("ask the user to allow it explicitly")
was the only way through. This repo's `tests/**/fixtures/SampleSolution` is exactly that case: a nested
solution deliberately excluded from the build so tests can load it as a workspace of their own. The read
guard has always gated on membership; the edit guard did not, so the same file was readable and
uneditable.

The deny message restates the current `validate_patch` call procedure, and points a **pure rename** at
`rename_symbol` instead — hand-authoring call-site edits misses interface, virtual and delegate dispatch,
which is exactly the mistake a blocked `Edit` is usually about to make. When either procedure changes,
`Hooks/GuardCsEdit.cs`'s message must change with it (see `docs/design/architecture.md`'s "Changing the tool
surface").

## `hook guard-cs-read` — PreToolUse on `Read`

Blocks `Read` on a `.cs` file that a project actually compiles, in favor of `search_index`/`get_symbol` —
the read-side counterpart of the edit guard.

Solution membership is decided from the filesystem alone, in `Hooks/CsFileMembership.cs` (shared with
`guard-cs-bash-read` below, so the two can never disagree on the answer): a hook is a separate process
with no access to the MCP stdio pipe, so it cannot ask the running server's `WorkspaceHost` whether a
file belongs to the loaded solution. What it checks statically:

- Walk upward from the file for the nearest `.csproj`, watching for a `*.sln`/`*.slnx` at a level
  **strictly between** the file and the repo root — finding one there means the file belongs to its own
  independent, nested solution (a test fixture's throwaway sample project, for example), so the read is
  **allowed**. Reaching the repo root itself (where this repo's own top-level `.slnx` lives) is the
  ordinary case and is not treated as nested.
- If a governing `.csproj` is found, its `<Compile Remove>` globs are checked too — a file excluded from
  compilation (the way `DotnetToolkit.McpServer.Tests.csproj` excludes `fixtures/**`) is **allowed**.
- A file that is not under the project root at all is **allowed** without walking, so the climb can
  never escape past the root into whatever `.sln`/`.csproj` happens to sit above it on the filesystem.

Path comparison follows the filesystem's own case rules — ordinal on Linux, case-insensitive elsewhere —
so the walk terminates at the root on Windows and macOS even when the two paths disagree on casing.

This is a heuristic, not an MSBuild evaluation: conditions, multi-targeting, and unusual glob forms
aren't handled, and it cannot see runtime state — a file genuinely governed by a project is still blocked
even while the server's workspace is `index_only`/degraded, because that is state a static check has no
way to observe.

## `hook guard-cs-bash-read` — PreToolUse on `Bash`

Closes the gap `guard-cs-read` structurally cannot: its matcher is the `Read` tool by name, so a shell
command that dumps the same file's bytes into the transcript via `Bash` (`cat`, `sed`, `head`, `tail`,
`grep`/`egrep`/`fgrep`/`rg`/`ag`, `awk`/`gawk`, `nl`, `tac`, `bat`, or `less`/`more`) is invisible to it —
a different tool name, same underlying read. This hook watches `Bash` instead and applies the identical
membership question via the same shared `Hooks/CsFileMembership.cs`.

It is not a shell parser: `Hooks/BashCommandScanner.cs` splits the command on pipeline/statement
separators (`| ; & && ||`), takes each segment's first word as the invoked command — with any directory
and a trailing `.exe` stripped — and, only for a segment whose command is in the blocklist above
(overridable via `DOTNET_TOOLKIT_READ_BLOCKLIST`), looks for a bare `.cs`-suffixed argument token.

**Separators inside quotes or backslash-escaped are not separators.** This matters more than it sounds:
splitting `grep -n "Alpha\|Beta" Foo.cs | head` textually pushes the `.cs` path into a segment starting
`Beta"`, which is not a read utility, while the segment starting `grep` carries no path — so a
multi-term grep, the most common way to search several terms at once, read compiled C# unguarded. Fixed
by a quote/escape-aware scan, covered by `BashCommandScannerTests`.

Quoted paths with spaces, variable-expanded paths, and heredocs are still not recognized; that
under-detection is deliberate (same fail-open posture as the other guards), not a security boundary meant
to be airtight. `git`, `dotnet`, `find`, and anything not in the blocklist are never touched, so
`git diff -- Foo.cs`, `git log Foo.cs`, and `find . -name '*.cs'` are all unaffected.

**The built-in `Grep` tool is not covered by either read guard.** `hooks/hooks.json` matches on tool
name, and its `PreToolUse` entries are `Edit|Write|NotebookEdit`, `Read`, and `Bash` — `Grep` and `Glob`
are matched by none of them, so `Grep` with `-A`/`-B`/`-C` or in content mode returns `.cs` source with
nothing intercepting it. This is a real hole in read enforcement, not a case the membership check
allows: `search_index` is still the right tool for finding a declared symbol, and the always-loaded
the `dotnet-read`/`dotnet-write` skills cover it (in this repo and in every repo init wired up), but
no hook enforces that here. It matters most for
`dotnet-code-review`, whose `tools:` list grants `Grep` and `Glob` outright. `dotnet-explore` closes the
hole the other way — it is granted neither, and its own instructions forbid `Read` on a `.cs` file at
all, so for that agent the read guard is a backstop rather than the boundary.

## `hook hint-reload-new-cs-file` — PostToolUse on `Write`

Fires when a `Write` creates a brand-new `.cs` file (the one case the edit guard allows through). Both
knowledge tiers are mtime-polling, not filesystem watchers, so a new file is invisible to the syntax
index and the MSBuild workspace until a sweep and reload complete — a `validate_patch`/`get_symbol` call
against it before then fails deterministically with `invalid_edit: file is not part of the loaded
solution`.

A hook process has no access to the MCP stdio pipe, so it cannot call `reload_workspace` through the
running session — but the server also exposes `Control/ControlServer.cs`, a loopback TCP listener on
`127.0.0.1` started alongside the other background services, whose port is published as plain text at
`CacheDir/control.port`. `Hooks/ControlClient.cs` reads that port, sends `rescan` (synchronous — a
syntax-index sweep, no MSBuild — the hook waits for the result) and then `reload` (fire-and-forget —
starts the background MSBuildWorkspace reload and returns immediately, since that can run far longer
than the hook's timeout), and reports both results in the injected `additionalContext`, telling Claude
to check `workspace_status` before the next `validate_patch`/`get_symbol` call on the new file.

Falls back to the reminder-only text — "call `reload_workspace(scope: "all")` and wait for
`workspace_status`" — if the control channel is unreachable for any reason (a server built before this
feature existed, a missing port file, a refused connection, no response within the timeout). Fails open
the same way every other hook here does.

The JSON reply is serialized, never hand-interpolated, since `file_path` is caller-controlled text (a
Windows path's backslashes) that has no business near manual JSON string escaping.

## `hook hint-write-checklist` — PreToolUse on the `validate_patch` MCP tool

The only hook here that matches an MCP tool rather than a built-in one, and the only one that is not a
guard: it never denies.

Every other hook is **retrospective** — it fires once `Edit`/`Write`/`Bash` has already been reached
for, which is exactly the wrong tool. A caller who does the right thing and goes straight to
`validate_patch`, but without invoking `dotnet-write`, trips none of them, so a checklist carried in
that skill's body would never arrive. This hook closes that gap by putting it in front of the caller at
the moment it applies, which is also why it does not have to be pre-loaded: the delivery is pull-free.
**The hook is the checklist's only owner** — `skills/dotnet-write/SKILL.md` points here and deliberately
keeps no copy, since a copy would drift and would still miss the caller this exists for.

**Once per session, not once per call.** A checklist repeated on every patch of a long editing task is
noise, and noise gets ignored. `Hooks/WriteChecklistHint.cs` dedupes on a marker file under the OS temp
directory named for `HookPayload.SessionId` (added to the payload parser for this hook), claimed with
`FileMode.CreateNew` — so the claim is atomic and two agents sharing one session still produce exactly
one checklist between them. The marker lives in temp rather than the repo deliberately: a consuming
repo should not accumulate per-session files.

**No session id means silence.** Without one there is no way to tell a first call from a fiftieth, and
emitting anyway would spam every patch in the session. The always-loaded rule and the `dotnet-write`
skill remain the primary carriers; this hook is a backstop, so failing quiet is correct. Every IO
failure — an existing marker, an unwritable temp directory — takes the same path.

The tool-name check is repeated inside `HookCli`'s switch arm (`EndsWith("__validate_patch")`) rather
than trusted from `hooks.json` alone, matching how the `guard-cs-*` arms re-check their own tool names.

## Related files

`scripts/build-plugin.sh` and `scripts/build-plugin.cmd` publish the server to `dist/` — required after
any change under `src/` for the plugin to serve the new build. Both are thin wrappers over
`dotnet publish src/DotnetToolkit.McpServer -c Release -o dist`, which is the canonical command; they are
developer conveniences, and nothing the plugin ships at runtime executes a shell.

`scripts/format-json.cs` is a standalone `dotnet run` file-based program (not part of any project, so the
read guards leave it alone) that pretty-prints one JSON file — for inspecting the compact caches under
`.claude/dotnet-toolkit/cache/` without a Python dependency:
`dotnet run scripts/format-json.cs -- <path>`. Nothing invokes it automatically.
