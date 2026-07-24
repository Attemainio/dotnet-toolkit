---
paths:
  - "**/*.cs"
---

# .NET concurrency

Canonical concurrency standard: async correctness, synchronization primitives, and deadlock avoidance.
Loaded on demand per `csharp-standards.md`'s index; read it before writing any C# that awaits, locks,
spawns work, or touches state reachable from more than one thread. `dotnet-code-review` validates
against it (aspect `[concurrency]`). A consuming repo overrides it via
`.claude/dotnet-toolkit/concurrency.md`. The *cost* of synchronization in hot paths is `performance.md`'s
territory; correctness is decided here.

## Async correctness

- **`async` all the way up** from genuinely asynchronous (I/O-bound) work. Never block partway with
  `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` — sync-over-async deadlocks on a captured
  `SynchronizationContext` and blocks a thread-pool thread either way.

```csharp
// DON'T — deadlock-prone, and burns a pool thread while waiting
var order = _repository.GetOrderAsync(id).Result;

// DO — async end-to-end
var order = await _repository.GetOrderAsync(id);
```

- **No `Task.Run` around synchronous CPU work just to satisfy an `async` signature** (async-over-sync) —
  return the computed value; if a signature demands a task, `Task.FromResult` states the truth.
- **`async void` only for actual event handlers.** Anywhere else, exceptions thrown from it bypass the
  caller and crash the process; return `Task` so failures are awaitable.
- **Thread `CancellationToken` through**: an `async` method that calls cancellable APIs (I/O,
  `Task.Delay`, another method taking a token) accepts one and passes it down — swallowing the ability to
  cancel is a correctness loss, not a style choice.
- **`ConfigureAwait(false)` in library code** (no UI/request context to resume on). Application-level
  code that needs its context back omits it deliberately, not by default.
- **A returned `Task` is awaited or explicitly observed.** A fire-and-forget call site needs a stated
  reason and an exception route (a logging continuation), not a discarded task whose failure vanishes.

## Waiting on many tasks

- **`Task.WhenAll` over `Task.WaitAll`** — `WaitAll` blocks the calling thread (sync-over-async in
  disguise); `WhenAll` composes. Same for `WhenAny` over `WaitAny`.
- After `WhenAll`, remember it aggregates: awaiting it rethrows only the *first* exception — inspect the
  task array (or catch and check `Exception.InnerExceptions`) when individual failures matter.
- `Parallel.ForEachAsync`/`Parallel.For` for CPU-parallel loops with a bounded degree of parallelism —
  not a `foreach` spawning an unbounded `Task.Run` per item.

## Locks

- **Lock on a dedicated private object** — `private readonly object _gate = new();` (or .NET 9+'s
  `System.Threading.Lock`). Never `lock (this)`, never a `string` or any publicly reachable object:
  anyone else can lock the same reference and deadlock you from outside the class.
- **Keep the guarded region minimal and non-blocking**: no I/O, no `await`-shaped work, no callbacks into
  unknown code while holding a lock. The compiler already refuses `await` inside `lock` — don't smuggle
  the equivalent in via `.Result` (that's both anti-patterns at once).
- **One lock order.** Two locks ever taken together get a documented acquisition order, everywhere. Lock
  A→B on one path and B→A on another is the textbook deadlock.

```csharp
// DON'T — publicly reachable lock target, I/O inside the lock
lock (_ordersList)
{
    _client.PostAsync(url, Serialize(_ordersList)).Wait();
}

// DO — private gate, snapshot inside, I/O outside
List<Order> snapshot;
lock (_gate)
{
    snapshot = [.. _orders];
}
await _client.PostAsync(url, Serialize(snapshot), ct);
```

## SemaphoreSlim — the async lock

`lock` can't span an `await`; `SemaphoreSlim(1, 1)` is the async-compatible mutual exclusion. The release
lives in `finally`, always:

```csharp
private readonly SemaphoreSlim _initGate = new(1, 1);

await _initGate.WaitAsync(ct);
try
{
    if (_initialized) return;
    await LoadAsync(ct);
    _initialized = true;
}
finally
{
    _initGate.Release();
}
```

Use `Wait()` on a semaphore only from genuinely synchronous code — `WaitAsync().Result` is
sync-over-async with extra steps. A `SemaphoreSlim` with `maxCount > 1` is a throttle, not a lock — don't
protect mutable state with one.

## Interlocked & volatile

- **`Interlocked`** (`Increment`, `Add`, `Exchange`, `CompareExchange`, `Or`/`And`) for single-word
  counters and flags — cheaper than a lock and correct without one. A `count++` on a shared field is a
  lost-update bug; `Interlocked.Increment(ref count)` is not.
- **Check-then-act needs `CompareExchange` or a lock** — `if (_current < max) _current++;` is racy even
  if both pieces were individually atomic; the decision and the write must be one atomic step.
- **`volatile` is a visibility/ordering annotation, not atomicity** — it doesn't make read-modify-write
  safe. Prefer `Interlocked` or a lock; treat a bare `volatile` solving a race as a smell that the real
  invariant spans more than one field.
- Compound state (two fields that must change together) can't be `Interlocked`-ed piecewise — take a
  lock, or make the state a single immutable object swapped by reference with `CompareExchange`.

## Shared state

- **Immutability first**: state that never mutates after construction (records, `frozen`/read-only
  collections) needs no synchronization at all — the cheapest correct option.
- A `static` or singleton-scoped mutable field reachable from concurrent paths gets a lock,
  `Interlocked`, or a concurrent collection — silence is a race, not a default (see `antipatterns.md`).
- `ConcurrentDictionary` for concurrent keyed access — but its thread-safety is per-operation:
  `GetOrAdd`'s value factory can run more than once under contention (make it idempotent or wrap values
  in `Lazy<T>`), and iterating while mutating is safe but yields a moving snapshot.
