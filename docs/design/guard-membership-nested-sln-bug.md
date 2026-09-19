# Bug: guard hooks silently ungoverned on any project with its own nested `.sln`

> **Historical record.** The write guards this bug most affected (`guard-cs-edit`,
> `guard-cs-bash-write`) were removed on 2026-09-19 — `Edit`/`Write` are the write path now. The
> `CsFileMembership` fix described here still governs the two advisory read guards and the
> `refresh-cs-files` hook's "is this file in the solution" check, which is why the file stays.

**Found:** 2026-08-22, during a `dotnet-performance` benchmark run against PandaAI
**Severity:** High — the write guard (`guard-cs-edit`), which is documented as a hard, unconditional
deny, does not fire at all on the affected files. This is not a benchmark artifact; it is a standing
gap in this repo's actual enforcement, for as long as the plugin has been installed there.
**Where:** `src/DotnetToolkit.McpServer/Hooks/CsFileMembership.cs`,
`TryResolveOwningProject` (shared by `guard-cs-edit`, `guard-cs-read`, `guard-cs-bash-read`,
`guard-cs-bash-write`)

## Symptom that surfaced it

While running `dotnet-performance`'s third pass (`set_hook_guards(state: "block")`, meant to force
`guard-cs-read` back to a hard deny), the positive control kept failing: a plain `Read` on
`PandaAI.Core/Utilities/Services/BinanceService/BinanceGateway.cs` was allowed through with **no**
denial and **no** advisory note — not even the default advisory that should appear with the guards at
their normal `Active` state. Restarting the MCP server (to rule out the documented session-id-mismatch
cause) made no difference. That absence of *any* signal, even under plain default state, was the tell
that this was not a suspend/block-state problem at all — the membership check itself was returning
"not governed" for a file that plainly is.

## Root cause

`TryResolveOwningProject` walks from a `.cs` file up toward the configured repo root looking for the
nearest `.csproj`. While climbing, at any directory **strictly between the file and the root**, it also
checks for a `.sln`/`.slnx`:

```csharp
// A nested solution root means an independent solution owns everything below it.
if (!PathsEqual(directory, root)
    && (EnumerateFiles(directory, "*.slnx").Any() || EnumerateFiles(directory, "*.sln").Any()))
{
    return false;
}
```

Finding one there makes the walk conclude the file belongs to its own **independent, excluded**
solution — modeled on dotnet-toolkit's own `tests/**/fixtures/SampleSolution`, a deliberately-excluded
test fixture that is genuinely not part of the loaded workspace. `return false` here means "not
governed," which for every guard means "allow, no advisory, no deny" — the file is treated as if it
were outside this repo's solution entirely.

**This rule cannot distinguish "genuinely independent/excluded solution" from "a project that also
ships its own per-project `.sln` for convenience,"** and the second case is a completely ordinary,
common .NET layout — each project openable standalone in Visual Studio, in addition to belonging to a
top-level multi-project solution. PandaAI is built exactly this way:

```
PandaAI/
  PandaAI.sln                       <- top-level solution, this is what workspace_status loads
  PandaAI.Core/
    PandaAI.Core.csproj
    PandaAI.Core.sln                <- ALSO exists, standalone-open convenience
  PandaAI.WebHost/
    PandaAI.WebHost.csproj
    PandaAI.WebHost.sln             <- ALSO exists
  PandaAI.GPUTrainer/
    PandaAI.GPUTrainer.csproj       <- no nested .sln here
```

`PandaAI.Core` and `PandaAI.WebHost` — which is essentially the entire codebase — both have their own
nested `.sln`. `PandaAI.GPUTrainer` does not. The walk hits `PandaAI.Core.sln` (or
`PandaAI.WebHost.sln`) before it ever reaches the root, decides the file is independently-solutioned,
and every guard backs off.

**Why this was never caught dogfooding:** dotnet-toolkit's own repo has exactly one `.sln`/`.slnx` at
the root (`dotnet-toolkit.slnx`) and no per-project nested solutions anywhere except the one deliberate
test fixture the rule was written for. The false-positive shape this bug produces simply never occurs
there.

## Confirmed impact (direct reproduction)

Invoked the published hook binary exactly as `hooks.json` does, with a payload shaped like what Claude
Code actually sends (absolute `file_path`, real `session_id`, `tool_input` object):

```bash
echo '{"tool_name":"Read","tool_input":{"file_path":"/mnt/.../PandaAI.Core/Utilities/Services/BinanceService/BinanceGateway.cs"},...}' \
  | dotnet dist/DotnetToolkit.McpServer.dll hook guard-cs-read
# exit 0, ZERO stdout — not even the default advisory JSON

echo '{"tool_name":"Edit","tool_input":{"file_path":"/mnt/.../PandaAI.Core/.../BinanceGateway.cs",...},...}' \
  | dotnet dist/DotnetToolkit.McpServer.dll hook guard-cs-edit
# exit 0 — the hard-deny write guard ALLOWS the edit
```

