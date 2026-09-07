# Codex on Windows + WSL2 — fixes for the 2026-09-07 validation

Response to the post-fix validation of
[`codex-windows-wsl-audit-2026-09-06.md`](codex-windows-wsl-audit-2026-09-06.md), which reported
eight remaining blockers. Written as a companion file because the validation itself could not be
appended — `bubblewrap` was missing, so the patch utility could not reach the repository.

**Seven of the nine blockers were mechanically fixable, and three of those were defects in this
repository rather than configuration.** A ninth blocker — plugin packaging — was raised by the
follow-up validation, confirmed, and fixed. One is genuinely Codex-side and is documented rather than
fixed. `bubblewrap` was installed by the user during this pass.

## Status

| Check | Before | After |
| --- | --- | --- |
| `doctor` on the Windows-owned profile (`/mnt/c/Users/atte9/.codex`) | 2 failures | **PASS** — 26 ok, 2 warnings, 0 not checked |
| `doctor` on the WSL profile (`/home/atte/.codex`) | PASS | PASS |
| Live MCP handshake probe | not runnable (no `bwrap`) | **19 tools discovered, workspace loaded** |
| `dotnet test` | 205 failed / 541 passed | **745 passed / 1 failed** (of 746) |
| `dist` vs `src` | current | current (`5E767AB1FC6B`) |

The full probe, which the missing sandbox previously blocked end to end:

```
live session:
  [ ok ] process started: yes
  [ ok ] protocol initialized: 2025-06-18, DotnetToolkit.McpServer 1.0.1.0
  [ ok ] tools discovered: 19: detect_circular_dependencies, ping, workspace_status, …
  [ ok ] ping: pong dotnet-toolkit/1.0.1+build.4b9ec566
  [ ok ] workspace root: /mnt/c/Users/atte9/source/repos/dotnet-toolkit
  [ ok ] index: ready 179 files, 294 types
  [ ok ] projects: loaded 2 projects in 2.7s
  [ ok ] server sdk choice: MSBuild 10.0.301 (/home/atte/.dotnet/sdk/10.0.301)

PASS — 26 ok, 2 warning(s), 0 not checked
```

The one remaining test failure is a pre-existing, order-dependent flake
(`GetSymbol_ExplicitIncludeListIsExactlyTheNamedComponents`): it passes in isolation, and was
confirmed failing at a clean `HEAD` in a separate worktree with none of this work present.

## The structural fix: a portable Codex MCP declaration

Blockers 1, 2 and 4 all reduced to the same thing — a launch path stored in `config.toml` can only be
spelled for one OS, and a Windows Codex desktop app executing in WSL2 reads a Windows-owned profile
from a Linux process. Rewriting the path fixes one environment and breaks the other.

Codex's own bundled `unified-computer-use` plugin turned out to demonstrate the mechanism we were not
using: it declares its MCP server with a **plugin-relative** path and no absolute path or variable
anywhere. Codex resolves such an argument against the plugin's install location while still launching
the server in the *session's* working directory — so the server stays project-aware. That is the same
guarantee `${CLAUDE_PLUGIN_ROOT}` provides on the Claude side.

The plugin now ships that declaration:

```jsonc
// .codex-plugin/plugin.json
"mcpServers": "./.codex-plugin/mcp.json"

// .codex-plugin/mcp.json
{ "mcpServers": { "dotnet": { "command": "dotnet",
                              "args": ["dist/DotnetToolkit.McpServer.dll"],
                              "startup_timeout_sec": 120 } } }
```

`codex plugin add` now delivers skills, hooks **and** tools in one step. No `codex mcp add`, and no
absolute path in any profile.

### `config.toml` is an override layer, not an alternative

The decisive evidence: Codex's `cua_repl` appears in *both* a plugin `.mcp.json` and the user's
`config.toml`. The two **merge, with the profile winning**. So the existing `[mcp_servers.dotnet]`
block was not a parallel registration — it was actively overriding the portable path with a
Windows-only one. Both profiles had that block removed (each backed up first as
`config.toml.bak-<timestamp>`) and a comment left in its place explaining why.

Per-tool policy (`[mcp_servers.dotnet.tools.<name>] approval_mode = "approve"`) is genuinely the
user's and stays in the profile; it merges cleanly.

## The defects behind blockers 7 and 8

### Blocker 7 — 205 test failures were a product bug, not the environment

