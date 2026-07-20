# C# best practices

Default idiomatic-C# reference for `dotnet-code-review`'s `correctness` dimension (all points) and its
`cleanup` dimension (the duplication/abstraction points specifically). Overridable per-repo via
`.claude/dotnet-toolkit/best-practices.md` (see `CLAUDE.md`).

## Correctness

- **Null handling**: every reference-typed parameter/return not marked `?` is a claim it's never null —
  verify the claim holds at every call site, not just that the annotation exists. Every `?`-marked value
  that's dereferenced must be null-checked or pattern-matched first, not `!`-forced past the compiler.
- **Edge cases**: empty collections, empty strings, zero/negative counts, boundary indices (`length - 1`,
  `i + 1` near a loop end). Check these are handled, not just the common case.
- **Exception handling**: a `catch` block either handles the failure meaningfully (retries, falls back,
  returns a sentinel the caller understands) or rethrows after logging — never silently swallows. Catching
  `Exception` (rather than a specific type) is acceptable at a genuine boundary (top-level request handler,
  background job loop) and wrong deep inside business logic where a narrower catch would do.
- **Resource disposal**: anything implementing `IDisposable`/`IAsyncDisposable` gets a `using`/`await using`
  or an explicit `try`/`finally` `Dispose()` — never left to the GC. Flag a class that holds a `Stream`,
  `HttpClient` (see below), or other disposable field without itself implementing `IDisposable`.
- **`HttpClient` lifetime**: never `new HttpClient()` per call/request — use `IHttpClientFactory` or a
  long-lived singleton instance. Short-lived `HttpClient` instances exhaust sockets under load even though
  each individual instance disposes cleanly.

## Async

- `async` all the way up the call chain from genuinely asynchronous (I/O-bound) work — don't block on a
  `Task` partway up with `.Result`/`.Wait()` (see `common-antipatterns.md`'s sync-over-async entry), and
  don't wrap purely synchronous CPU work in `Task.Run` just to satisfy an `async` signature that didn't need
  one.
- `CancellationToken` parameters on `async` methods that call other cancellable APIs (I/O, `Task.Delay`,
  another `async` method that itself takes one) — flag a method silently swallowing the ability to cancel
  because it didn't thread the token through.
- Avoid `async void` except for actual event handlers — exceptions thrown from `async void` can't be
  awaited/caught by the caller and will crash the process instead.

## LINQ vs. loops

- LINQ (`Where`/`Select`/`OrderBy`/etc.) is the default for readability in ordinary (non-hot-path) code —
  don't flag idiomatic LINQ as a style problem outside a proven hot path. The `performance` dimension owns
  the hot-path exception to this; `correctness` should not duplicate that judgment.
- Materializing a LINQ query multiple times (`.Where(...)` reused across several `.Count()`/`.Any()`/
  `.ToList()` calls without an intermediate `.ToList()`) re-runs the whole pipeline each time — flag when
  the source is expensive to enumerate (a database-backed `IQueryable`, a large in-memory sequence built
  lazily).

## Dependency injection & structure

- Constructor injection over service-locator resolution (see `common-antipatterns.md`).
- A constructor's parameter list is the honest list of a class's dependencies — a class reaching for a
  static/ambient singleton it doesn't declare as a dependency is hiding a real coupling.
- Interfaces exist for a reason (multiple implementations, test doubles, a real seam) — an interface with
  exactly one implementation and no test-double usage is a `cleanup`-dimension finding
  (orphaned abstraction), not automatically wrong, but worth questioning.

## Records, structs, and value semantics

- Prefer `record`/`record struct` for data that's compared by value and doesn't mutate after construction.
- A `struct` larger than roughly 16 bytes, or one that's frequently boxed/passed by value in a hot path,
  should be reconsidered as a `class` or passed by `in`/`ref` — but this is the `performance` dimension's
  call in a proven hot path, not a blanket rule for every struct.

## Duplication & abstraction cost (shared with the cleanup dimension)

- Three or more near-identical blocks of logic should become one shared method/type. Two occurrences are a
  judgment call — duplication is often cheaper than a premature abstraction that guesses wrong about the
  axis of variation.
- An abstraction (interface, base class, generic helper) earns its cost when it removes real duplication
  or serves a real substitution need (test doubles, multiple production implementations). An abstraction
  added "in case we need it later" with a single call site is a cost without a benefit — flag it.
