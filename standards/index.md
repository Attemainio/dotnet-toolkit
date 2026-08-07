# Coding standards — which ones to read, and when

The canonical C# coding standards for every repo this plugin is installed in. Plugin-owned and the
**only** copy: they are never copied into a consuming repo, so they are always current, and there is
no per-repo override tier.

They are **not** auto-loaded. Nothing triggers them by path. Reading them is an explicit step, and
this file is the routing table for which ones that step covers.

## Reaching them

Call `workspace_status`, take its `pluginRoot:` line, and join: `<pluginRoot>/standards/<name>.md`.
**Never write `${CLAUDE_PLUGIN_ROOT}` into the path** — the harness does not expand it inside a rule
or an agent definition, so it stays literal and the read fails. `Read` the joined path; the C# guards
only block `.cs` files.

## The table

| Read | When |
|---|---|
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

## Who reads this table, and how

Two callers, with deliberately different load rules:

- **`dotnet-write`** (before the first C# edit of a session) reads the baseline set plus every row
  whose "When" matches the change about to be made, and skims `antipatterns.md` once. Writing to the
  standard beats fixing to it afterward.
- **`dotnet-code-review`** (each instance, cold) reads a fixed core every time — `naming.md`,
  `styling.md`, `best-practices.md`, `xml-documentation.md`, `antipatterns.md`, `security.md` — then
  only the rows the *retrieved code* triggers, and ends its report with a `Standards:` line naming
  what it loaded and what it did not.

Once per session is enough for either. Hold them; don't re-read per edit or per file.

## Why the "When" column is phrased the way it is

Because the review agent matches it against retrieved source, **every "When" cell must state an
observable property of the code** — it awaits, it is a hot path, it is a public surface change — not
a topic. A cell that cannot be matched against source gets silently skipped or over-loaded, and an
untriggered aspect is reported **not assessed**, never clean.

Adding a standard means adding its file here *and* deciding which of those two load rules it falls
under. A standard with no row in this table is a file nobody reads.
