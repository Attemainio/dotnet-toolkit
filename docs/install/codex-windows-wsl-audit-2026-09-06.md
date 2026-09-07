# dotnet-toolkit Codex installation audit

**Date:** 2026-09-06  
**Auditor:** Codex desktop app, Windows 11 host with WSL2 execution  
**Repository:** `C:\Users\atte9\source\repos\dotnet-toolkit` / `/mnt/c/Users/atte9/source/repos/dotnet-toolkit`  
**Baseline:** commit `aadfc17` on `main`, plus the dirty working tree present during the audit  
**Plugin manifest version:** `1.0.1+codex.20260902054545`  
**Server-reported version:** `1.0.1.0`

## Purpose

This report records why dotnet-toolkit did not work immediately in the Windows Codex desktop app when that app was configured to execute through WSL2. It covers MCP discovery, DLL paths, .NET SDK selection, plugin/marketplace loading, skills, agents, hooks, repository guidance, and the Codex WSL sandbox.

The intended end state is:

- Claude Code works natively from the same clone.
- Codex works natively on Windows.
- Codex works natively in standalone WSL2.
- The Windows Codex desktop app also works when its execution environment is switched to WSL2.
- Installing and verifying the plugin does not require rediscovering host/profile/path rules manually.
- MCP tools, skills, agents, and hooks are present and usable in a fresh session.

No existing dotnet-toolkit files were changed during this audit. The repository was already dirty, including changes to source, docs, manifests, hooks, and `dist/`; all source-versus-binary findings therefore describe the exact working tree observed, not necessarily the last released or committed state.

## Executive verdict

The MCP server implementation is capable of starting, completing the MCP handshake, returning `tools/list`, indexing source, and loading solutions. The immediate failure in Codex was primarily an installation/execution-context mismatch: the Windows-owned Codex profile contained Windows paths, while the desktop app launched the MCP process inside WSL2.

There is also a confirmed source/published-binary mismatch in the audited dirty working tree. The current C# source contains `global.json`-aware SDK selection, while the committed/present `dist/DotnetToolkit.McpServer.dll` still contains the older user-local-newest selection behavior. This caused FluidScript to select SDK 10.0.301 and degrade even though compatible SDK 10.0.111 was installed.

The plugin bundle's skills and hooks were loaded successfully. Claude-format Markdown agent briefs were present and usable through Codex skills that launch a generic worker and tell it to read the brief, but they were not installed as native named Codex custom agents. End-to-end hook interception could not be fully proven because the WSL Codex sandbox lacked `bubblewrap`, although direct hook entrypoints and the guard test suites worked.

## Environment and profile topology

### Observed execution environment

- Kernel: WSL2 Linux (`6.6.87.2-microsoft-standard-WSL2`).
- Distribution: Ubuntu.
- Shell working directory: `/mnt/c/Users/atte9/source/repos/FluidScript`.
- Linux user: `atte`.
- `dotnet` resolved to `/usr/bin/dotnet` -> `/usr/lib/dotnet/dotnet`.
- System SDKs under `/usr/lib/dotnet/sdk`: 10.0.111 and 8.0.130.
- User-local SDK under `/home/atte/.dotnet`: 10.0.301.
- Standalone WSL Codex executable: `/home/atte/.local/bin/codex`.

### Active profile used by the desktop task

The Windows desktop app injected:

```text
CODEX_HOME=/mnt/c/Users/atte9/.codex
WSL_DISTRO_NAME=Ubuntu
```

Therefore this task used the Windows-owned Codex profile mounted into WSL, not `/home/atte/.codex`.

This distinction explains the apparent contradiction:

- The independently installed WSL Codex profile at `/home/atte/.codex/config.toml` was configured with WSL paths.
- The Windows desktop task did not read that profile.
- It read `C:\Users\atte9\.codex\config.toml` through `/mnt/c/Users/atte9/.codex/config.toml` and executed its local processes in Linux.

Remote control from Android controls the Windows desktop session; it does not change which Codex home or execution environment is selected.

## Reproduction summary

### Broken configured launch

The active desktop profile contained:

```toml
[mcp_servers.dotnet]
command = "dotnet"
args = ['C:\Users\atte9\source\repos\dotnet-toolkit\dist\DotnetToolkit.McpServer.dll']
```

Executed inside WSL2, this failed:

