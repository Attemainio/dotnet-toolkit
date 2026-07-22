---
paths:
  - "**/*.cs"
---

# .NET performance

Canonical performance standard, hot-path conventions included. Loaded on demand per
`csharp-standards.md`'s index; read it before writing loop-heavy, per-request, or per-tick C#.
`dotnet-code-review` validates against it (aspect `[performance]`). A consuming repo overrides it via
`.claude/dotnet-toolkit/performance.md`. Threading/atomicity *correctness* (locks, `Interlocked`
semantics, deadlocks) lives in `concurrency.md` — this file only covers their cost.

## Hot/cold-path classification (apply in this order)

1. **Explicit marker wins.** A method whose XML doc or comment declares it hot (`<remarks>Hot
   path</remarks>`, `// HOT PATH`, or a project-specific convention) is hot — trust it over any heuristic.
2. **Stated hint.** If the invoking agent/user states which folders/methods are hot or cold, use that.
3. **Heuristic fallback** (only when neither applies):
   - **Hot**: called from a tight loop over a collection/stream; once per request/message in a
     meaningful-throughput service; once per tick/frame in a simulation, game loop, or trading/pricing
     engine; anything benchmarked or profiled in the surrounding code (`BenchmarkDotNet`, a `Stopwatch`).
   - **Cold**: startup/initialization, configuration loading, logging, CLI parsing, slow-cadence
     background jobs, admin/rarely-used endpoints.
   - When genuinely uncertain, say so rather than guessing — wrongly-hot produces noise (flagging
     idiomatic LINQ in once-a-day code); wrongly-cold misses a real allocation problem.

**Cold paths get LINQ, `foreach`, and readability-first treatment without complaint.** Only flag a cold
path for something genuinely egregious (an O(n³) loop where O(n) is trivial, a full-table scan in a
loop). Applying hot-path rules uniformly across a codebase produces false-positive noise that trains
people to ignore performance review entirely.

## Allocation sources to avoid in hot paths

- String interpolation (`$"..."`), `string.Format`, `ToString()`, and `+` concatenation inside a loop —
  each allocates. `StringBuilder` for loop-accumulated strings.
- `params T[]` call sites — each call allocates the variadic array; add an explicit-arity overload when
  the call site is in a hot loop.
- `foreach` over an interface-typed (`IEnumerable<T>`) sequence may allocate/box the enumerator and
  blocks some JIT optimizations — in a proven hot path, loop over the concrete `List<T>`/array/`Span<T>`,
  or use `for`.
- LINQ operators — each allocates an enumerator/closure per call; fine cold, a real cost inside a hot
  loop (see `antipatterns.md`).
- Closures capturing outer variables in a lambda passed into a hot-path call — each invocation allocates
  the closure. Pass captured state as an explicit argument or use a `static` lambda.
- `Task`/`async` state-machine allocation per iteration of a hot per-item loop — batch the async work
  instead of `await`-ing per element where the workload allows.

## Temporary buffers & `stackalloc`