- `Channel<T>` for producer/consumer pipelines — bounded (`Channel.CreateBounded`) unless there's a
  stated reason unbounded growth is safe; an unbounded channel fed faster than it drains is a memory
  leak with extra steps.

## Console & process streams

- **Multi-threaded console/stderr writes get serialized** through one lock or a single writer
  (`Channel<string>` drained by one task) — interleaved partial lines from concurrent `Console.Error`
  writes are unreadable and, for machine-parsed output, corrupt. In an MCP/stdio server, stdout belongs
  to the protocol exclusively; diagnostics go to stderr, through the logging pipeline, never `Console.Out`.
- **Drain a child process's redirected stdout and stderr concurrently, never sequentially.** Reading one
  stream to completion before touching the other deadlocks the moment the unread pipe's buffer fills —
  the child blocks writing, the parent blocks reading.

```csharp
// DON'T — deadlocks when stderr fills while stdout is being drained
var stdout = process.StandardOutput.ReadToEnd();
var stderr = process.StandardError.ReadToEnd();

// DO — both pipes drained concurrently
var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
var stderrTask = process.StandardError.ReadToEndAsync(ct);
await Task.WhenAll(stdoutTask, stderrTask);
await process.WaitForExitAsync(ct);
```

- **Concurrent draining alone is not sufficient when the child spawns its own persistent workers** —
  `dotnet build`/`dotnet restore`/MSBuild are the recurring example: they hand off to long-lived MSBuild
  worker nodes that inherit the redirected pipes and outlive the direct child process. `WaitForExitAsync`
  on the direct child returns, but a lingering node still holds the pipe's write end open, so
  `ReadToEndAsync` never sees EOF and hangs anyway — past every drain-concurrently fix above. Set
  `MSBUILDDISABLENODEREUSE=1` and `DOTNET_CLI_USE_MSBUILD_SERVER=0` on the `ProcessStartInfo` when
  shelling out to `dotnet build`/`restore`/`msbuild` with redirected streams, and bound the drain with a
  timeout so a stray pipe-holder fails loudly instead of hanging silently. This bit twice in the same
  codebase on the same line of work — fixed once, then resurfaced at a second call site that shelled out
  to the same command without the fix — because the symptom (near-zero CPU, no visible child process,
  "just taking forever") reads as slow compilation rather than a hang.

## Review calibration

A reachable deadlock shape (lock-order inversion, sync-over-async on a context, sequential pipe
draining, blocking inside a lock) or a demonstrated race (unsynchronized shared mutation, racy
check-then-act) is 🔴 with the interleaving stated concretely. Missing `ConfigureAwait(false)` in
library code, un-threaded cancellation tokens, and unbounded channels are 🟡. Anything asserting "this
could race" without naming the two call paths that overlap is 🔵 at most — trace both paths with
`get_references`/`get_call_hierarchy` before asserting, and check `search_log` for a recorded reason the
pattern is intentional.
