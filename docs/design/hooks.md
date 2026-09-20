# Hook design notes

> **Maintainer-facing. Nothing routes here, and nothing should** — hooks fire from
> `hooks/hooks.json` and their deny messages carry the instruction at the moment it is needed. This
> page exists for the engineering rationale: why the guards are .NET subcommands rather than shell,
> how membership is decided statically, and where enforcement has a known hole.

The plugin ships seven hooks in `hooks/hooks.json`. They travel with the plugin — a consuming repo gets
the enforcement from installation alone, with nothing repo-local to set up or clean up; uninstalling the
plugin removes them.

Every hook is a subcommand of the published server binary, invoked as
`dotnet "${CLAUDE_PLUGIN_ROOT}/dist/DotnetToolkit.McpServer.dll" hook <name>`, implemented under
`src/DotnetToolkit.McpServer/Hooks/`. That is the whole cross-platform story: the plugin already
requires the .NET runtime, so a hook needs no shell, no shebang, and no JSON interpreter beyond
`System.Text.Json`. Codex sets the same `CLAUDE_PLUGIN_ROOT` compatibility variable for plugin hooks,
so the command is shared rather than duplicated with a host-specific path.

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

## The off-switch, and the forced-on switch, and why both expire

`HookCli` reads `GuardSuspension` before dispatching to either of the two **guards**
(`guard-cs-read`, `guard-cs-bash-read`) and asks which of three modes (`GuardMode`) is in force.
**`Suspended`** short-circuits before either evaluates — they return allow without running at all.
**`Blocked`** does not short-circuit anything; it is passed into them as a parameter that flips their
outcome from `AllowAdvising` to `Deny` when the file is governed and the read is a whole-file one — a
ranged read passes silently in every mode, since it is the intended way to fetch a located symbol's
text. Until 2026-09-19 two write guards (`guard-cs-edit`, `guard-cs-bash-write`) hard-denied
`Edit`/`Write` and write-shaped `Bash` on a compiled `.cs` file, routing every edit through the
plugin's own patch tool; that tool and both guards are gone (see the write-path note below). The
hints, the refresh and the meter are unaffected by either
mode — they add context or observe, rather than withhold a call, so silencing them would cost the
caller information without buying back any of the freedom a suspend or a block is asked for. The meter
especially: `dotnet-performance` suspends or blocks the guards precisely in order to measure a
*specific* route, which is not measured at all if changing the state also stops the measuring.

The reason `Suspended` exists at all is `dotnet-performance`'s floor probe: the claim that these tools
beat `grep`/`Read` cannot be *measured* without running `grep`/`Read` in the same repo, and the guards
make that route unreachable by construction. A benchmark that cannot run the baseline is not a
benchmark. `Blocked` exists for the same skill's ceiling probe, added 2026-08-22: it reproduces this
repo's own pre-2026-08-20 read enforcement as a comparison point, and doubles as a backstop against
`dotnet-perf-mcp-probe`'s one narrow `Read` allowance turning a protocol slip into silent contamination
instead of a loud, reportable denial.

Three properties carry the risk; the first two are deliberate inversions of what the rest of this page
says:

- **State is an expiry, not a flag.** The file under `.claude/dotnet-toolkit/cache/` holds which mode
  (`Suspended` or `Blocked`) is in force and the instant it lapses; any read past that instant deletes
  the file, and requests are capped at 4 hours. So there is no way to hold either mode indefinitely
  through `set_hook_guards`, and nothing has to remember to undo it. This matters more for `Suspended`
  than for most switches: left on, it does not fail loudly, it just stops recording work, and the repo
  then looks exactly as it does when the plugin was never installed. A stuck `Blocked` fails the other
  way — loudly, as an ordinary `Read` unexpectedly denying — which is a real cost of its own (a
  confused session) even though it is the safer of the two directions to get stuck in.