- `stackalloc` for small, fixed-size, non-escaping buffers. The size must be a constant or provably
  capped (~1KB guidance) — **never sized by unchecked external input** (stack-overflow risk), and never
  inside a loop (the stack frame doesn't shrink until the method returns).
- `ArrayPool<T>.Shared` for larger temporaries that still don't escape: every `Rent` gets its `Return`
  in a `finally`, and the used slice is cleared first if it could hold sensitive data.

```csharp
// DO — the stackalloc-or-pool switch: stack for the common small case, pool above the cap
const int MaxStackAlloc = 512;
byte[]? rented = null;
Span<byte> buffer = byteCount <= MaxStackAlloc
    ? stackalloc byte[MaxStackAlloc]
    : (rented = ArrayPool<byte>.Shared.Rent(byteCount));
try { Encode(value, buffer[..byteCount]); }
finally { if (rented is not null) ArrayPool<byte>.Shared.Return(rented); }

// DON'T — unbounded external input on the stack, inside a loop
foreach (var msg in messages)
{
    Span<byte> buf = stackalloc byte[msg.Length];   // attacker-sized, re-alloc'd per iteration
}
```

- A plain `new T[...]` is correct when the buffer's lifetime is unclear or it escapes the method
  (returned, stored, captured) — pooling something that outlives its allocation site is a correctness
  bug, not an optimization.

## Pointers, `unsafe`, and overflow checking

- **Prefer `Span<T>`/`ReadOnlySpan<T>`/`Memory<T>`/`ref struct` over raw pointers** — they carry bounds,
  work on stack and heap alike, and cover almost every case `unsafe` used to. Reaching for `unsafe` is a
  last resort that needs a proven-hot path, a measurement showing the span-based version is insufficient,
  and a comment saying so.
- Inside `unsafe`: a `fixed` pointer is valid only within its `fixed` block — **never store it, return
  it, or capture it**; the GC may move the object the moment the block exits.
- `MemoryMarshal`/`Unsafe.*` reinterpretation tricks need the same bar as `unsafe` blocks: measured
  benefit, a comment stating the invariant that makes them safe, and containment inside one small helper
  rather than scattered through business logic.
- **`checked`/`unchecked` is a statement of intent, not a speed knob.** Code where overflow is a real
  hazard (money, counters near limits, size arithmetic on external input) wraps the arithmetic in
  `checked`. Code that deliberately relies on wrapping (hash mixing, ring-buffer indices) says so with
  an explicit `unchecked` block — don't leave the reader guessing which one was meant.

## Vectorized operations (SIMD)

- **Reach for the library first**: `System.Numerics.Tensors.TensorPrimitives` (sums, dot products,
  min/max, element-wise math over spans) and `Vector<T>` cover most numeric kernels with hand-tuned,
  hardware-adaptive code. Hand-rolled `Vector128/256/512<T>` intrinsics come only after the library
  helper is shown insufficient by a benchmark.
- Vectorize only loops that are **proven hot, data-parallel, and branch-free per element** — a loop with
  per-element branching or cross-element dependencies usually loses more to shuffling than SIMD gains.
- Every vectorized loop handles the **scalar tail** (element count not divisible by the vector width) and
  keeps a scalar fallback path (`Vector.IsHardwareAccelerated` check) so the code is correct on any
  hardware.

```csharp
// DO — library helper: adaptive SIMD, tail handled, one line
float total = TensorPrimitives.Sum(values.AsSpan());

// DON'T — hand-rolled intrinsics with no benchmark, no tail handling, no fallback
for (int i = 0; i < values.Length; i += Vector256<float>.Count)   // drops the tail silently
    acc = Avx.Add(acc, Vector256.LoadUnsafe(ref values[i]));      // and crashes pre-AVX
```

## Bitwise operations

- Pack many related booleans into a `[Flags]` enum or a bit mask instead of a `bool[]`/many fields when
  they're checked together in a hot path — one branch on a mask beats N branches on N bools, and the
  representation is 8–64× smaller.
- Shifts/masks over division/modulo only where the JIT can't already do it (it can, for constant
  powers of two) — don't hand-obfuscate arithmetic for a transform the compiler performs anyway.
- Atomicity of read-modify-write bit operations (`Interlocked.Or`/`And`) is `concurrency.md`'s
  territory — a bit trick on shared state must satisfy that file first, this one second.

## Performance attributes

- `[MethodImpl(MethodImplOptions.AggressiveInlining)]` — only on small, proven-hot methods where a
  benchmark shows the call overhead matters and the JIT didn't already inline. Never scattered
  speculatively: it bloats code size and can *hurt* by evicting instruction cache.
