# Common .NET anti-patterns

Shared catalog read by `dotnet-reviewer`, `dotnet-performance`, and `dotnet-refactor-cleaner`. Each entry
names the pattern, why it's a problem, and which agent owns flagging it — so all three recognize the same
vocabulary without three divergent definitions of the same issue.

## Correctness & design (dotnet-reviewer)

- **Swallowed exceptions** — `catch { }` or `catch (Exception) { /* nothing */ }` that discards the
  failure. The caller proceeds as if the operation succeeded. Logging without rethrowing is only correct
  when the caller can genuinely continue with degraded/missing state; otherwise it must rethrow.
- **Exceptions for control flow** — throwing/catching to implement a normal, expected branch (e.g.
  `TryParse`-shaped logic done via `throw`/`catch` instead of a `bool`/`Try*` return). Expensive and
  obscures intent.
- **God classes / god methods** — a class or method that has grown to own unrelated responsibilities
  because it was the easiest place to add "just one more thing." Usually visible as a class with many
  unrelated public methods, or a method whose name no longer describes what it does.
- **Service-locator misuse** — resolving dependencies from a container/`IServiceProvider` deep inside
  business logic instead of constructor injection. Hides the real dependency graph and breaks testability.
- **Leaky abstractions** — an interface or base class whose members expose implementation details of one
  specific implementer (e.g. a method that only makes sense for the SQL-backed implementation, forced onto
  an interface with an in-memory implementer too).
- **Primitive obsession** — using bare `string`/`int`/`Guid` for a concept with its own validation rules
  (an email, a money amount, an ID that must never mix with another entity's ID) instead of a small wrapper
  type. Flag when the same raw-primitive validation is repeated at multiple call sites.
- **Mutable shared state without synchronization** — a `static` field or singleton-scoped field mutated
  from multiple call paths with no lock/`Interlocked`/immutability. Correctness bug the moment two callers
  overlap.
- **Optional-parameter creep** — a method with five or more optional parameters, usually a sign it's doing
  too many variations of one job and should be split or given a request/options object.

## Performance & hot paths (dotnet-performance)

- **N+1 query/call shape** — a loop that issues one query/request/allocation per iteration where a single
  batched call would do (classic with EF Core lazy-loaded navigation properties inside a `foreach`, but the
  same shape shows up with any per-item network/IO call in a loop).
- **LINQ in a proven hot path** — `Where`/`Select`/`OrderBy` chains re-evaluated on every call inside a
  tight loop or a per-request hot path, where a `for` loop over a `Span<T>`/array avoids the enumerator
  allocation and deferred-execution overhead.
- **String concatenation in a loop** — repeated `+`/`+=` on `string` inside a loop instead of
  `StringBuilder`; each iteration allocates a new string.
- **Sync-over-async** — `.Result`/`.Wait()`/`.GetAwaiter().GetResult()` on a `Task` from synchronous code
  that could have been `async` end-to-end. Risks deadlock on a captured `SynchronizationContext` and
  blocks a thread-pool thread while waiting.
- **Async-over-sync** — wrapping genuinely synchronous, non-blocking work in `Task.Run` just to make a
  method `async`, adding thread-pool scheduling overhead for no I/O-bound reason.
- **Unbounded caching** — an in-memory cache (`Dictionary`, `ConcurrentDictionary`, `MemoryCache`) that
  never evicts, sized by unbounded user/request input — a slow memory leak under load.
- **Missing `ConfigureAwait` context awareness** — not itself always wrong, but flag when library code
  (no UI/request context to resume on) awaits without considering `ConfigureAwait(false)`, especially in
  code that's hot enough for the context-capture overhead to matter.
- **Boxing a value type in a hot path** — passing a `struct` through an `object`-typed parameter,
  non-generic collection, or `IComparable`/`IFormattable` call site inside a loop that runs per-request or
  per-tick.

## Cleanup & duplication (dotnet-refactor-cleaner)

- **Copy-pasted logic** — the same (or near-same, with minor variable renames) block of logic appearing in
  two or more places instead of a shared method. Flag once per duplicated shape, listing every occurrence.
- **Dead code** — a type, method, field, or property with zero references anywhere in the solution
  (verified via `get_references`, never guessed from a text search). Includes commented-out code blocks
  left in place "just in case."
- **Orphaned single-caller abstraction** — an interface or abstract base class with exactly one
  implementer and one caller, added for a flexibility need that never materialized. Not every
  single-implementer interface is wrong (some exist for testability), but flag ones with no evidence of
  that intent (no test double, no DI-swap use case visible).
- **Half-migrated pattern** — two different ways of doing the same thing coexisting in one codebase (e.g.
  half the services use one error-handling convention, half use another) because a refactor was started
  and never finished. Flag the split itself, not just one side of it.
- **Unused `using` directives and unused parameters** — routine noise, but real: report them, note that
  `dotnet format` can auto-fix the `using` case specifically.

## Applying this catalog

An agent citing an anti-pattern from this list should still say *why it matters in this specific piece of
code* — citing the catalog entry name is a starting point for the finding, not a substitute for the
reasoning.