- **Reading the state fails *closed*.** An unreadable or unparseable state file leaves the guards at
  their default (`Active`), which is the opposite of the fail-open rule above. Both are the same
  principle applied to different risks — failing open keeps a broken guard from wedging the user's
  editing; failing closed keeps a broken state file from silently disarming a guard that works. A file
  in the pre-`Blocked` format (a bare timestamp, no mode line) is not "unreadable" for this purpose —
  `GuardSuspension.TryRead` treats it as `Suspended`, the mode every such file predates.
- **State is scoped to the calling Claude Code session, when one is visible.** `set_hook_guards` reads
  `CLAUDE_CODE_SESSION_ID` from its own process environment and writes
  `guards-suspended-until.<sessionId>` instead of the bare `guards-suspended-until` file; `HookCli`
  checks that scoped file first (via `HookPayload.SessionId`, the same field `hint-cs-write-checklist`
  already reads) and falls back to the unscoped file. This exists so two *unrelated* Claude Code
  sessions pointed at the same repo root — two terminals, two worktrees — cannot silently share or
  clobber each other's suspension window. It does **not** isolate a subagent from its own parent
  session: `CLAUDE_CODE_SESSION_ID` and `CLAUDE_CODE_CHILD_SESSION` were confirmed by direct
  observation to hold the identical value for a subagent and the session that spawned it, so scoping
  lands at the top-level session, not at the individual agent. A caller with no session id in its
  environment reads and writes the unscoped file exactly as every caller did before this existed, and
  every scoped check still falls back to that same file, so an older unscoped suspension stays honoured
  rather than going silently invisible.
- **The server's own session id goes stale, so a hook's outranks it.** The server process reads
  `CLAUDE_CODE_SESSION_ID` once, at launch, and holds it for life; a hook process is spawned per tool
  call and always carries the current one. Resume or continue a session without restarting the server
  and the two diverge — the server then writes `guards-suspended-until.<stale-id>`, no hook ever looks
  that name up, the scoped check misses, the unscoped fallback does not exist, and the guards stay
  armed. This is not hypothetical: it cost the 2026-08-13 performance run its entire raw route, and it
  is the *worst* shape of failure, because `set_hook_guards` returns success and `workspace_status`
  corroborates it. Both report the server's view of a file it wrote; neither observes a hook.
  `GuardSuspension.ObserveSessionId` closes it — `meter-tool-call` sends the id **it** resolves
  (`guardSessionId`, from its own environment, so the value is the one the guard hooks will look up
  by construction rather than by assumption), `ControlServer` records it, and `CurrentSessionId()`
  prefers it over this process's inherited value. Until some hook has reported one,
  `SessionIdIsConfirmed` is false and `set_hook_guards` also writes the unscoped file and says so in
  its scope sentence — a wider suspension the caller is told about beats a scoped one that silently
  does nothing.

`workspace_status` prints a `hookGuards: SUSPENDED` or `hookGuards: BLOCKED` line with the time
remaining, and nothing when the guards are at their default — checking its own session's scoped state
the same way `HookCli` does. It carries this rather than `set_hook_guards` alone because it is the call
every skill makes first, so a session inheriting a suspend or a block it did not start finds out before
it edits (or before an unexpected `Read` denial confuses it) rather than after. **Neither line is
evidence that a hook will honour the state**, for the reason above; `dotnet-performance` therefore
proves each transition with an actual guarded call before launching the probe that depends on it — an
advisory-free `Read` before the raw probe, an actual denial before the MCP probe — rather than trusting
either instrument alone.

`DOTNET_TOOLKIT_DISABLE_HOOKS` remains the separate, non-expiring hatch for a harness that owns the
process lifetime. `set_hook_guards(state: "restore")` cannot clear it — it lives in the server's own
environment — and reports that rather than claiming a restore it did not perform.

## The advisory carries a number (2026-09-20)

