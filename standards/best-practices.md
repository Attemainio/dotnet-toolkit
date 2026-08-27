# C# best practices

Canonical idiomatic-C# standard. Loaded on demand per `standards/index.md`'s table; read it before
writing C#. `dotnet-code-review` validates all of it under `[correctness]`, with the
duplication/abstraction section under `[cleanup]`. Async/threading correctness lives in `concurrency.md`, not
here.

## Correctness

- **Null handling**: every reference-typed parameter/return not marked `?` is a claim it's never null —
  make the claim true at every call site. Every `?`-marked value that's dereferenced gets a null check or
  pattern match first, never a `!` to force past the compiler (see `styling.md` for the `!` bar).
  Guard a non-nullable parameter at a public entry point with `ArgumentNullException.ThrowIfNull(param)`
  (`ArgumentException.ThrowIfNullOrWhiteSpace(param)` for a string that also can't be blank) — the static
  helper is the idiomatic .NET 6+ form; a hand-rolled `if (x is null) throw new
  ArgumentNullException(nameof(x));` says the same thing with more code to review.
- **Edge cases**: handle empty collections, empty strings, zero/negative counts, and boundary indices
  (`length - 1`, `i + 1` near a loop end) — not just the common case.
- **Exception handling**: a `catch` block either handles the failure meaningfully (retries, falls back,
  returns a sentinel the caller understands) or rethrows after logging — never silently swallows.
  Catching bare `Exception` is acceptable at a genuine boundary (top-level request handler, background
  job loop) and wrong deep inside business logic where a narrower catch would do.

```csharp
// DON'T — the caller proceeds as if the operation succeeded
try { UpdateInventory(order); }
catch (Exception) { }

// DO — handle it meaningfully, or log and rethrow
try { UpdateInventory(order); }
catch (DbUpdateConcurrencyException ex)
{
    _log.LogWarning(ex, "Inventory conflict for order {OrderId}; retrying", order.Id);
    RetryUpdate(order);
}
```

- **Never throw from inside a `finally` block** — it replaces whatever exception was already propagating,
  silently hiding the original failure. (Rethrow hygiene — `throw;` vs. `throw ex;` — is
  `error-handling.md`'s territory; this is the one throw-from-`finally` case that file doesn't already
  state.)
- **Compare non-linguistic strings with `StringComparison.Ordinal`/`OrdinalIgnoreCase`** — identifiers,
  keys, file paths, enum-like tokens. The culture-aware default (`==`, `string.Compare` without an
  explicit comparison) can disagree with itself between locales (the Turkish-I "i"/"İ" case is the
  canonical example) and is slower for no benefit on strings nobody displays.
- **Resource disposal**: anything `IDisposable`/`IAsyncDisposable` gets `using`/`await using` or an
  explicit `try`/`finally` `Dispose()` — never left to the GC. A class holding a disposable field
  (`Stream`, timer, semaphore) implements `IDisposable` itself.
- **`HttpClient` lifetime**: never `new HttpClient()` per call/request — use `IHttpClientFactory` or a
  long-lived singleton. Short-lived instances exhaust sockets under load even though each disposes
  cleanly.

## LINQ vs. loops

- LINQ is the default for readability in ordinary (non-hot-path) code. The hot-path exception belongs to
  `performance.md` — don't hand-roll loops in cold code for imagined speed.
- Don't materialize a LINQ query multiple times: a `.Where(...)` reused across several
  `.Count()`/`.Any()`/`.ToList()` calls re-runs the whole pipeline each time. Insert one `.ToList()` when
  the source is expensive to enumerate (a database-backed `IQueryable`, a lazily-built sequence).
- **Use the operator that names the intent instead of the `GroupBy` workaround for it** — each one below
  says what the old shape only implied, and skips the intermediate `IGrouping` objects:
  - `CountBy(keySelector)` / `AggregateBy(...)` (net9.0+) over `GroupBy(...).Select(g => ...Count())`.
  - `Index()` (net9.0+) over a hand-maintained counter: `foreach ((int i, string v) in values.Index())`.
  - `LeftJoin` / `RightJoin` (net10.0+) over `GroupJoin` + `SelectMany` + `DefaultIfEmpty`. Both
    `Enumerable` and `Queryable` have them, and EF Core 10 translates them to real SQL outer joins.
    **The unmatched side arrives as `null`** — the result selector's parameter is nullable, so treat it
    that way (`customer?.Name`) rather than annotating the warning away.

## Collection choice

The default stays `List<T>`/`Dictionary<TKey, TValue>`. Reach past them only for the shape they can't
state:

- **`FrozenDictionary<K,V>` / `FrozenSet<T>` (net8.0+) for build-once, read-many-times lookups** —
  handler and dispatch tables, static metadata, parsers, symbol maps, application-lifetime caches. Build
  cost is deliberately high in exchange for faster reads, so this is only ever right for a `static
  readonly` (or otherwise long-lived) collection that is never rebuilt per call. Pass the comparer at
  construction, as with any dictionary.
- **`OrderedDictionary<TKey, TValue>` (net9.0+) when both key lookup and a stable position matter** —
  configuration, ordered columns, serialization models, an ordered pipeline. It replaces the
  `Dictionary` + parallel `List<TKey>` pair that has to be kept in sync by hand, and adds
  `Insert`/`GetAt`/`SetAt`. Not a substitute for `SortedDictionary` — the order is insertion order, not
  key order.
- Neither is thread-safe for writes. `FrozenDictionary` is safe to *read* concurrently because nothing
  writes it after construction, which is a property of how it's used, not a concurrency guarantee.

## Dependency injection & structure

- Constructor injection over service-locator resolution (see `antipatterns.md`).
- A constructor's parameter list is the honest list of a class's dependencies — don't reach for a
  static/ambient singleton the class doesn't declare.
- **Inject `TimeProvider` (net8.0+) instead of reading `DateTimeOffset.UtcNow` inside logic that needs
  testing** — it is the framework's own clock abstraction, so a hand-rolled `IClock`/`IDateTimeProvider`
  interface is one seam the platform already provides. Production passes `TimeProvider.System`; a test
  passes a fake and advances time, deterministically and without sleeping. It also backs
  `Task.Delay`, `Task.WaitAsync`, `CancellationTokenSource`, and timers, so delays and timeouts become
  controllable by the same seam. Ambient `DateTime.UtcNow` is fine in code nobody asserts on (a log
  line, a diagnostic).

```csharp
public sealed class Cache(TimeProvider timeProvider)
{
    public bool IsExpired(DateTimeOffset expiresAt) => timeProvider.GetUtcNow() >= expiresAt;
}
```

- An interface earns its existence through multiple implementations, a test double, or a real seam. An
  interface with exactly one implementation and no test-double usage is an orphaned abstraction —
  question it before adding another.
- **A singleton must never capture a scoped/transient dependency directly** (the "captive dependency"
  problem) — the scoped instance gets held past its intended lifetime, serving stale or shared state to
  every singleton consumer thereafter. A singleton that needs scoped work injects `IServiceScopeFactory`
  and creates a scope per operation instead of the scoped dependency itself.
- Enable `ValidateScopes`/`ValidateOnBuild` in development/test (`Host.CreateDefaultBuilder` does by
  default; set them explicitly on `ServiceProviderOptions` elsewhere) so a captive dependency or a missing
  registration fails fast at startup instead of surfacing later as an unexplained runtime symptom.
- A library that registers its own services into the caller's container uses `TryAddSingleton`/
  `TryAddScoped`/`TryAddTransient` — a bare `Add*` silently duplicates or overrides a registration the
  consuming application already made.

## Configuration

- **Bind external configuration into a strongly-typed options class, not scattered
  `IConfiguration["Key:SubKey"]` string-indexer lookups.** `services.Configure<SmtpOptions>(configuration.GetSection("Smtp"))`
  plus a constructor-injected `IOptions<SmtpOptions>` (or `IOptionsSnapshot<SmtpOptions>` when the value
  can change without a restart, e.g. behind a reloadable JSON provider) turns a key typo'd differently at
  three call sites into a single property name the compiler checks.
- **Validate configuration at startup, not at first use**:
  `AddOptions<T>().Bind(...).ValidateDataAnnotations().ValidateOnStart()` turns a missing or malformed
  setting into a startup failure with a clear message, instead of a `NullReferenceException` or a
  silently-wrong default surfacing later on whatever request happens to touch it first. Data-annotation
  attributes (`[Required]`, `[Range]`, a custom `IValidatableObject`) on the options class state the
  constraint once, next to the property it constrains.
- **`IOptionsMonitor<T>`** only where the consumer genuinely needs to react to a config change while
  running (a background service adjusting behavior live) — most consumers want the cheaper `IOptions<T>`
  (bound once) or `IOptionsSnapshot<T>` (rebound per scope), not the monitor's per-change callback
  machinery.

## Records, structs, and value semantics

- Prefer `record`/`record struct` for data compared by value that doesn't mutate after construction.
- A `struct` larger than roughly 16 bytes, or one frequently boxed/passed by value in a hot path, should
  be reconsidered as a `class` or passed by `in`/`ref` — the hot-path judgment belongs to
  `performance.md`, not blanket-applied to every struct.
- Mark a `struct`/`record struct` `readonly` the moment its fields never change after construction — a
  non-`readonly` struct passed by `in` or read through a `readonly` field gets silently defensive-copied
  on every member call the compiler can't prove is non-mutating, even a plain getter.

## Duplication & abstraction cost (the `[cleanup]` aspect)

- Three or more near-identical blocks of logic become one shared method/type. Two occurrences are a
  judgment call — duplication is often cheaper than a premature abstraction that guesses wrong about the
  axis of variation.
- An abstraction (interface, base class, generic helper) earns its cost when it removes real duplication
  or serves a real substitution need. One added "in case we need it later" with a single call site is a
  cost without a benefit — don't write it, and flag it when found.

## Review calibration

Correctness findings that describe a reachable failure (a null dereference with a concrete path, a
swallowed exception on a path the caller depends on, a throw from inside a `finally` masking the original
exception, a captive-dependency lifetime mismatch confirmed against the actual registration) are 🔴;
convention-level items (interface bloat, constructor-honesty violations, a culture-aware compare on an
internal identifier, an options class bound with no validation anywhere in its path) are 🟡;
duplication/abstraction items are `[cleanup]` findings and default to 🔵
unless the duplication has already diverged into inconsistent behavior. A suspected captive dependency
not yet confirmed against the DI registration is 🔵 — check the registration before asserting the
lifetime mismatch. (`throw ex;` losing the stack trace is `error-handling.md`'s finding, already 🔴
there — don't double-report it under this aspect too.)