The reported diagnosis (redirected pipes failing to close) described the symptom, not the cause. Both
were reproduced here.

`MSBuildRegistration.Register()` points `MSBUILD_EXE_PATH`, `MSBuildExtensionsPath` and
`MSBuildSDKsPath` at one SDK **for the whole process**, and every `dotnet` child inherits them. On
this machine the test host runs on 10.0.111 (`/usr/lib/dotnet`) while the registered SDK is 10.0.301
(`~/.dotnet`), so `dotnet restore` was handed a foreign MSBuild:

```
10.0.111 host + 10.0.301 MSBUILD_EXE_PATH  →  exit=1, nothing written to either stream
same host, those three variables cleared   →  exit=0, restored in 90 ms
```

**The silence is the trap.** A child that fails with no output is indistinguishable from one whose
pipe is held open, which is why the symptom pointed at process plumbing while the cause was an
environment variable.

A second, independent finding: `MSBUILDDISABLENODEREUSE=1` does **not** stop MSBuild worker nodes
surviving. Measured with both documented switches set, `dotnet restore` returns in 0.78 s and leaves
**ten** nodes holding the inherited pipe write-ends. Waiting for EOF therefore hangs regardless.

Fixes:

- `MSBuildRegistration.ScrubInheritedRegistration(psi)` — new shared helper, owned by the type that
  injects the variables.
- Called at all three call sites: `Validation/TargetedTests.cs` (runs a bare `dotnet` off `PATH` —
  the broken pairing), `Workspace/WorkspaceHost.cs` (`RestoreAsync`), and the integration fixture.
  The first two are **product code**, so this was affecting `validate_patch`'s `targeted_tests` rung
  and workspace restore on any multi-SDK machine, not only the test suite.
- The fixture no longer treats stream EOF as a completion signal. Process exit is the contract; the
  drain is best-effort with a short grace and only decorates a failure message.
- `Captured()` now distinguishes a still-running drain from a faulted read, so the next occurrence is
  diagnosable from test output alone.

The invariant in `CLAUDE.md` was incomplete and has been rewritten to carry both rules.

### Blocker 8 — the `doctor` marketplace false positive

Confirmed, and worse than reported: `CheckMarketplace` judged **every** local marketplace against
this repository's layout, so it failed `openai-bundled` and advised repointing it at a dotnet-toolkit
clone. Following that advice would have broken a working Codex installation.

Two changes:

1. Codex's own layout, `.agents/plugins/marketplace.json`, is now a recognized manifest alongside
   `.codex-plugin/plugin.json` and `.claude-plugin/marketplace.json`.
2. Ownership is decided by resolving the source against `pluginRoot` **after** namespace translation.
   Only our own entry gets a verdict and a repair; a foreign entry in an unreadable layout is
   skipped, and a foreign entry in the wrong path namespace is a warning — information, not a failed
   install.

`doctor` also now recognizes a plugin-manifest declaration as a valid registration, so the portable
route is not reported as "no MCP tools".

## Blocker 9 — plugin packaging (raised after the first pass, confirmed)

The follow-up validation was right to call the zero-tools claim premature: `doctor` proved the server
runs **from the source tree**, not that Codex can install a snapshot and launch it from its cache.
`codex plugin add` failed with `failed to copy plugin file: Permission denied (os error 13)`.

Reproduced, and the suspected cause confirmed exactly. The installer snapshots the marketplace root
verbatim, and the marketplace pointed at the whole development repository:

```
$ find . -type f -print0 | xargs -0 -P8 -I{} sh -c 'head -c1 "$1" >/dev/null 2>&1 || echo "DENIED: $1"' _ {}
DENIED: ./.vs/dotnet-toolkit.slnx/FileContentIndex/4716c1cf-….vsidx
DENIED: ./.vs/dotnet-toolkit.slnx/FileContentIndex/30c92386-….vsidx
… 5 files, all Visual Studio's content index
```

Visual Studio holds those under a deny-read share lock. POSIX bits are useless here — DrvFs reports
`777` for every file on `/mnt/c`, so the denial only appears on an actual open. Scale was the second
half of the problem: **632 MB / 2038 files** snapshotted for a plugin whose payload is 50 MB.