The first FluidScript run on 2.0.0 had the full probe make thirteen whole-file reads, see the advisory
on each, and override it each time without comment. The note restated a policy; a fourteenth copy of a
policy the agent has already decided against changes nothing. Both read guards now open with the
figure the read is about to cost — the file's lines and a `chars4` token estimate, the same scale the
meter reports in — and from the second whole-file read of a session on, the session's running count
and total (`Hooks/ReadCostNote.cs`, tally in a per-session file beside the checklist marker). The
alternative candidate, firing only from the second read on, is folded in: the first note carries the
per-file number, later ones carry a growing one. Whether a number persuades where a policy did not is
the next run's measurement.

## The write path has no guard (removed 2026-09-19)

Two hooks — `guard-cs-edit` on `Edit`/`Write`/`NotebookEdit`/`apply_patch` and `guard-cs-bash-write`
on write-shaped `Bash` (`sed -i`, `tee`, a redirect onto a `.cs`) — used to hard-deny any raw write to
a compiled `.cs` file and route it through `validate_patch`, the plugin's line-anchored patch tool,
whose apply was the only writer of the development log. Both went with that tool. The defect sweep
that decided it (FluidScript, 2026-09-19) found the failure classes were the design: line-anchored
edits missed the brace by one on a regular basis, a third of the tool's calls carried no edit at all
(lease refusals), and `dotnet build` produced the same dependent-break list the tool's compile ladder
did, uncapped, thirty seconds later.

`Edit`/`Write` are now the write path — string-anchored, on text the caller has read — with two
things around them: `hint-cs-write-checklist` (below) puts the write-time checklist in front of the
first edit, and `refresh-cs-files` (below) keeps the index in step with every write, which is what
made the removal safe: before it, an `Edit` left `search_index` and the `sym_` ids frozen until a
manual reload. `rename_symbol` stays, stripped to the rename and the write, because a rename is the
one edit a string replacement cannot make.

`BashCommandScanner.FindWriteTargetCsArgument`, the write-shape recogniser the Bash guard used,
survives: the refresh hook uses it to learn which files a shell command wrote.

## `hook guard-cs-read` — PreToolUse on `Read`

**Advisory by default, not blocking.** Allows `Read` on a `.cs` file that a project actually compiles,
and returns `hookSpecificOutput.additionalContext` pointing at `search_index`/`get_symbol` plus a
ranged `Read` — the read-side counterpart of the old edit guard, but not a hard deny. Built with
`HookOutcome.AllowAdvising`, the read-side sibling of `AllowWith`.

**`additionalContext`, not `permissionDecisionReason` — since 2026-09-20.** Through that date the
advisory went out as `permissionDecision: "allow"` with a `permissionDecisionReason`, and a run against
PandaAI verified what the hooks reference says: on an allow, the reason is shown to the user and never
to the model. Every earlier "the probe saw the advisory and overrode it" finding was an advisory nobody
received, and the cost figure added that morning was invisible too. The decision field is gone as well
as the reason: an explicit hook allow waives the user's own permission prompt for the call, which on a
`Bash` command whose first segment reads a compiled file meant auto-approving whatever followed the
semicolon. The advisory now rides the same channel the write-checklist hint always used, and no longer
decides anything.

This guard was a hard deny through 2026-08-20. It moved to advisory-only after the 2026-08-21
performance run showed the MCP route is not reliably cheaper or more correct on every question — a
small file, a whole-repo negative, raw text search all favored the raw route, and a hard deny forced
the more expensive tool even there.

**A ranged `Read` — one with `offset` or `limit` — draws no note in any mode**, forced-block
included (since 2026-09-19). Once a symbol is located, `get_symbol`'s `declarationSites` plus a
`Read` of that range is the intended way to fetch its text, so advising against it would be advising
against the route the skills recommend; and a forced-block probe that could not read code at all
would measure nothing. Only the whole-file read is the question the advisory exists for, and its
text now says so: locate, then read the range.

