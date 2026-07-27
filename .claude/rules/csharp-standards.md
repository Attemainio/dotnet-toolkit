# C# standards: what to read before touching C#

<!-- Always-loaded (no `paths:` frontmatter) — deliberately. Path-scoped rules only fire on the built-in
Read tool, and in this repo `.cs` contact goes through the MCP tools or is blocked by the guards, so a
path-scoped rule would almost never load (verified against Claude Code docs, 2026-07). This file is the
index that makes the on-demand standards discoverable; it stays short because it loads in every session.
The standards files themselves carry `paths: ["**/*.cs"]` solely to keep them out of the launch context. -->

This file is the **master index** for every coding-standards file in `.claude/rules/` — the single place
that lists which file exists and when to read it. A new standards file is not "done" until it has a row
here; nothing else in this plugin enumerates the set on its own (see "Changing the tool surface" in
`CLAUDE.md` for the full list of places a new file must also be wired into).

The canonical coding standards live beside this file in `.claude/rules/`. They are loaded **on demand**,
not automatically — before writing or editing C#, read the ones relevant to the change (the
`dotnet-change` skill makes this a required step):

| Read | When |
| --- | --- |
| `naming.md`, `styling.md`, `best-practices.md`, `xml-documentation.md` | every C# change — the baseline set |
| `architecture.md` | new/changed project or namespace boundaries, dependency direction, layering, a new abstraction |
| `api-design.md` | a public or internal API surface change: new/changed method signature, nullability, collection return types, async shape, cancellation |
| `error-handling.md` | exceptions, result/error patterns, retries, timeouts, failure propagation across a boundary |
| `resource-management.md` | `IDisposable`/`IAsyncDisposable`, streams, unmanaged resources, pooling, ownership transfer |
| `security.md` | endpoints, auth, SQL, configuration/credentials, logging, crypto |
| `performance.md` | hot paths: tight loops, per-request/per-tick code, buffers, SIMD, `unsafe` |
| `concurrency.md` | anything that awaits, locks, spawns work, or shares state across threads |
| `testing.md` | writing or modifying tests |
| `antipatterns.md` | the shared catalog — skim once per session; cited by name everywhere else |

`dotnet-code-review` validates the same files as a second pass — every aspect in one invocation, with
large targets partitioned into parallel per-scope instances — so the standards are shared, not
review-only. Consuming repos override any file via
`.claude/dotnet-toolkit/<name>.md`.

## Write-time checklist — the highest-cost-if-caught-late items

Hold these without needing a review pass to catch them:

- **No credential-shaped literal in source** — configuration comes from `IConfiguration`/environment/a
  secret store, never a string literal, even a placeholder-looking one.
- **No string-concatenated/interpolated SQL** in a raw-SQL API call — parameterize.
- **Every controller/endpoint gets an explicit `[Authorize]` or `[AllowAnonymous]`** — never an unmarked
  endpoint relying on the global default.
- **New tests exercise real dependencies, not an in-memory database substitute**, for anything asserting
  constraint/transaction/query-translation behavior the substitute doesn't share.

And the one mechanical rule: **C# edits go through `validate_patch`** (CLAUDE.md carries the full
procedure and tool table) — it is the only writer to the development log.