**Fix:** `scripts/package-plugin.sh` and `scripts/package-plugin.cmd` build a clean `package/`
directory from an explicit allow-list (`.claude-plugin`, `.codex-plugin`, `skills`, `agents`,
`hooks`, `standards`, `docs`, `dist`, `.claude/rules`, `AGENTS.md`, `README.md`, `LICENSE.txt`), then
**assert** that `.git`, `.vs`, `src`, `tests`, `obj`, `bin` and `scripts` did not leak in — an
allow-list alone would silently ship whatever a later commit adds at the root. Result: **50 MB / 234
files**. `package/` is gitignored, because it is a build output assembled from tracked files.

A fresh `git clone` contains none of `.vs`, `bin` or `obj`, so installing from a **remote**
marketplace was never affected. This is specifically about a local marketplace whose source is also a
live working tree.

### Verified end to end in a disposable `CODEX_HOME`

```
codex plugin marketplace add …/dotnet-toolkit/package   → Added marketplace `dotnet-toolkit`
codex plugin add dotnet-toolkit@dotnet-toolkit          → Installed plugin root:
                                                          …/cache/dotnet-toolkit/1.0.1+codex.20260907152403
codex mcp list                                          → dotnet | dotnet | dist/DotnetToolkit.McpServer.dll | enabled
```

Then `doctor` run **from the installed cache**, not the source tree:

```
  [ ok ] codex manifest: 1.0.1+codex.20260907152403
  [skip] dist vs src: no src/ here, so this is an installed plugin rather than a clone
  [ ok ] mcp_servers.dotnet: declared by the plugin manifest (…/cache/…/dist/DotnetToolkit.McpServer.dll)
  [ ok ] tools discovered: 19
  [ ok ] workspace root: /mnt/c/Users/atte9/source/repos/dotnet-toolkit
  [ ok ] index: ready 179 files, 294 types
```

That settles four things at once, including the one property the previous report listed as unproven:

- `codex plugin add` succeeds (recommended sequence step 3).
- The cached version is `1.0.1+codex.20260907152403` (step 4), and the three previously stale skills
  now match source byte for byte.
- **`codex mcp list` contains `dotnet`** (step 5) — the manifest declaration genuinely registers the
  server, with the relative arg recorded as-is.
- **`workspace root` is the target repository, not the plugin cache.** A plugin-relative argument
  does not drag the working directory along with it, so the server stays project-aware.

## Blocker-by-blocker

| # | Blocker | Outcome |
| --- | --- | --- |
| 1 | Windows-owned profile in effect, zero MCP tools | **Fixed** — manifest declaration; no per-profile path |
| 2 | Wrong DLL path in `config.toml` | **Fixed structurally** — override block removed from both profiles |
| 3 | `\\?\C:\…` marketplace source | **Fixed** here; root cause is Codex-side (below) |
| 4 | The inactive WSL profile was repaired instead | **Fixed** — both profiles pass; Windows-owned one verified |
| 5 | Three stale cached skills | **Version bumped** to `1.0.1+codex.20260907152403`; needs a reinstall |
| 6 | `bubblewrap` missing | **Installed** — `bubblewrap 0.9.0` at `/usr/bin/bwrap`; probe now runs |
| 7 | 205 test failures | **Fixed** — see above; 745/746 |
| 8 | `doctor` marketplace false positive | **Fixed** — see above; covered by 14 new tests |
| 9 | `codex plugin add` fails, `Permission denied` | **Fixed** — `scripts/package-plugin.sh\|.cmd`; install verified in a disposable profile |

Confirmed correct in the source and needing no change: the three repaired skills are already
host-neutral. They name the Claude prefix as an example and instruct the reader to resolve the actual
prefix by probing. Only the September 2 **cache** hard-codes it.

## Genuinely Codex-side

`codex plugin marketplace add` run on the Windows side stores a `\\?\C:\…` extended-length path,
which a WSL executor cannot resolve — reported as
`marketplace root does not contain a supported manifest`. The user's `config.toml` contains **both**
spellings simultaneously: `/mnt/c/…` for `openai-bundled`, `\\?\C:\…` for `openai-primary-runtime`.
That is one configuration file with a Windows writer and a Linux writer.

A **local** marketplace source must be absolute, so this is the one setting the manifest cannot make
portable. It was corrected by hand and is documented in `docs/installation.md`; `doctor` names both
the mismatch and the replacement path. Re-running `marketplace add` from the Windows side will
reintroduce it.

## Changes

**New**