```text
Could not execute because the specified command or file was not found.
dotnet-C:\Users\atte9\source\repos\dotnet-toolkit\dist\DotnetToolkit.McpServer.dll does not exist.
```

No dotnet MCP tools were exposed to the Codex task. The server appeared as configured and enabled in `codex mcp list --json`, demonstrating that configuration inventory alone does not prove launch success.

### Corrected WSL launch

This launch succeeded:

```text
dotnet /mnt/c/Users/atte9/source/repos/dotnet-toolkit/dist/DotnetToolkit.McpServer.dll
```

It returned:

- MCP protocol version `2025-06-18`.
- Server name `DotnetToolkit.McpServer`.
- Server version `1.0.1.0`.
- `tools.listChanged: true`.
- A complete `tools/list` response with schemas and detailed descriptions.

With `DOTNET_TOOLKIT_DOTNET_ROOT=/usr/lib/dotnet`, FluidScript reported:

```text
root: /mnt/c/Users/atte9/source/repos/FluidScript
solution: FluidScript.slnx
index: ready 192 files, 413 types
workspace: loaded 5 projects in 3.6s
loaded: FluidScript.Api, FluidScript.Api.Tests, FluidScript.Core,
        FluidScript.Core.Tests, FluidScript.Fixtures
```

The dotnet-toolkit repository itself loaded 165 files, 273 types, and 2 projects.

## Findings

### F1 — Critical: Windows DLL path was passed to Linux `dotnet`

**Area:** Codex MCP registration / Windows desktop with WSL execution  
**Evidence:** active `/mnt/c/Users/atte9/.codex/config.toml`, `[mcp_servers.dotnet]`  
**Status:** confirmed

The user-facing application was Windows, but `runCodexInWindowsSubsystemForLinux = true` moved local execution into Linux. The stored MCP argument remained a Windows path. Linux `dotnet` cannot interpret `C:\...` as a DLL path.

**Required behavior:** installation must derive the path syntax from the MCP executor environment, not from the UI host OS.

**Required WSL form:** 

```toml
[mcp_servers.dotnet]
command = "dotnet"
args = ["/mnt/c/Users/atte9/source/repos/dotnet-toolkit/dist/DotnetToolkit.McpServer.dll"]
```

**Native Windows form:** 

```toml
[mcp_servers.dotnet]
command = "dotnet"
args = ['C:\Users\atte9\source\repos\dotnet-toolkit\dist\DotnetToolkit.McpServer.dll']
```

**Plugin changes needed:**

- `docs/installation.md:61-72`: document Windows desktop + WSL as a distinct case.
- `skills/dotnet-init/SKILL.md:10-18`: detect or ask which executor Codex uses before constructing the path.
- `README.md:46-57`: remove the implication that one absolute registration command works unchanged in native Windows and Unix execution.
- Verification must execute `ping` or `workspace_status`; `codex mcp list --json` is insufficient.

### F2 — High: marketplace source path also used the wrong path namespace

**Area:** Codex plugin marketplace / Windows desktop with WSL execution  
**Evidence:** active config used `\\?\C:\Users\atte9\source\repos\dotnet-toolkit`  
**Status:** confirmed

Running `codex plugin list --json` from the active WSL environment failed:

```text
failed to load configured marketplace snapshot(s)
marketplace root does not contain a supported manifest
```

The already-populated versioned cache still allowed skills and hooks to appear in this task, but plugin listing/reload/update was not reliable from the WSL executor.

**Required behavior:** marketplace registration must use the path namespace understood by the process evaluating it, and the installer must verify both source discovery and cache loading.

**Candidate WSL form:**

```toml
[marketplaces.dotnet-toolkit]
source_type = "local"
source = "/mnt/c/Users/atte9/source/repos/dotnet-toolkit"
```

Because the desktop UI and WSL CLI may evaluate marketplace paths in different processes, this needs an explicit Windows-desktop-with-WSL integration test before finalizing the fix.

### F3 — Critical in the audited working tree: `dist` does not match current SDK-selection source

**Area:** published server artifact  
**Evidence:** `src/DotnetToolkit.McpServer/Workspace/MSBuildRegistration.cs:55-119` versus embedded strings and runtime output from `dist/DotnetToolkit.McpServer.dll`  
**Status:** confirmed for the dirty working tree

Current source:

