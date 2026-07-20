---
paths:
  - "**/*.cs"
---

# C# contact point: the MCP tools are the path

<!-- Deliberately short. CLAUDE.md is always loaded and carries the full tool table, the rationale, and
this repo's history with it; repeating that here would cost the same tokens twice on every session that
touches C#. This file is reinforcement at the moment of contact, not a second reference. -->

You are touching a `.cs` file. `search_index` / `get_symbol` / `get_references` / `get_scope` replace
Grep, Glob, `find`, `cat`, and bare `Read` here — text search cannot see interface, virtual, or
delegate dispatch, and hands back partial-class fragments with no signal the rest exists.

**Writes go through `validate_patch`.** A `PreToolUse` hook blocks `Edit`/`Write` on an existing `.cs`
file, so reaching for one costs a round trip and changes nothing. It is also the only writer to the
development log — an edit that bypasses it loses its reasoning permanently.

1. `get_symbol` — keep `contentVersion` and the `declarationSites` line span.
2. `validate_patch` with `baseVersions: {symbolId: contentVersion}` and line-span `edits`,
   `applyOnSuccess: false` — read the ladder verdict without touching disk.
3. Re-send with `applyOnSuccess: true` and an `intent` in user terms once `isSufficient: true`.

"Too large or interleaved to decompose" is not an exception — split it into more `validate_patch`
calls, one per touched symbol, sharing one `intent`. Creating a *new* file is the only real exception:
`baseVersions` needs a `symbolId` that does not exist yet.
