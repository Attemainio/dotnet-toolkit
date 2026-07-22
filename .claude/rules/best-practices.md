---
paths:
  - "**/*.cs"
---

# C# best practices

Canonical idiomatic-C# standard. Loaded on demand per `csharp-standards.md`'s index; read it before
writing C#. `dotnet-code-review` validates all of it under `[correctness]`, with the
duplication/abstraction section under `[cleanup]`. A consuming repo overrides it via
`.claude/dotnet-toolkit/best-practices.md`. Async/threading correctness lives in `concurrency.md`, not
here.

## Correctness

- **Null handling**: every reference-typed parameter/return not marked `?` is a claim it's never null —
  make the claim true at every call site. Every `?`-marked value that's dereferenced gets a null check or
  pattern match first, never a `!` to force past the compiler (see `styling.md` for the `!` bar).
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

## Dependency injection & structure

- Constructor injection over service-locator resolution (see `antipatterns.md`).
- A constructor's parameter list is the honest list of a class's dependencies — don't reach for a
  static/ambient singleton the class doesn't declare.
- An interface earns its existence through multiple implementations, a test double, or a real seam. An
  interface with exactly one implementation and no test-double usage is an orphaned abstraction —
  question it before adding another.

## Records, structs, and value semantics

- Prefer `record`/`record struct` for data compared by value that doesn't mutate after construction.
- A `struct` larger than roughly 16 bytes, or one frequently boxed/passed by value in a hot path, should
  be reconsidered as a `class` or passed by `in`/`ref` — the hot-path judgment belongs to
  `performance.md`, not blanket-applied to every struct.

## Duplication & abstraction cost (the `[cleanup]` aspect)

- Three or more near-identical blocks of logic become one shared method/type. Two occurrences are a
  judgment call — duplication is often cheaper than a premature abstraction that guesses wrong about the
  axis of variation.
- An abstraction (interface, base class, generic helper) earns its cost when it removes real duplication
  or serves a real substitution need. One added "in case we need it later" with a single call site is a
  cost without a benefit — don't write it, and flag it when found.

## Review calibration

Correctness findings that describe a reachable failure (a null dereference with a concrete path, a
swallowed exception on a path the caller depends on) are 🔴; convention-level items (interface bloat,
constructor-honesty violations) are 🟡; duplication/abstraction items are `[cleanup]` findings and
default to 🔵 unless the duplication has already diverged into inconsistent behavior.
