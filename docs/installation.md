# Installation

The short path is in the [README](../README.md#quick-start). This page covers the persistent install,
updates, uninstall and troubleshooting.

## Requirements

- **.NET 10 SDK.** The server targets `net10.0`, and the projects it analyzes need their own SDK
  present.
- **Claude Code or the Codex CLI.**
- **`dotnet restore` run at least once in your repo, in the same OS you run the agent in.** A restore
  done on Windows does not satisfy a WSL session, and vice versa. This is the single most common cause
  of a degraded workspace.

## You do not need to build it

`dist/` — the published server and hooks — is **committed to this repository**. A `git clone` or
`git pull` gives you a working plugin with no local build step:

```bash
git clone https://github.com/Attemainio/dotnet-toolkit
```

The host-specific MCP adapters and `hooks/hooks.json` invoke `dotnet <dll>` against `dist/`, so the same clone works
identically on Windows, Linux, macOS and WSL.

Build only if you are **modifying the plugin** — see [CONTRIBUTING.md](../CONTRIBUTING.md).

## Loading the plugin

### Trying it out

```bash
claude --plugin-dir /path/to/dotnet-toolkit
```

This does not modify Claude Code's configuration. Stop passing the flag and the plugin is gone.

### Keeping it

Register the clone as a local marketplace once, and it loads in every future session:

```text
/plugin marketplace add /path/to/dotnet-toolkit
/plugin install dotnet-toolkit@dotnet-toolkit
```

The name appears twice because the repo is both the marketplace and the one plugin in it —
`<plugin>@<marketplace>`.

### Codex

Codex understands this repository's existing Claude-compatible local marketplace, so there is no
second marketplace file to maintain:

```bash
codex plugin marketplace add /path/to/dotnet-toolkit
codex plugin add dotnet-toolkit@dotnet-toolkit
```

Those two commands are the whole installation: skills, `hooks/hooks.json`, **and** the MCP server.

> **Installing from a working clone? Package it first.** The installer snapshots the marketplace root
> verbatim, so pointed at a development tree it copies `src/`, `tests/`, `.git/` and build output —
> 632 MB here versus 50 MB of actual plugin — and on Windows it does not merely bloat, it **fails**:
> Visual Studio holds `.vs/<solution>/FileContentIndex/*.vsidx` open with a deny-read lock and the
> copy aborts with `failed to copy plugin file: Permission denied (os error 13)`. Run
> `scripts/package-plugin.sh` (or `.cmd`) and point the marketplace at `package/` instead:
>
> ```bash
> scripts/build-plugin.sh      # publish dist/ first
> scripts/package-plugin.sh    # then assemble package/
> codex plugin marketplace add /path/to/dotnet-toolkit/package
> ```
>
> A fresh `git clone` has none of those directories, so installing from a **remote** marketplace needs
> no packaging step. This is only for a local source that is also a working tree.
The server needs no separate `codex mcp add`, because `.codex-plugin/plugin.json` points at
`.codex-plugin/mcp.json`, which declares it with a path relative to the plugin:

```json
{ "mcpServers": { "dotnet": { "command": "dotnet", "args": ["dist/DotnetToolkit.McpServer.dll"] } } }
```

**That relative path is the whole portability story.** Codex resolves a plugin-declared argument
against wherever the plugin is installed, while still launching the server in the session's own
working directory — so one manifest is correct under native Windows, native Linux/macOS and WSL
alike, and the server still finds the *project's* solution rather than the plugin's. It is the same
mechanism `${CLAUDE_PLUGIN_ROOT}` provides on the Claude side.

#### Do not add an absolute path to `config.toml`

A `[mcp_servers.dotnet]` block in `config.toml` is an **override layer** on top of the manifest, not
an alternative to it — Codex merges the two, and the profile wins. Writing a launch path there
therefore replaces the portable declaration with one absolute path, which can only ever be spelled
for one OS:

```toml
# Re-breaks the WSL case. The manifest already handles this.
[mcp_servers.dotnet]
command = "dotnet"
args = ['C:\path\to\dotnet-toolkit\dist\DotnetToolkit.McpServer.dll']
```

Per-tool policy (`[mcp_servers.dotnet.tools.<name>] approval_mode = "approve"`) is a different
matter: that genuinely is yours, belongs in the profile, and merges cleanly with the manifest.

#### The marketplace source is still executor-specific

**The OS you are looking at and the OS your tools run in are not the same question.** A Windows Codex
desktop app can execute its local processes inside WSL2 while keeping its Windows-owned profile —
`CODEX_HOME` then points at `C:\Users\you\.codex`, mounted into Linux as `/mnt/c/Users/you/.codex`,
and a `/home/you/.codex` profile you configured separately is *not* the one in effect.

A **local** marketplace source has to be an absolute path, so it is the one setting the manifest
cannot make portable:

| Where you run Codex | Profile in effect | Marketplace source |
| --- | --- | --- |
| Native Windows | `%USERPROFILE%\.codex` | `C:\path\to\dotnet-toolkit` |
| Native macOS or Linux | `~/.codex` | `/path/to/dotnet-toolkit` |
| Standalone WSL | `/home/<user>/.codex` | `/path/to/...` or `/mnt/c/...` |
| **Windows desktop app executing in WSL** | the **Windows** `.codex`, read through `/mnt/c/...` | `/mnt/c/path/to/dotnet-toolkit` |

The last row is the one that catches people, and Codex writes the wrong spelling into it by itself:
`codex plugin marketplace add` run on the Windows side stores a `\\?\C:\...` extended-length path,
which a WSL executor cannot resolve — reported as
`marketplace root does not contain a supported manifest`. Run the command from a shell in the same
place your tools will run, or rewrite the source by hand afterwards. `doctor` names both the
mismatch and the replacement path.

Codex on WSL also needs **`bubblewrap`** (`sudo apt-get install bubblewrap`) for its Linux sandbox.
Without it, filesystem-backed operations fail before any hook or tool result appears, which reads as
"the plugin's hooks do not fire".

#### Verify it, do not assume it

`codex mcp list --json` reports what your configuration *says*; it launches nothing, so it shows a
server as enabled whether or not the command can run. Check for real:

```bash
dotnet /path/to/dotnet-toolkit/dist/DotnetToolkit.McpServer.dll doctor --repo /path/to/your/repo
```

It reports the executor and topology, which profile is in effect, whether every stored path is
openable *from this process*, which SDK the workspace will load with and why, whether `bubblewrap` is
present, and then launches the configured server and completes a real handshake — process started,
protocol initialized, tools discovered, ping, workspace loaded — naming a fix for each failure. It
exits nonzero if anything failed, so it works in CI. `--json` for machine-readable output,
`--no-probe` to skip the launch, `--timeout <seconds>` for a cold restore.

Open `/plugins` in the Codex CLI to enable or disable the plugin, and `/hooks` to review and trust
its hooks. Plugin support is currently a Codex CLI and desktop-app feature, not an IDE-extension
feature.

The plugin and server have separate switches. Disable the plugin in `/plugins`. Disable only the MCP
server while keeping the skills and hooks enabled with:

```toml
[mcp_servers.dotnet]
enabled = false
```

That is the override layer doing its intended job: the manifest still declares the server, and the
profile switches it off. Set it back to `true`, or delete the block, to load it again — there is no
`codex mcp remove dotnet` to run, since nothing registered it in the profile. Remove the plugin with
`codex plugin remove dotnet-toolkit@dotnet-toolkit`; remove the local catalog separately with
`codex plugin marketplace remove dotnet-toolkit`.

#### Agents in Codex

The `agents/` briefs are Markdown with Claude YAML frontmatter, and that is the supported form in
both hosts. Codex does not register them as native `.codex/agents/*.toml` custom agents; the
launcher skills instead start a generic worker and point it at `agents/<name>.md`, treating the
frontmatter's Claude-specific tool, model and colour fields as host configuration and the Markdown
body as the shared role contract. One canonical brief per agent, no generated duplicates to drift —
see the repo's `AGENTS.md`.

> **Installed before 2026-08-19?** The marketplace was renamed from `dotnet-toolkit-local`. Run
> `/plugin marketplace remove dotnet-toolkit-local` first.

## Wiring it into a repo

Installing makes the tools *available*. It does not make a fresh session *prefer* them.

```text
/dotnet-init
```

This is the step that writes files, and it asks first. It shows you the exact plan, writes only after
you approve, and backs up anything it touches.

What it adds to the repo you run it in:

| Path | What it is |
|---|---|
| `.claude/rules/dotnet-index.md` | **One** always-loaded rule — a pure router that names no tools, only which skill to invoke for reading, writing, exploring or reviewing C# |
| `.claude/settings.json` | The read-only MCP tools merged into the permission allowlist, so they stop prompting on every call |
| `.claude/dotnet-toolkit/install.json` | A record of what was installed, used to verify and refresh later |
| `.claude/dotnet-toolkit/.gitignore` | Keeps the toolkit's run output out of your history — see below. Your own root `.gitignore` is never touched |
| `.claude/dotnet-toolkit/local-install.json` | *Conditional.* Your repo's own notes on any local workaround needed beyond the plugin's own procedure — a restricted-path copy step, a locked-file update sequence. Never auto-created; `/dotnet-init` offers to write or update it when one applies |

It **never modifies your CLAUDE.md**.

In Codex, the plugin's skill metadata provides the routing automatically. For development inside this
repository, the checked-in `AGENTS.md` is intentionally only an adapter to the canonical
`CLAUDE.md` and `.claude/rules/dotnet-index.md`; the instructions are not duplicated.

The 13 coding standards are *not* copied. They stay in the plugin at
`${CLAUDE_PLUGIN_ROOT}/standards/` and are read by explicit path when the writer or the reviewer needs
one — so they cost nothing until something asks for them.

### Verifying and refreshing

Re-running `/dotnet-init` is the verify-and-refresh path. It checks the installed state, tells you
whether your copies have fallen behind the plugin, and refreshes only what the plugin changed.

## Updating

```bash
git pull
```

That is normally the whole update — `dist/` comes with it. Then run `/reload-plugins` or restart, since
the server running in an open session is the one it started with.

Re-run `/dotnet-init` afterwards to refresh the router rule if the plugin changed it.

### If `git pull` fails while a Claude Code session has this plugin loaded

On Windows, an open session holds `dist/DotnetToolkit.McpServer.dll` locked for as long as the server
process runs, and a `git pull` that tries to overwrite a locked file does not queue behind it — it
aborts partway through. **Close Claude Code entirely, then run `git pull` again from a plain terminal,
then reopen.** Restarting a session without exiting the app does not release the lock, and running the
command via an in-session `!` shell prefix does not either — the server process is still the same one.

```powershell
cd "path\to\dotnet-toolkit"
git pull
```

If a previous pull was interrupted this way, `git status` shows a pile of modified/deleted files with
`dist/` itself listed as untracked (`??`) and `HEAD` still on the pre-pull commit — that is the
partial-checkout state, not corruption. **Re-running `git pull` does not finish it, even with the app
closed**: git already wrote the incoming content before it hit the locked DLL, so that content now reads
back as *local changes*, and the merge aborts before touching anything further (`Your local changes to
the following files would be overwritten by merge`, plus `The following untracked working tree files
would be overwritten`). None of it is your own work — it is the interrupted pull's own half-applied
output. With the app closed, land on the target directly instead. Safe whenever
`git rev-list --count origin/main..HEAD` is `0` (no local commits to lose):

```powershell
git fetch origin
git reset --hard origin/main
git clean -fd dist/      # drops any build fossil predating the reset, e.g. a stray .exe
```

Then re-run `/dotnet-init` — a plugin pull does not refresh a consuming repo's copy of
`.claude/rules/dotnet-index.md` on its own.

## Uninstalling

- **Loaded with `--plugin-dir`**: stop passing the flag. Nothing was recorded anywhere.
- **Installed from the local marketplace**: `/plugin uninstall dotnet-toolkit@dotnet-toolkit`, then
  `/plugin marketplace remove dotnet-toolkit`, then `/reload-plugins`.

The MCP server and the guard hooks travel *with* the plugin — they stop the moment it unloads.

The files `/dotnet-init` wrote into your `.claude/` are yours to keep or delete. Re-running it lists
exactly what a clean removal touches, as a dry run.

## Troubleshooting

| Symptom | What it means |
|---|---|
| Tools say the workspace is still loading | The MSBuild model builds in the background so startup stays instant. `search_index`/`get_symbol` already answer from the syntax index; `workspace_status` shows progress. |
| `workspace_status` says **DEGRADED** | A project failed to load and its reference edges are missing — results are incomplete, not wrong-but-complete. Anything that breaks `dotnet build` breaks this too. Usually a missing `dotnet restore`. |
| Results look stale | Change detection is mtime-polling, so it works on WSL `/mnt/*` where inotify does not fire. `reload_workspace` forces a refresh. |
| Answers changed after a rebase or a big pull | `reload_workspace(scope: "all")`. |
| More than one .NET 10 SDK installed | The server registers the newest and logs `MSBuild: ...` to stderr at startup. `DOTNET_TOOLKIT_DOTNET_ROOT` pins a different install. |
| A tool response is hard to parse | `set_output_format(format: "compact")` for compact JSON, or `"json"` for indented. Holds for the session. |
| The server never starts, with no named error | A restricted execution policy may be blocking the binary's path. See below. |
| `git pull` fails partway, or leaves `dist/` untracked | An open Claude Code session held `dist/`'s DLL locked mid-pull. See [Updating](#updating). |

### Restricted execution environments (AppLocker, locked-down paths)

Some organizations restrict which filesystem paths a binary may execute from — Windows AppLocker path
rules are the common case. If your clone's location isn't on the allowed list, the server never starts,
and the failure surfaces as `--plugin-dir`/the marketplace load silently not working, with nothing
naming AppLocker specifically.

The workaround is to run the plugin from a location the policy already allows — a per-user path such as
under `%LOCALAPPDATA%` or `%APPDATA%` is the usual choice — and copy the clone's contents there:

```powershell
robocopy "C:\path\to\dotnet-toolkit" "%LOCALAPPDATA%\dotnet-toolkit" /MIR
```

Point `--plugin-dir` (or the marketplace registration) at the copy, not the original clone.

**This is not automatic, and `git pull` does not fix it for you**: pulling updates the *original* clone
only, so the copy the marketplace actually runs from goes stale on every update unless the copy step is
repeated afterward. If this applies to your setup, record the exact copy command in
`.claude/dotnet-toolkit/local-install.json` (`/dotnet-init` offers to write it) so the next session —
and the next person on your team — doesn't have to rediscover the workaround.

### Per-repo configuration

Optional settings live in `.claude/dotnet-toolkit/config.json` — pinning a solution when several exist,
and excluding generated code from the index. See [`design/architecture.md`](design/architecture.md).

### Caches

Caches live in your repo under `.claude/dotnet-toolkit/cache/`, are self-gitignored, and are
rebuildable from source at any time. Deleting the directory is safe.

### What lands in `.claude/dotnet-toolkit/`, and what git sees

Everything the toolkit writes *about your repo* lands here, and the `.gitignore` init puts at the root
of that folder splits it in two:

| Tracked | Ignored |
|---|---|
| `install.json` — what you installed | `review/`, `eval/`, `perf/`, `redteam/` — reports from `dotnet-review`, `dotnet-selfeval`, `dotnet-performance`, `dotnet-guard-audit` |
| `config.json` — your per-repo toolkit settings | `backups/` — init's pre-overwrite copies, one machine's |
| `local-install.json` — your repo's own installation workarounds, if any | |
| the `.gitignore` itself | `cache/` — self-ignored by the server, rebuildable |

The split is what each file *describes*. `install.json` and `config.json` describe the repo, so they
belong to it and travel with a clone. The rest describes one run against the repo on one machine on
one date — and a report committed today is read as current long after the finding it raised was
fixed, which is the failure this prevents.

It is one file at the folder root, not one per folder: the same rule restated four times drifts three
ways. **Your own root `.gitignore` is never edited.** If you would rather ignore the whole
`.claude/dotnet-toolkit/` directory from there, that works too — it just un-tracks `install.json`
along with it, so a fresh clone can't tell what was installed.