- `[MethodImpl(MethodImplOptions.AggressiveOptimization)]` — skips tiered compilation for the method
  (full opts immediately, but excluded from tiering's profile-guided re-JIT). Only for methods measured
  to suffer from tier-0 warm-up, e.g. a long-running inner loop entered once — with the benchmark cited
  in a comment or `<remarks>`.
- `[SkipLocalsInit]` — removes locals zero-initialization; measurable only in hot methods with large
  `stackalloc`/locals. Requires reasoning that no code path reads uninitialized memory — state it where
  the attribute is applied.
- Any of these without measurement evidence nearby is cargo cult — the attribute is the *conclusion* of
  a profiling session, never the opening move.

## Nested loops & algorithmic shape

Watch for accidental O(n²)+ — it outweighs every micro-optimization above:

- A loop containing `Contains`/`Any`/`IndexOf`/`First` over another collection is O(n·m). Build a
  `HashSet<T>`/`Dictionary<TKey,TValue>` once, then probe it.
- Nested loops over related collections often collapse into one pass plus an index/lookup built up front.
- Hoist loop-invariant work (a `.ToList()`, a regex compile, a formatted string) out of the loop body.

```csharp
// DON'T — O(orders × customers) probe
foreach (var order in orders)
    if (customers.Any(c => c.Id == order.CustomerId)) Ship(order);

// DO — O(orders + customers)
var customerIds = customers.Select(c => c.Id).ToHashSet();
foreach (var order in orders)
    if (customerIds.Contains(order.CustomerId)) Ship(order);
```

## Async & I/O

- Sync-over-async and async-over-sync are `concurrency.md` correctness items; in a proven
  high-throughput path, sync-over-async risking thread-pool starvation escalates to 🔴 here.
- Batch I/O over per-item I/O in a loop — one query fetching N rows beats N queries fetching one (the
  N+1 shape in `antipatterns.md`).
- `ValueTask`/`ValueTask<T>` only where it measurably avoids an allocation the caller cares about (a
  method that usually completes synchronously) — reflexive use adds API complexity (`ValueTask` awaits
  once) without the benefit that justifies it.

## Caching

- A pure lookup recomputed identically on every call, called often enough to matter, is a caching
  candidate — but caching adds invalidation complexity, so it's a suggestion (🔵) unless evidence (a
  trace, a stated complaint) shows the recomputation actually costs something.
- Every cache gets a bound (size, TTL, or both) — an unbounded cache keyed by unbounded input is a slow
  memory leak, not a win (see `antipatterns.md`).

## Measuring: `dotnet-trace` before optimizing

In performance-critical scenarios, don't reason from the source alone — capture a trace and let the
profile name the cost. This is the measurement evidence the calibration section below demands, and it's
cheap to produce:

1. **Capture.** Launch the scenario under the profiler —
   `dotnet-trace collect -- dotnet test --filter FullyQualifiedName~HotPathBenchTests` — or attach to a
   running process: `dotnet-trace ps` to find the pid, then `dotnet-trace collect -p <pid>`. The default
   profile samples CPU; add `--profile gc-verbose` when the question is *allocation* sources rather than
   computation. Output is a `.nettrace` file.
2. **Read.** `dotnet-trace report trace.nettrace topN -n 10` prints the hottest frames as text. For a
   deeper look, `dotnet-trace convert trace.nettrace --format speedscope` and inspect the JSON. For heap
   dominance ("what is holding the memory"), `dotnet-gcdump collect -p <pid>` complements the CPU/alloc
   view; `dotnet-counters monitor -p <pid>` gives live GC and allocation-rate counters while the
   scenario runs.
3. **Act recursively.** Fix the top-ranked cost, re-run the same capture, and repeat until the top of
   the profile is the actual intended work rather than overhead. One pass is rarely enough — removing
   the #1 cost promotes #2, which may now be worth a different fix than the first profile suggested.

Reserve this loop for code classified **hot** (per the ordering at the top of this file) or for a stated
performance complaint. Cold paths keep readability and simplicity — LINQ and `foreach` stay, and no
trace run is owed for code that runs once a day.

## Review calibration

Loop-structure, allocation-site, or data-structure changes in **hot** code without measurement evidence
in the surrounding context (a `BenchmarkDotNet` result, a trace, a stated profiling finding) get 🟡
("verify this actually matters") rather than asserted as a definite win — recommend and explain, and
give a specific counter/trace/benchmark to verify with. Never assume a guess about hot-path cost is
correct without something in the code or the request confirming the path is both hot and measured.