- receives the repository root;
- reads the governing `global.json`;
- chooses an installed version allowed by `GlobalJsonPolicy`;
- logs `for global.json ...` when that route wins.

The present published DLL instead contains the older string:

```text
from the user-local install
```

It does not contain the current source's `for global.json` or `no installed SDK satisfies global.json` strings. Runtime output also used the old message and selected 10.0.301.

The worktree already had both source and `dist` changes before this audit. This may be an in-progress unpublished rebuild rather than a defect in the last committed artifact, but the state is not shippable as-is.

**Required behavior:** a plugin version/cache must never present C# source and a committed `dist` binary implementing different SDK-selection policies.

**Required safeguards:**

- CI rebuilds `dist` and fails if the resulting tracked artifacts differ.
- A black-box test launches the actual `dist` DLL against a fixture with competing system/user SDK feature bands.
- Release/version validation checks the manifest, assembly informational version, contract version, and binary behavior together.
- Installation verification reports the selected SDK and why it won.

### F4 — High: old published DLL selected an incompatible SDK despite compatible SDK availability

**Area:** SDK selection / workspace health  
**Evidence:** FluidScript `global.json`, server startup logs  
**Status:** confirmed

FluidScript requests:

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestPatch"
  }
}
```

Available SDKs included system 10.0.111 and user-local 10.0.301. The old published server chose 10.0.301 because it was newest, then its restore failed because that feature band did not satisfy the repository policy. `workspace_status` would therefore be degraded and semantic results incomplete.

Pinning the actual WSL system root fixed it:

```toml
[mcp_servers.dotnet.env]
DOTNET_TOOLKIT_DOTNET_ROOT = "/usr/lib/dotnet"
```

or equivalently:

```toml
env = { DOTNET_TOOLKIT_DOTNET_ROOT = "/usr/lib/dotnet" }
```

This override is an effective local workaround, but the current source's `global.json` policy should make it unnecessary when a compatible installed SDK is discoverable.

### F5 — High: verification can report “enabled” while zero MCP tools are callable

**Area:** `dotnet-init` verification  
**Evidence:** `codex mcp list --json` showed `dotnet` enabled while the launch command failed and no tools appeared  
**Status:** confirmed

`skills/dotnet-init/SKILL.md:15-16` requires configuration listings and a live `/mcp` check. The listings are not health checks, and `/mcp` remains a manual UI observation.

**Required behavior:** verification must be executable and outcome-based:

1. Confirm plugin source resolves.
2. Confirm the MCP process starts.
3. Complete `initialize`.
4. Call `tools/list` and assert the expected tool count/names.
5. Call `ping`.
6. Call `workspace_status` in the intended consuming repository.
7. Fail if the workspace is degraded, stale, or rooted in the wrong directory.

This should be one repeatable script or command, not a checklist reconstructed by each user or agent.

### F6 — High: standalone WSL and Windows-desktop-with-WSL profiles are separate but documentation does not make that operationally obvious

**Area:** installation model  
**Status:** confirmed

The standalone WSL profile at `/home/atte/.codex/config.toml` had correct WSL paths, plugin enablement, hook trust, and per-tool approval rules. The desktop task ignored it because the app supplied `CODEX_HOME=/mnt/c/Users/atte9/.codex`.

**Required behavior:** installation docs and diagnostics must print:

- the effective `CODEX_HOME`;
- the process OS/executor;
- which config file was loaded;
- the resolved marketplace root;
- the exact MCP command after path conversion;
- whether the current task is desktop-native Windows, desktop-through-WSL, or standalone WSL.

The phrase “same Codex host” is not sufficient guidance when a Windows desktop host delegates execution into WSL while retaining the Windows-owned profile.

### F7 — High: Codex installation still requires a separate manual MCP registration

**Area:** out-of-the-box plugin experience  
**Evidence:** `.codex-plugin/plugin.json`, `docs/installation.md:61-78`  
**Status:** confirmed design limitation

The native Codex manifest declares skills but no MCP launch dependency. Installation therefore has two independent operations:

1. install/enable the plugin bundle;
2. separately run `codex mcp add dotnet -- dotnet <absolute DLL path>`.

Installing the plugin can leave users with working skills/hooks but no tools, exactly as observed.

**Required investigation:** determine whether current Codex plugin/skill packaging can declare the local MCP dependency through supported dependency metadata (including `agents/openai.yaml` where applicable), or whether `dotnet-init` must perform and verify registration explicitly. If Codex cannot portably anchor the DLL at the plugin root while inheriting the workspace CWD, the installer still needs a host-aware registration step with a live test.

### F8 — High, pending post-fix runtime confirmation: shared skills hard-code a Claude-style MCP prefix

**Area:** skill portability  
**Evidence:** `skills/dotnet-read/SKILL.md` and related skills  
**Status:** probable compatibility defect; not observable in the broken task because no dotnet tools were attached

The skills instruct callers to search for exact names prefixed:

```text
mcp__plugin_dotnet-toolkit_dotnet__
```

Codex separately registers the server with the name `dotnet`. Codex conventionally namespaces tools by the configured server name, which may produce:

```text
mcp__dotnet__workspace_status
```

rather than the Claude plugin-qualified prefix.

**Required test:** after fixing the MCP path, capture the exact tool names visible to a fresh Codex task and run every skill's exact `ToolSearch("select:...")` instruction. If prefixes differ, skills need a host-neutral resolution rule or Codex-specific adapter. This must be tested rather than inferred.

### F9 — Medium/high: agent briefs are Claude-format assets, not native Codex custom agents

**Area:** subagents  
**Evidence:** `agents/*.md`, `.codex-plugin/plugin.json`, current Codex custom-agent documentation  
**Status:** confirmed packaging difference

The plugin ships Markdown agents with Claude YAML frontmatter. Codex skills compensate by launching a generic worker and instructing it to read the Markdown body while ignoring Claude-specific frontmatter. This adapter worked during the audit: `dotnet-guard-redteam` ran and returned findings.

However, Codex documents native custom agents as TOML files under `~/.codex/agents/` or `.codex/agents/`, with fields such as `name`, `description`, and `developer_instructions`. The plugin does not install such definitions, and its Codex manifest does not declare agents.

This conflicts with internal installation claims such as `docs/install/install.md:50` and `docs/install/audit.md`, which say agents are discovered from the plugin manifest. In this audit, the agents were reachable through skill adapters, not registered as named native Codex agents.

**Required decision:**

- If adapter-based execution is the supported Codex design, document it accurately and test every launcher skill.
- If “native agents” is a requirement, generate/install `.codex/agents/*.toml` equivalents and verify their MCP/skill grants.

### F10 — Medium: hooks are installed and trusted, but end-to-end interception was not fully verifiable

**Area:** Codex hooks  
**Evidence:** active hook trust state, direct hook probes, guard tests  
**Status:** partially verified

Positive evidence:

- All nine current hook definitions had stored trusted hashes.
- Cached hooks matched the source tree exactly.
- Direct `hook guard-*` entrypoints worked.
- 23 Bash scanner/related tests passed.
- 41 `GuardCs*` tests passed.
- Named write shapes (`sed -i`, `perl -i`, spaced redirects, `tee`, `cp`, `mv`, `dd`) were denied.

Limitation:

- Several real Codex shell/apply-patch probes failed in the desktop sandbox before a toolkit hook result was visible.
- Therefore “hook command works” is proven, but “Codex reliably invokes every hook for every intended tool alias in this environment” remains to be rechecked after fixing `bubblewrap` and MCP startup.

### F11 — High environmental blocker: WSL Codex sandbox lacks `bubblewrap`

**Area:** Codex WSL runtime  
**Status:** confirmed

Many filesystem-backed operations failed with:

```text
bubblewrap is unavailable: no system bwrap was found on PATH and no bundled codex-resources/bwrap binary was found
```

Consequences observed:

- ordinary read-only shell diagnostics sometimes failed before command execution;
- image inspection failed even after copying the image into `/tmp`;
- live hook interception tests could not complete;
- `apply_patch` probes could not establish whether the hook ran.

Current Codex documentation states that WSL2 Codex uses the Linux sandbox and that modern releases use `bubblewrap`. This is not caused by dotnet-toolkit, but dotnet-toolkit installation verification should detect and report it because it prevents reliable hook/tool validation.

### F12 — Medium: guard read coverage has advisory gaps

**Area:** hook scanner  
**Status:** confirmed by direct red-team invocation

The read guard is advisory rather than a hard deny. These shapes were silent:

- variable-expanded path, such as `cat "$F"`;
- `find ... -exec cat`;
- `xxd`;
- `od`;
- `base64`;
- Python reads;
- `dd` reads.

The core named readers (`cat`, `sed`, `head`, `grep`) and multi-argument `cat` produced the intended advisory. This does not block MCP startup, but it means hooks alone cannot guarantee that all C# reads route through dotnet-read.

### F13 — Medium: known write-scanner gaps remain

**Area:** hook scanner  
**Status:** confirmed/documented

Python-based file writes and an unspaced redirect form were not denied by direct hook evaluation. The standard named write forms were denied. These gaps matter because a missed write bypasses compilation validation and development-log recording.

### F14 — Low/medium: Heredoc documentation is broader than actual behavior

**Area:** `docs/design/hooks.md`  
**Status:** confirmed documentation drift

A common heredoc command with a spaced `> target.cs` redirect was denied correctly, while documentation reportedly describes heredocs generally as unrecognized. Narrow the documentation to the specific unrecognized forms rather than classifying all heredocs as gaps.

### F15 — Medium: version identity is ambiguous during diagnostics

**Area:** release/versioning  
**Status:** confirmed observation

The audit saw:

- `.codex-plugin/plugin.json`: `1.0.1+codex.20260902054545`;
- `.claude-plugin/plugin.json`: `1.0.1`;
- server `initialize`: `1.0.1.0`.

Different version formats can be intentional, but they make it difficult to prove that the installed cache, source clone, and `dist` binary are the same build. This contributed to the source/dist ambiguity.

**Required behavior:** `ping` or `workspace_status` should expose an immutable build identifier that also appears in the native Codex manifest, Claude manifest, and published artifact metadata.

### F16 — Informational: FluidScript has no repo-local dotnet-toolkit init manifest/router

**Area:** consuming-repository wiring  
**Status:** observed, not necessarily a Codex defect

FluidScript contained runtime cache/review artifacts but did not contain:

- root `AGENTS.md`;
- `.claude/rules/dotnet-index.md`;
- `.claude/dotnet-toolkit/install.json`.

Codex can route through installed plugin skill metadata without the Claude-specific copied router. Claude Code may still require `dotnet-init` repo wiring. Installation verification should state which host is being checked and should not conflate missing Claude repo wiring with missing Codex plugin wiring.

## Components that were healthy

### MCP implementation and descriptions

`Program.cs` uses:

```csharp
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
```

It sends logs to stderr, starts indexing/workspace loading in the background, and keeps stdout for JSON-RPC. Direct `tools/list` returned the tool schemas and rich descriptions. The implementation successfully exposed the complete tool family, including navigation, graph, write, telemetry, server, and guard-control tools.

### Skills

- The installed skill cache matched the source `skills/` directory.
- `dotnet-init`, `dotnet-read`, `dotnet-guard-audit`, and `dotnet-consistency` triggered in Codex.
- The principal remaining risk is exact MCP tool-prefix portability after the server becomes visible.

### Agents

- Installed cached agent briefs matched source.
- The Codex generic-worker adapter successfully ran the guard red-team brief.
- Native named Codex-agent registration was not present.

### Hooks

- Cached hooks matched source.
- Nine hook trust records matched the current definitions.
- Direct entrypoints and automated guard tests worked.
- Full Codex lifecycle invocation remains to be rechecked after fixing the sandbox.

## Required product/install matrix

Every release should run this matrix from a fresh profile and a clean consuming repository.

| Scenario | Profile home | Executor | Required path form | Required outcome |
|---|---|---|---|---|
| Claude Code native Windows | Claude profile | Windows | `C:\...` | plugin MCP, skills, agents, hooks; workspace loaded |
| Claude Code native Linux/WSL | Claude profile | Linux | `/...` or `/mnt/c/...` | same |
| Codex native Windows | `%USERPROFILE%\.codex` | Windows | `C:\...` | plugin enabled, MCP tools visible, skills/hooks active |
| Codex standalone WSL | `/home/<user>/.codex` | Linux | `/...` or `/mnt/c/...` | same plus `bubblewrap` available |
| Windows Codex desktop using WSL | Windows-owned Codex home mounted in WSL | Linux | `/mnt/c/...` | marketplace resolves, MCP tools visible, hooks run |

For each cell, verify both a repository on the native filesystem and, where supported, a repository on `/mnt/c`.

## One-command verification requirements

Create a verifier that prints a concise pass/fail report and exits nonzero on failure. It should check:

1. Effective host/UI and executor OS.
2. Effective `CODEX_HOME` or Claude configuration root.
3. Plugin source path and manifest resolution.
4. Plugin cache version versus source version.
5. `dist` artifact build identity versus source/manifest.
6. `dotnet` resolution and installed SDK roots.
7. Governing `global.json` and selected SDK with selection reason.
8. MCP command path existence in the executor's namespace.
9. MCP `initialize` success.
10. Exact `tools/list` names, schemas, and expected count.
11. `ping` success.
12. `workspace_status`: correct root, index ready, workspace loaded, no failed projects.
13. Exact tool names visible to skill `ToolSearch` on Claude and Codex.
14. Skill discovery and one representative skill trigger.
15. Agent discovery/adapter execution and MCP access from inside the agent.
16. Hook discovery, trust, and one safe read-advisory/write-denial probe.
17. WSL `bubblewrap` availability when WSL execution is selected.
18. No stale duplicated registrations or conflicting profiles.

The verifier must distinguish:

- configured;
- enabled;
- process started;
- protocol initialized;
- tools discovered;
- workspace healthy;
- agent can call tools;
- hook is actually invoked.

## Recommended fix order

1. Rebuild and validate `dist` against the current source; establish a source/dist CI gate.
2. Fix Codex installer path selection for native Windows, native WSL, and Windows-desktop-through-WSL.
3. Replace inventory-only verification with a live protocol/tool/workspace verifier.
4. Resolve marketplace path evaluation under Windows desktop + WSL.
5. Validate actual Codex MCP tool prefixes and make shared skills host-neutral.
6. Decide and implement the Codex agent model: documented adapter or native `.codex/agents/*.toml` installation.
7. Add/preflight the WSL `bubblewrap` requirement.
8. Re-run end-to-end hook interception and close high-risk write-scanner gaps.
9. Unify build identity across both manifests and the server.
10. Execute the complete host matrix from clean profiles.

## Acceptance criteria for the follow-up audit

The installation is accepted only when all of the following hold in a newly created Codex task, without manual recovery during the task:

- `codex plugin list --json` resolves dotnet-toolkit without warnings.
- `codex mcp list --json` reports the intended command and executor-valid path.
- The task exposes the complete dotnet-toolkit MCP tool set.
- `ping` succeeds.
- `workspace_status` reports the expected solution, ready index, and loaded projects.
- `search_index` and `get_symbol` return healthy results without `limitedBy: degraded`.
- `validate_patch` can perform a dry-run and rejects stale base versions.
- Every skill can resolve its exact MCP tool names.
- Review/explore/red-team agents can launch and call their granted MCP tools.
- All nine hooks are discovered and trusted.
- A safe C# read probe produces the intended advisory and a safe non-applying write probe is denied.
- No `bubblewrap` or path-namespace errors occur.
- The runtime build identifier matches the installed plugin/cache identifier.
- The same release continues to pass Claude Code's native installation tests.

## Official Codex references used

- MCP configuration and STDIO lifecycle: https://learn.chatgpt.com/docs/extend/mcp
- WSL execution model and `bubblewrap`: https://learn.chatgpt.com/docs/windows/wsl
- Plugin packaging: https://learn.chatgpt.com/docs/build-plugins
- Custom Codex agents: https://learn.chatgpt.com/docs/agent-configuration/subagents
- Hooks and plugin hook trust: https://learn.chatgpt.com/docs/hooks
- AGENTS.md discovery: https://learn.chatgpt.com/docs/agent-configuration/agents-md

## Audit limitations

- The active MCP was broken, so normal in-task dotnet MCP tool calls were unavailable. Source inspection used a temporary successful stdio MCP session against the same `dist` DLL.
- The repository was already dirty. No attempt was made to attribute pre-existing edits to a specific author or to reset them.
- End-to-end hook interception was blocked in several cases by missing `bubblewrap`; direct hook behavior and automated tests were used instead.
- The suspected Claude-versus-Codex MCP tool-prefix mismatch must be confirmed after the Codex registration is fixed.
- Claude Code was reported by the user to work well and was not reinstalled or destructively reconfigured during this audit.
