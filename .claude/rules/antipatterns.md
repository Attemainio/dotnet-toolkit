---
paths:
  - "**/*.cs"
---

# Common .NET anti-patterns

Shared catalog read by `dotnet-code-review` and by the main agent before writing C# (per
`csharp-standards.md`'s index). Each entry names the pattern, why it's a problem, and which **aspect**
owns flagging it — `[correctness]`, `[concurrency]`, `[performance]`, `[cleanup]`, or `[security]` — so
every finding cites one shared vocabulary instead of divergent definitions of the same issue. A
consuming repo overrides it via `.claude/dotnet-toolkit/antipatterns.md`.

## Correctness & design (correctness)

- **Swallowed exceptions** — `catch { }` or `catch (Exception) { /* nothing */ }` that discards the
  failure; the caller proceeds as if the operation succeeded. Logging without rethrowing is only correct
  when the caller can genuinely continue with degraded/missing state.
- **Exceptions for control flow** — throwing/catching to implement a normal, expected branch
  (`TryParse`-shaped logic done via `throw`/`catch` instead of a `bool`/`Try*` return). Expensive and
  obscures intent.
- **God classes / god methods** — a class or method grown to own unrelated responsibilities because it
  was the easiest place to add "just one more thing." Visible as a class with many unrelated public
  methods, or a method whose name no longer describes what it does.
- **Service-locator misuse** — resolving dependencies from a container/`IServiceProvider` deep inside
  business logic instead of constructor injection. Hides the real dependency graph and breaks
  testability.
- **Leaky abstractions** — an interface or base class whose members expose one specific implementer's
  details (a method that only makes sense for the SQL-backed implementation, forced onto an interface
  with an in-memory implementer too).
- **Primitive obsession** — bare `string`/`int`/`Guid` for a concept with its own validation rules (an
  email, a money amount, an ID that must never mix with another entity's ID) instead of a small wrapper
  type. Flag when the same raw-primitive validation repeats at multiple call sites.
- **Optional-parameter creep** — five or more optional parameters, usually a sign the method is doing too
  many variations of one job and should be split or given an options object.

## Concurrency (concurrency)

Definitions and the full standard live in `concurrency.md`; these are the catalog names.

- **Sync-over-async** — `.Result`/`.Wait()`/`.GetAwaiter().GetResult()` on a `Task` from synchronous code
  that could have been `async` end-to-end. Risks deadlock on a captured `SynchronizationContext` and
  blocks a thread-pool thread while waiting.
- **Async-over-sync** — wrapping genuinely synchronous, non-blocking work in `Task.Run` just to make a
  method `async`, adding scheduling overhead for no I/O-bound reason.
- **Mutable shared state without synchronization** — a `static` or singleton-scoped field mutated from
  multiple call paths with no lock/`Interlocked`/immutability. A correctness bug the moment two callers
  overlap.
- **`async void`** — outside actual event handlers; exceptions escape the caller and crash the process.
- **Blocking inside a lock** — I/O, `.Result`, or a long computation while holding a lock; the classic
  first half of a deadlock or a contention collapse.
- **Sequential draining of redirected process streams** — reading a child process's stdout to end before
  starting stderr (or vice versa); deadlocks when the unread pipe's buffer fills. Drain both
  concurrently.

## Performance & hot paths (performance)

- **N+1 query/call shape** — a loop issuing one query/request per iteration where a single batched call
  would do (classic with EF Core lazy-loaded navigation properties inside a `foreach`; same shape with
  any per-item network/IO call in a loop).
- **LINQ in a proven hot path** — `Where`/`Select`/`OrderBy` chains re-evaluated per call inside a tight
  loop or per-request hot path, where a `for` loop over a `Span<T>`/array avoids enumerator allocation
  and deferred-execution overhead.
- **String concatenation in a loop** — repeated `+`/`+=` on `string` inside a loop instead of
  `StringBuilder`; each iteration allocates a new string.
- **Unbounded caching** — an in-memory cache (`Dictionary`, `ConcurrentDictionary`, `MemoryCache`) that
  never evicts, keyed by unbounded user/request input — a slow memory leak under load.
- **Missing `ConfigureAwait` context awareness** — not itself always wrong, but flag when library code
  (no UI/request context to resume on) awaits without considering `ConfigureAwait(false)`, especially
  where the context-capture overhead matters. (Correctness aspects: `concurrency.md`.)
- **Boxing a value type in a hot path** — passing a `struct` through an `object`-typed parameter,
  non-generic collection, or interface call site inside a loop that runs per-request or per-tick.
- **Accidental O(n²)** — `Contains`/`Any`/`IndexOf` over a list inside a loop over another list, or
  nested loops over related collections where a `HashSet`/`Dictionary` lookup or a single pre-indexed
  pass would do. See `performance.md`'s nested-loop section.

## Cleanup & duplication (cleanup)

- **Copy-pasted logic** — the same (or near-same) block appearing in two or more places instead of a
  shared method. Flag once per duplicated shape, listing every occurrence.
- **Dead code** — a type, method, field, or property with zero references anywhere in the solution
  (verified via `get_references`, never guessed from a text search). Includes commented-out code blocks
  left "just in case." Never applies to a symbol `get_symbol`/`search_index` reports `origin: "external"`
  — that is a BCL/NuGet symbol this repo's own source calls, implements, or extends, not something this
  repo declares or could remove.
- **Orphaned single-caller abstraction** — an interface or abstract base with exactly one implementer and
  one caller, added for a flexibility need that never materialized. Not every single-implementer
  interface is wrong (some exist for testability); flag ones with no evidence of that intent.
- **Half-migrated pattern** — two ways of doing the same thing coexisting because a refactor was started
  and never finished. Flag the split itself, not just one side of it.
- **Unused `using` directives and unused parameters** — routine noise, but real: report them, note that
  `dotnet format` auto-fixes the `using` case.

## Security (security)

- **Hardcoded credential-shaped literal** — a connection string, API key, or token as a string literal in
  source, regardless of whether it happens to be a placeholder. See `security.md`.
- **String-built SQL** — raw SQL assembled via concatenation/interpolation instead of parameterization.
  See `security.md`.
- **Ambiguous endpoint auth** — a controller/endpoint with neither `[Authorize]` nor `[AllowAnonymous]`
  stated explicitly. See `security.md`.

## Applying this catalog

Citing an entry from this list is a starting point, not a finding by itself — say *why it matters in this
specific piece of code*, with the actual line cited.
