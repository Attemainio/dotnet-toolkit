# Installation

The short path is in the [README](../README.md#quick-start). This page covers the persistent install,
updates, uninstall and troubleshooting.

## Requirements

- **.NET 10 SDK.** The server targets `net10.0`, and the projects it analyzes need their own SDK
  present.
- **Claude Code.**
- **`dotnet restore` run at least once in your repo, in the same OS you run Claude Code in.** A restore
  done on Windows does not satisfy a WSL session, and vice versa. This is the single most common cause
  of a degraded workspace.

## You do not need to build it

`dist/` — the published server and hooks — is **committed to this repository**. A `git clone` or
`git pull` gives you a working plugin with no local build step:

```bash
git clone https://github.com/Attemainio/dotnet-toolkit
```

`.mcp.json` and `hooks/hooks.json` both invoke `dotnet <dll>` against `dist/`, so the same clone works
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

It **never modifies your CLAUDE.md**.

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

### Per-repo configuration

Optional settings live in `.claude/dotnet-toolkit/config.json` — pinning a solution when several exist,
and excluding generated code from the index. See [`design/architecture.md`](design/architecture.md).

### Caches

Caches live in your repo under `.claude/dotnet-toolkit/cache/`, are self-gitignored, and are
rebuildable from source at any time. Deleting the directory is safe.