Compared against a `PandaAI.GPUTrainer` file (no nested `.sln`), which behaves exactly as documented:

| File | Nested `.sln` between file and root? | `guard-cs-read` | `guard-cs-edit` |
|---|---|---|---|
| `PandaAI.Core/Utilities/Services/BinanceService/BinanceGateway.cs` | yes (`PandaAI.Core.sln`) | plain allow, **no advisory** | plain allow, **no deny** (bug) |
| `PandaAI.GPUTrainer/Program.cs` | no | allow **with advisory JSON** | **denied**, exit 2 — correct |

So in this repo, as installed today, the write guard has been enforcing on `PandaAI.GPUTrainer` only.
`PandaAI.Core` and `PandaAI.WebHost` — the two projects the actual C# work in this repo lives in — have
had no working `guard-cs-edit`/`guard-cs-bash-write` enforcement, and no `guard-cs-read` advisory
either, for as long as this pattern has existed in the repo layout.

`guard-cs-bash-read` and `guard-cs-bash-write` share the same `CsFileMembership` call and were not
independently re-verified in this session, but by the same logic they are affected identically — the
bug is in the shared membership function, not in anything guard-cs-read–specific.

## Why this also broke today's benchmark's Pass 3

`GuardCsRead.Evaluate` only consults the `blocked` parameter (from `set_hook_guards(state: "block")`)
**after** `CsFileMembership.TryResolveOwningProject` has already returned true. For a `PandaAI.Core`
file that check now returns false, so `blocked` is never reached — `set_hook_guards(state: "block")`
had no observable effect on any `PandaAI.Core`/`PandaAI.WebHost` file, no matter how many times the MCP
server was restarted. Pointing the positive control at a `PandaAI.GPUTrainer` file instead would have
worked, and did in an ad hoc check during this investigation.

This also means Pass 2's suspend-state positive control (`Bash head -1` on `BinanceGateway.cs`, "no
advisory note appeared" ⇒ concluded suspension was real) is **not actually distinguishing "suspended"
from "membership bug"** — both produce the identical observable (no advisory). The practical result for
that pass was still correct (raw reads were unblocked either way, which is what the pass needed), but
the check itself was not the proof it was written to be, on this repo.

## Suggested fix directions (not implemented — for you to choose from)

1. **Cross-check against the loaded solution's actual project list**, not just "a `.sln` file exists at
   this level." If the nested `.sln` is discoverable as *also* referencing the same `.csproj` the root
   solution loads (or, cheaper: if the nested `.sln`'s directory is itself listed as a project folder
   inside the root `.sln`/`.slnx`), it is not independent — don't exempt it.
2. **Only exempt a nested `.sln` when the file's owning `.csproj` is NOT also reachable from the
   configured root solution.** This is the actual distinguishing fact ("genuinely a different,
   uninvolved solution" vs. "same project, also has its own solution file") and would need parsing the
   root `.sln`/`.slnx`'s project list, which the plugin's `WorkspaceHost`/`SolutionLocator` already does
   at the MCP-tool layer — the hook process just doesn't have it available statically today.
3. **Cheaper, narrower heuristic:** only treat a nested `.sln` as exempting when the `.csproj` found
   during the walk is *not* itself referenced anywhere in the root solution file's text (a text search
   over the `.sln`'s project GUID block, avoiding a full parse). Would correctly handle both PandaAI's
   case and the original fixture case without needing MSBuild.
4. Regardless of which fix is chosen, add a test fixture shaped like PandaAI's layout — a root solution
   plus a project that also ships its own per-project `.sln` and *is* included in the root solution —
   alongside the existing excluded-fixture test, so this exact false positive has permanent coverage.

## Reproduction environment

- Repo: PandaAI (`/mnt/c/Users/atte9/source/repos/PandaAI`), solution `PandaAI.sln`, 3 loaded projects
  (`PandaAI.Core`, `PandaAI.GPUTrainer`, `PandaAI.WebHost`)
- dotnet-toolkit checkout: `/mnt/c/Users/atte9/source/repos/dotnet-toolkit`, branch `main`, working tree
  has uncommitted changes (the in-progress `Blocked`-mode feature — unrelated to this bug, which
  reproduces identically under plain `Active` state)
- `dist/DotnetToolkit.McpServer.dll` built 2026-08-22 08:46 local, from the current (uncommitted)
  source tree
- WSL2, .NET 10.0.111 runtime
