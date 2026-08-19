# Contributing

Issues, self-evaluation reports and pull requests: <https://github.com/Attemainio/dotnet-toolkit/issues>

## Reporting how the tools behave on your repo

The most useful contribution is a measurement from a codebase that is not this one.

```text
/dotnet-selfeval
```

It runs a fixed probe over every shipped tool and measures each call's exact token cost from
`get_retrieval_metrics` deltas, then reports where the same outcome was reachable with fewer calls or
fewer tokens, which response fields restate what the caller already knew, and which outputs carry
noise. It is read-only: it never changes your code and never applies a patch.

Every finding is an improvement to **this plugin**, never a comment on your codebase.

This matters because the plugin has been tuned against a handful of solutions, and every codebase has
structural shapes the tools have not met: deep partial classes, big overload sets, generated code,
`.slnx` vs `.sln`, projects that fail to load. Those are the conditions under which a tool quietly
underperforms.

Reports contain tool names, token counts and call routes — **no source code**. Skim before posting if
your repo is private.

For "is this plugin worth it on my repo" rather than "what is wrong with it", run `/dotnet-performance`
instead — see [docs/benchmarks.md](docs/benchmarks.md).

## Building

`dist/` is committed, so users never build. You do:

```bash
dotnet build
dotnet test                                                      # unit + MSBuildWorkspace integration tests
dotnet test --filter FullyQualifiedName~ClassName                # a single test class
dotnet publish src/DotnetToolkit.McpServer -c Release -o dist    # required after any src/ or Hooks/ change
```

`scripts/build-plugin.sh` and `scripts/build-plugin.cmd` are thin wrappers over that publish line.

Two rules that are not optional:

- **`dist/` is what runs**, for the MCP server *and* the hooks. A source change is not delivered until
  it is republished.
- **Commit the republished `dist/` in the same commit as the `src/` change that required it.** A stale
  committed `dist/` ships a broken binary to everyone who pulls, not just to your own session. Treat it
  like a failing test.

`TreatWarningsAsErrors` is set repo-wide, so a build with warnings fails. If more than one .NET 10 SDK
is installed, build with the same one the server registers for MSBuild — it logs `MSBuild: ...` to
stderr at startup, and `DOTNET_TOOLKIT_DOTNET_ROOT` pins a different install.

## Repository layout

| Path | What lives there |
|---|---|
| `src/DotnetToolkit.McpServer/` | The server. `Tools/` is the MCP surface and the ground truth for names, signatures and return shapes |
| `tests/` | Unit tests plus `WorkspaceIntegrationTests`, which loads a fixture solution via `MSBuildWorkspace` |
| `skills/` | The skills Claude invokes — see below |
| `agents/` | `dotnet-explore` and `dotnet-code-review` definitions |
| `standards/` | The 13 coding standards, plus `index.md` saying which apply when. Read by explicit path, never auto-loaded |
| `.claude/rules/` | Exactly one file: the always-loaded router `dotnet-init` copies into consuming repos |
| `hooks/` | `hooks.json`; every hook is a subcommand of the published server binary |
| `docs/design/` | Architecture, agent design, hook rationale |
| `dist/` | The published server and hooks. Committed |

[`docs/design/architecture.md`](docs/design/architecture.md) explains how the pieces fit.

## The skills

| Skill | For |
|---|---|
| `dotnet-read` | Reading and navigating C# — which tool answers which question, and the cheap-route table |
| `dotnet-write` | Editing C# — the `validate_patch` write protocol and the pre-edit standards step |
| `dotnet-explore` | Surveying an unfamiliar area — briefs and launches the `dotnet-explore` agent |
| `dotnet-review` | Review requests — partitions scope across parallel `dotnet-code-review` instances |
| `dotnet-init` | Install / verify / uninstall in a consuming repo |
| `dotnet-consistency` | Audits whether the docs, skills, rules and hooks still match `Tools/*.cs` |
| `dotnet-performance` | Benchmarks the MCP tools against raw `Read`/`Grep` on a target repo |
| `dotnet-selfeval` | Audits the plugin's own responses for redundancy and waste |

## After changing a tool

The implemented tool surface under `Tools/*.cs` is the ground truth for **facts** — names, signatures,
return shapes — and every doc, skill, rule and hook message describing it is downstream.

After changing a tool name, signature or response contract, or a hook, script, skill or documented
workflow, **invoke `dotnet-consistency`**. It owns the authoritative list of files that describe the
surface and sweeps for drift. Update every affected file in the same change; two copies always diverge.

Any response-shape change also needs `Contracts/Contract.cs` bumped.

## Non-obvious invariants

- **stdout is reserved for MCP JSON-RPC.** All logging goes to stderr; never write to `Console.Out` in
  server code.
- **Tool signature changes break in-process callers** — the tests call these methods positionally.
- **Change detection is mtime-polling, not filesystem watchers**, so it works on WSL `/mnt/*` where
  inotify does not fire. Don't "fix" it into a watcher.
- **Any code that shells out with redirected stdout+stderr must drain both streams concurrently**
  (`Task.WhenAll`, never one fully then the other) — an unread stream's pipe buffer fills and the child
  hangs forever. Shelling out to `dotnet` also needs `MSBUILDDISABLENODEREUSE=1` and
  `DOTNET_CLI_USE_MSBUILD_SERVER=0`, because `restore`/`build` spawn persistent MSBuild worker nodes
  that inherit the redirected pipes and outlive the direct child.
- **`.gitattributes` forces LF on `*.sh`/`*.cs`/`*.csproj`.** Windows-side tooling once CRLF'd `.cs`
  files and broke raw string literals in two tests.

[`CLAUDE.md`](CLAUDE.md) is the full operating contract for working on the plugin itself.
