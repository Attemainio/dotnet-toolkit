# .NET resource management

Canonical resource-management standard. Loaded on demand per `standards/index.md`'s table; read it
before implementing `IDisposable`/`IAsyncDisposable`, working with a stream/handle/pooled buffer, or
deciding who owns disposing a dependency. `dotnet-code-review` validates against it as part of the
`[correctness]` aspect.

## Disposal ownership

**Dispose what you create; never dispose what you were given.** A type that receives a dependency
through its constructor (DI or otherwise) does not own its lifetime and must not `Dispose` it — the
container or the caller that created it owns that. A type that creates its own disposable field
(`new SqlConnection(...)`, `new FileStream(...)`) does own it and must dispose it.

```csharp
// DON'T — disposes a dependency it doesn't own; breaks every other consumer of the same instance
public sealed class ReportExporter(Stream output) : IDisposable
{
    public void Dispose() => output.Dispose();
}

// DO — owns and disposes only what it created
public sealed class ReportExporter(string path) : IDisposable
{
    private readonly FileStream _output = File.Create(path);
    public void Dispose() => _output.Dispose();
}
```

## Implementing IDisposable / IAsyncDisposable

- A `sealed` class with only managed disposable fields needs the simple pattern — a `Dispose()` method
  that disposes its fields, no finalizer, no `protected virtual Dispose(bool)`. The full
  finalizer-plus-`Dispose(bool)` pattern is only for a class holding an unmanaged handle directly (rare
  outside interop) or one designed to be inherited from.
- Implement `IAsyncDisposable` (`DisposeAsync`) instead of, or alongside, `IDisposable` when disposal
  itself does I/O (flushing a stream, closing a network connection) — `Dispose()` forced to block on
  that I/O is a sync-over-async violation (see `concurrency.md`).
- `using`/`await using` (statement or declaration) at the point of creation, not a manual `try`/`finally`
  calling `Dispose()` — the compiler-generated version is correct on every exit path including an
  exception, which a hand-written one is one missed `catch` away from getting wrong.

```csharp
// DON'T — a thrown exception before the manual Dispose() call leaks the connection
var connection = new SqlConnection(connectionString);
connection.Open();
DoWork(connection);
connection.Dispose();

// DO
await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync(ct);
await DoWorkAsync(connection, ct);
```

## Streams and buffers

**Don't buffer an entire stream into memory when the size is caller-controlled or unbounded** — process
it incrementally (`Stream.CopyToAsync`, chunked reads) rather than `ReadAllBytesAsync`-into-memory for
something that could be a multi-gigabyte upload.

**Rent, don't allocate, a large or per-call buffer on a hot path** — `ArrayPool<T>.Shared.Rent`/`Return`
(always in a `try`/`finally`) instead of `new byte[size]` per call; see `performance.md` for the
allocation angle. A rented array must be returned exactly once and never used after return.

## Pooled and cached object lifetime

An object drawn from a pool (`ArrayPool<T>`, an object pool, a pooled `HttpClient`/`DbContext`) is
returned/reset before it's reused — a caller that mutates a pooled object and forgets to reset it before
returning it corrupts the *next* caller's state, a bug that only shows up under concurrent reuse. Never
hold a reference to a pooled object past the point it was returned to the pool.

## Ownership across async boundaries

A disposable captured into a background `Task`/fire-and-forget continuation must have its lifetime
extended to cover that continuation, or be disposed only after the task is known to have completed —
disposing it on the calling method's return while the spawned task still holds a reference is a
use-after-dispose race, not merely a leak.

## Event subscriptions

A `+=` event subscription is a disposable-shaped resource even though the compiler doesn't require any
disposal syntax: the publisher's invocation list holds a reference to the subscriber's target, and that
reference lives until an explicit `-=`. Whichever side is expected to be short-lived and isn't
unsubscribed leaks for as long as the other side is reachable. Unsubscribe symmetrically with where the
subscription was made — in `Dispose`/`DisposeAsync` for a subscriber whose lifetime is shorter than the
publisher's — or use a weak-event pattern when the publisher is guaranteed to outlive many short-lived
subscribers. A constructor that raises an event before the instance is fully constructed hands a handler
a reference to a partially-initialized object; raise events from a method called after construction
completes, never from the constructor itself.

## Review calibration

Disposing a constructor-injected dependency, an unhandled exception path that skips a manual `Dispose()`
call (no `using`), or a pooled buffer used after `Return` is 🔴. A missing `using`/`await using` where
the exit paths happen to all reach `Dispose()` today, `Dispose()` blocking synchronously on I/O that
should be `DisposeAsync`, an unbounded in-memory buffer of caller-controlled input, or an event
subscription with no matching unsubscribe anywhere in the subscriber's disposal path is 🟡. A
finalizer/full `Dispose(bool)` pattern on a `sealed` class with only managed fields (harmless but
unnecessary), or an event raised from a constructor with no evidence a handler could actually observe the
partially-constructed state, is 🔵.
