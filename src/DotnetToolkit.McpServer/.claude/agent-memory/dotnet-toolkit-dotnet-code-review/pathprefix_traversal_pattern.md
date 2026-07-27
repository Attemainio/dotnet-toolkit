---
name: pathprefix-traversal-pattern
description: SolutionLocator.AbsPath uses string.StartsWith(Root) without a separator boundary check — a real path-traversal bypass to flag if seen elsewhere in this repo
metadata:
  type: project
---

`SolutionLocator.AbsPath` (src/DotnetToolkit.McpServer/Workspace/SolutionLocator.cs) validates that a
resolved absolute path stays under `Root` with `abs.StartsWith(Root, StringComparison.Ordinal)`. This
passes for a sibling directory that merely shares the prefix (`Root` = `/foo/bar`, escape target =
`/foo/barbaz`), so the documented "rejecting escapes above the root" guarantee has a real bypass.
Correct form needs an exact-match-or-`Root + DirectorySeparatorChar` check.

**Why:** flagged during a scope review of `Workspace/` + `Git/` (2026-07-28). `AbsPath` is called with
caller-influenced relative paths from several MCP tools (`get_scope`'s `file` param, etc.), so this is a
reachable check, not just theoretical.

**How to apply:** if another prefix-based containment check (`path.StartsWith(root)`) turns up elsewhere
in this codebase during a future review pass, flag it the same way — this is a recurring bug shape, not
a one-off.