- `.codex-plugin/mcp.json` — the portable server declaration
- `scripts/package-plugin.sh`, `scripts/package-plugin.cmd` — the clean distribution build
- `tests/…/DoctorInstallationTests.cs` — 14 tests over marketplace ownership and manifest
  registration

**Modified**

- `src/…/Diagnostics/Doctor.cs` — marketplace ownership, Codex manifest layout, `DeclaredInManifest`
- `src/…/Workspace/MSBuildRegistration.cs` — `ScrubInheritedRegistration`
- `src/…/Validation/TargetedTests.cs`, `src/…/Workspace/WorkspaceHost.cs` — scrub before launch
- `tests/…/WorkspaceIntegrationTests.cs` — completion signal, drain grace, diagnostics
- `.codex-plugin/plugin.json` — `mcpServers` reference + version bump
- `CLAUDE.md` — the child-process invariant, rewritten with both rules
- `docs/installation.md` — Codex section rewritten; the old claim that the plugin bundle ships no MCP
  tools, and that a relative path cannot both anchor at the plugin and inherit the workspace, is
  disproven and removed

**Outside the repo** (backed up first): both `config.toml` files.

## Next steps

**Already proven** (see Blocker 9): `codex plugin add` succeeds, the cache holds
`1.0.1+codex.20260907152403`, `codex mcp list` contains `dotnet`, the cached binary launches and
exposes 19 tools, and its workspace root is the project rather than the plugin directory.

**The live profile has already been migrated** while Codex was closed, from a WSL shell so the
marketplace source is stored in the executor's namespace:

```
marketplaces.dotnet-toolkit → /mnt/c/Users/atte9/source/repos/dotnet-toolkit/package
plugin cache               → 1.0.1+codex.20260907152403
codex mcp list             → dotnet | dotnet | dist/DotnetToolkit.McpServer.dll | enabled
doctor                     → PASS, 19 tools, workspace loaded
```

All nine hook trust records survived the reinstall — `hooks/hooks.json` is copied byte-identical, so
its hashes still match. `config.toml` was backed up again beforehand.

**Do these, in order**

1. **Commit.** `dist/` and `dist/build-manifest.json` go in the **same commit** as the `src/`
   changes — the staleness gate fails if they are split. Run
   `dotnet dist/DotnetToolkit.McpServer.dll doctor --no-probe` first. `package/` is gitignored and
   must not be committed.
2. **Repackage after any change to `src/`, `skills/`, `agents/`, `hooks/`, `standards/` or `docs/`,
   then reinstall.** `package/` is a snapshot, so it goes stale exactly like `dist/` does:
   ```bash
   scripts/build-plugin.sh && scripts/package-plugin.sh
   codex plugin remove dotnet-toolkit@dotnet-toolkit
   codex plugin add dotnet-toolkit@dotnet-toolkit
   ```
   Run it from a shell in the **same place Codex executes** — WSL, for a desktop app configured with
   `runCodexInWindowsSubsystemForLinux`. Re-running `marketplace add` from the Windows side is what
   reintroduces the `\\?\C:\…` source.

**Then, in a new Codex Desktop task**

4. Call a real tool — `workspace_status` first, then `search_index`, `get_symbol`, and a dry-run
   `validate_patch` against a real repository such as FluidScript. Tools listed in a menu is not the
   same claim as tools that answer.
5. **Verify automatic hook dispatch.** Unblocked by `bubblewrap`; it was the last item the missing
   sandbox made untestable, and the only one of the original findings never exercised end to end.
6. Test **skill routing** and one **agent delegation**, which the packaging failure has masked
   throughout.
7. **Test native Windows Codex.** The portable declaration is argued from Codex's own bundled plugin
   and now verified in WSL, but the Windows half remains inference until exercised.

**Follow-ups in this repository**

7. `doctor`'s probe-target line still reads *"the configured entry is not launchable here"* when
   there is simply no profile entry because the manifest declares it. Cosmetic, but misleading.
8. The order-dependent flake in `GetSymbol_ExplicitIncludeListIsExactlyTheNamedComponents` remains
   unexplained; it predates this work.
9. No CI workflow yet. `doctor --no-probe` is the runnable gate — the audit's original request
   ("rebuild `dist` and fail if artifacts differ") cannot work as written, because the module version
   id changes on every compile and a byte-diff would fail on every correct commit. Wiring the gate
   into a workflow needs runner and SDK decisions.