**`set_hook_guards(state: "block")` forces this guard back to its pre-2026-08-20 hard deny**, added the
same day as that migration once `dotnet-performance` needed a third probe with a real forced-enforcement
comparison point rather than only a ceiling (MCP-only tools) and a floor (guards suspended). `HookCli`
reads the `Blocked` half of `GuardSuspension.Current`'s tri-state result (`Active`/`Suspended`/`Blocked`)
and passes it into `Evaluate`'s `blocked` parameter; the membership check and the file-vs-tree distinction
are unchanged, only the outcome constructor flips from `AllowAdvising` to `Deny`. Not the plugin's shipped
default — a session that unexpectedly finds `Read` denying a `.cs` file should check `workspace_status`'s
`hookGuards: BLOCKED` line before assuming the file is simply ungoverned.

Solution membership is decided from the filesystem alone, in `Hooks/CsFileMembership.cs` (shared with
`guard-cs-bash-read` below, so the two can never disagree on the answer): a hook is a separate process
with no access to the MCP stdio pipe, so it cannot ask the running server's `WorkspaceHost` whether a
file belongs to the loaded solution. What it checks statically:

- Walk upward from the file for the nearest `.csproj`, watching for a `*.sln`/`*.slnx` at a level
  **strictly between** the file and the repo root. Finding one there means the file *may* belong to its
  own independent, nested solution (a test fixture's throwaway sample project, for example) — but only
  when the project the nested solution sits next to is **not itself referenced by the root
  `.sln`/`.slnx`**. An ordinary .NET layout has a project ship its own per-project solution purely for
  standalone-open convenience while still being listed in the root solution (a `PandaAI.Core.sln` next
  to `PandaAI.Core.csproj`, with both also covered by the root `PandaAI.sln`) — a plain "any nested `.sln`
  exists" check silently exempted every guard on such a project until this cross-check was added
  (`Hooks/CsFileMembership.cs`'s `IsReferencedByRootSolution`, which text-scans the root solution's
  project list — a `.slnx` via `Workspace/SlnxParser.cs`, a classic `.sln` via a regex over its
  `Project(...)` lines — rather than requiring an MSBuild evaluation). Only when the project is absent
  from that list is the read genuinely **allowed** as independently-solutioned. Reaching the repo root
  itself (where this repo's own top-level `.slnx` lives) is the ordinary case and is not treated as
  nested.
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

**Advisory by default, not blocking**, for the same reason and since the same run: allows the call and
returns the same `AllowAdvising` shape `guard-cs-read` does. Closes the gap `guard-cs-read` structurally
cannot:
its matcher is the `Read` tool by name, so a shell command that dumps the same file's bytes into the
transcript via `Bash` (`cat`, `sed`, `head`, `tail`, `grep`/`egrep`/`fgrep`/`rg`/`ag`, `awk`/`gawk`,
`nl`, `tac`, `bat`, or `less`/`more`) is invisible to it — a different tool name, same underlying read.
This hook watches `Bash` instead and applies the identical membership question via the same shared
`Hooks/CsFileMembership.cs`.

It is not a shell parser: `Hooks/BashCommandScanner.cs` splits the command on pipeline/statement
separators (`| ; & && ||`), takes each segment's first word as the invoked command — with any directory
and a trailing `.exe` stripped — and, only for a segment whose command is in the blocklist above
(overridable via `DOTNET_TOOLKIT_READ_BLOCKLIST`), looks for a bare `.cs`-suffixed argument token.

**Separators inside quotes or backslash-escaped are not separators.** This matters more than it sounds:
splitting `grep -n "Alpha\|Beta" Foo.cs | head` textually pushes the `.cs` path into a segment starting
`Beta"`, which is not a read utility, while the segment starting `grep` carries no path — so a
multi-term grep, the most common way to search several terms at once, read compiled C# unguarded. Fixed
by a quote/escape-aware scan, covered by `BashCommandScannerTests`.

**Coverage widened 2026-09-07** after a red-team pass found the advisory was a matter of which utility
the caller happened to reach for. Now recognized: the byte dumpers (`xxd`, `od`, `hexdump`, `base64`,
`strings`) — a hex dump puts the same source in the transcript as `cat`, only less legibly; `dd if=x.cs`,
whose path is welded to a key rather than being a bare operand; a script interpreter's inline program
(`python3 -c "print(open('Foo.cs').read())"`), whose path is inside the program and never an argument;
and `find . -name '*.cs' -exec cat {} \;`, whose own first word is `find` — deliberately not on the
blocklist, since listing names reads nothing — while the utility `-exec` names reads every match.

The interpreter case is a heuristic and is used as one: the `.cs`-literal search runs **only** when the
command is a known interpreter, because a bare `.cs`-shaped substring is far too weak a signal on its own
— a grep pattern or a commit message mentioning a filename would match it.

**Still not recognized, and one of them cannot be:** a quoted path containing a space (the standing
`Tokenize` limitation below), and a **variable-expanded path** (`F=src/Foo.cs; cat "$F"`). The second is
not a gap that can be closed by a better scanner — the path is not in the command text at all, and
resolving it would mean evaluating the shell. Both stay under-detected on the same fail-open posture as
the other guards; this is a workflow nudge, not a security boundary. `git`, `dotnet`, plain `find`, and
anything not in the blocklist are never touched, so `git diff -- Foo.cs`, `git log Foo.cs`, and
`find . -name '*.cs'` with no `-exec` are all unaffected.

**This under-detection is total, not narrow, on any repo whose path contains a space** — confirmed
2026-08-20 against a consuming repo cloned under `...\Energy Lead Agent\`: every `.cs` path in it
contains a space, so `Tokenize` splits every one of them mid-path and `FindCsArgument`/`CsScanRoots`
never see a `.cs`-suffixed token, meaning the advisory note never fires there at all. Now that this
guard allows rather than denies, the blast radius of that gap is "a missed nudge", not "an unenforced
boundary" — the read always proceeded either way. It was a live finding while this guard still denied;
it is stated here for the record: the same `Tokenize` limitation also decides which `Bash` writes
`refresh-cs-files` can see, where a missed path means one sweep of latency rather than a missed
nudge. (`dotnet-guard-redteam`, which used to probe for exactly this class of gap, went with the
write guards on 2026-09-19 — there is no enforcement left to bypass.)

**`set_hook_guards(state: "block")` forces this guard's hard deny back on**, the same way it does for
`guard-cs-read` above — `NamedCompiledFile`/`ScannedProject` and the membership check are unchanged;
only the outcome flips from `AllowAdvising(FileMessage(...))`/`AllowAdvising(TreeMessage(...))` to
`Deny(BlockedFileMessage(...))`/`Deny(BlockedTreeMessage(...))`. The `Tokenize` gap above is
under-detection either way it runs: while blocked, a spaced path that defeats the tokenizer is not
merely a missed nudge again — it is a real unenforced read, the same stakes the gap carried before
2026-08-20.

**The built-in `Grep` tool is not covered by either read guard.** `hooks/hooks.json` matches on tool
name, and its `PreToolUse` entries are `Edit|Write|NotebookEdit`, `Read`, and `Bash` — Codex maps
the first matcher to `apply_patch`; `Grep` and `Glob`
are matched by none of them, so `Grep` with `-A`/`-B`/`-C` or in content mode returns `.cs` source with
nothing intercepting it. Now that the read guards are advisory rather than blocking, this "hole" is the
same shape as every other unmatched tool: no advisory note, nothing enforced either way. `search_index`
is still the right tool for finding a declared symbol, and the `dotnet-read`/`dotnet-write` skills cover
it; `Grep`/`Glob` simply carry no nudge toward that. It matters most for `dotnet-code-review`, whose
`tools:` list grants `Grep` and `Glob` outright, and `dotnet-explore`, whose own instructions forbid
`Read` on a `.cs` file regardless of what any hook does.

## `hook guard-cs-bash-write` — removed

See "The write path has no guard" above. Its scanner lives on in `refresh-cs-files`.

## `hook refresh-cs-files` — PostToolUse on every tool that can write a `.cs` file

Fires after `Edit`, `Write`, `NotebookEdit`, `apply_patch` and `Bash`. It extracts the `.cs` and
project files the call wrote — `tool_input.file_path` for the native tools, the `*** Add/Update/Delete
File:` headers for `apply_patch`, and for `Bash` the same segment scan the write guard used
(`BashCommandScanner.FindWriteTargetCsArgument`, with `cd` tracked) — and sends them to the running
server as `refresh <json array>` over the loopback control channel. A tree-rewriting `git` verb
(`checkout`, `pull`, `merge`, `rebase`, `stash`, …) names no file, so it sends `rescan`, the full
sweep, instead.

Why it exists: both knowledge tiers are mtime-polling, not filesystem watchers. Before this hook the
syntax index caught an edit on the next debounced sweep (2 s) and the live Roslyn solution followed
it, but the SQLite symbol store — what `search_index`, every `sym_` id, `get_references` and both
hierarchies answer from — subscribed to nothing and was rebuilt only by the (since removed) patch
tool's own apply path or by a manual `reload_workspace`. An edit made any other way left them frozen.
The store now subscribes to the sweep (`SymbolIndexBuilder.OnFilesChanged`), so **the sweep is what
guarantees convergence**; the hook is what makes it immediate, so `search_index` right after `Edit`
sees the edit.

Server side, `refresh` runs `ProjectIndex.RefreshPathsAsync` (stat only those paths, re-parse what
moved, raise the same `FilesChanged` notification a sweep would — which swaps the text into the live
solution and queues the store refresh), then waits up to ten seconds for
`SymbolIndexBuilder.RefreshDocumentsAsync` on the changed paths. That pass re-indexes exactly those
documents, expanded to the other parts of any partial type they declare and, once it knows which
symbols vanished, to the documents whose call edges still name them (above fifty of those it queues a
full rebuild instead). A second asker for the same paths within the same pass joins it rather than
queuing a repeat.

**Silent on success by design.** A refactor is thirty edits; thirty "refreshed" notes would be thirty
lines of context saying nothing needs doing. The hook injects `additionalContext` only when the agent
has to act: a new or deleted file starts a workspace reload (the only way the semantic tier learns of
it), the pass outlasted its budget and is still running, or the server was unreachable for a *new*
file — an edited one converges on the next sweep regardless. It fails open like every other hook here.

## ## `hook track-subagent-start` / `hook track-subagent-stop`

Codex's tool hook payload does not repeat the active subagent identity. Its lifecycle payload does.
These hooks keep a fail-open temporary correlation keyed by the hashed `session_id` plus `turn_id`
while an agent is active, which `meter-tool-call` consults before recording a row. Claude Code's
direct `agent_id`/`agent_type` fields still win when present.

## `hook meter-tool-call` — PostToolUse on every tool (`matcher: "*"`)

The only hook whose subject is tools it otherwise never sees, and the only one that matches everything.
It exists because **the server cannot measure the alternative it is being compared against.** A
`retrieval_events` row is written from inside an MCP tool method, so it covers this plugin's tools and
nothing else; a `Grep` or a `Read` never enters the server process. `dotnet-performance` was therefore
metering one route with the server and the other by asking an agent to count its own calls — which is
not a comparison, and which the raw probe got wrong by roughly half on consecutive runs. A
`PostToolUse` hook fires on **harness dispatch**, independent of which tools an agent's grant contains,
so both routes are measured by the same code on the same payload.

One payload carries both directions and the attribution, which is why this is a single `PostToolUse`
hook rather than a `Pre`/`Post` pair: `tool_input` (what the model had to generate — output tokens),
`tool_response` (what was loaded into its context — input tokens), `tool_use_id`, and `agent_id` /
`agent_type` when the host includes them directly. Codex instead supplies those fields on
`SubagentStart`/`SubagentStop`; `SubagentTracker` correlates them by `session_id` plus `turn_id`
while that agent is active. Counting happens in the hook, using the same
`TelemetryRecorder.EstimateTokens` the server's own telemetry uses — comparability depends on it being
literally the same function — so the wire carries two integers rather than a whole tool response. The
row also retains the harness, model, turn and transcript identifiers when supplied. None changes
tokenization: `chars4` stays a host- and model-neutral approximation, including for local LLMs.

**It reports over the control channel instead of writing to SQLite itself**, for two reasons that are
easy to miss. A hook is a separate process with its own `Ids.AmbientSession`, so a row it wrote would
carry a session id no read ever matches, since `get_retrieval_metrics` is scoped to the server's
session. And routing through the channel keeps SQLite single-writer, which is what makes a hook firing
on *every* tool call safe against a store that sets no busy timeout. `ControlServer` stamps the row
with its own session id on arrival.

Idempotent by `tool_use_id`, which is `UNIQUE` with an `INSERT OR IGNORE`, so a redelivered hook is a
no-op rather than a doubled cost. Fails open and **silently** — a measurement must never interrupt the
work it measures, so an unreachable server or a payload with no `tool_use_id` simply records nothing.
The tell is the meter's call count sitting below the transcript's own `tool_uses`, a reconciliation the
perf report already performs.

This is also the one hook that costs something on calls it has no opinion about: it matches every tool,
so it adds its ~70ms to calls the guards would have ignored. That is the price of the comparison being
sound, and it is bounded — the rows are cleared on restart with the rest of the raw telemetry.

## `hook hint-cs-write-checklist` — PreToolUse on `Edit`/`Write`/`NotebookEdit`

Not a guard: it never denies. Fires on the session's **first** edit of a `.cs` file some project of
this repo compiles (the same `CsFileMembership` test the read guards use — a brand-new file, a loose
file and a test fixture's own solution are not the C# the checklist is about, and must not consume the
session's one emission) and injects the write-time checklist as `additionalContext`.

A caller who goes straight to `Edit` without invoking `dotnet-write` would otherwise never see the
checklist that skill carries. Firing here puts it in front of the caller at the moment it applies,
which is also why it does not have to be pre-loaded. **The hook is the checklist's only owner** —
`skills/dotnet-write/SKILL.md` points here and deliberately keeps no copy, since a copy would drift and
would still miss the caller this exists for.

**Once per session, not once per edit.** A checklist repeated on every edit of a long refactor is
noise, and noise gets ignored. `Hooks/CsWriteChecklistHint.cs` dedupes on a marker file under the OS
temp directory named for `HookPayload.SessionId`, claimed with `FileMode.CreateNew` — so the claim is
atomic and two agents sharing one session still produce exactly one checklist between them. The marker
lives in temp rather than the repo deliberately: a consuming repo should not accumulate per-session
files.

**No session id means silence.** Without one there is no way to tell a first call from a fiftieth, and
emitting anyway would spam every edit in the session. The always-loaded rule and the `dotnet-write`
skill remain the primary carriers; this hook is a backstop, so failing quiet is correct. Every IO
failure — an existing marker, an unwritable temp directory — takes the same path.

## Related files

`scripts/build-plugin.sh` and `scripts/build-plugin.cmd` publish the server to `dist/` — required after
any change under `src/` for the plugin to serve the new build. Both are thin wrappers over
`dotnet publish src/DotnetToolkit.McpServer -c Release -o dist`, which is the canonical command; they are
developer conveniences, and nothing the plugin ships at runtime executes a shell.

`scripts/format-json.cs` is a standalone `dotnet run` file-based program (not part of any project, so the
read guards leave it alone) that pretty-prints one JSON file — for inspecting the compact caches under
`.claude/dotnet-toolkit/cache/` without a Python dependency:
`dotnet run scripts/format-json.cs -- <path>`. Nothing invokes it automatically.
