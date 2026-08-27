# .NET performance

Canonical performance standard, hot-path conventions included. Loaded on demand per
`standards/index.md`'s table; read it before writing loop-heavy, per-request, or per-tick C#.
`dotnet-code-review` validates against it (aspect `[performance]`). Threading/atomicity *correctness* (locks, `Interlocked`
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
- `params T[]` call sites — each call allocates the variadic array. On C# 13+ declare the parameter
  `params ReadOnlySpan<T>` instead (see below); an explicit-arity overload is the older workaround for
  the same problem.
- `foreach` over an interface-typed (`IEnumerable<T>`) sequence may allocate/box the enumerator and
  blocks some JIT optimizations — in a proven hot path, loop over the concrete `List<T>`/array/`Span<T>`,
  or use `for`.
- LINQ operators — each allocates an enumerator/closure per call; fine cold, a real cost inside a hot
  loop (see `antipatterns.md`).
- Closures capturing outer variables in a lambda passed into a hot-path call — each invocation allocates
  the closure. Pass captured state as an explicit argument or use a `static` lambda.
- `Task`/`async` state-machine allocation per iteration of a hot per-item loop — batch the async work
  instead of `await`-ing per element where the workload allows.
- A substring allocated only to look it up — `dict[input.Substring(0, 5)]`. On net9.0+ take an alternate
  lookup once and query it with a span instead. **The slice must come from `AsSpan`** — a range indexer on
  a `string` (`input[..5]`) still allocates the substring the lookup was meant to avoid, which is what
  CA1831 flags. Worth it in parsers, protocol handlers, and tokenizers. It requires the dictionary's
  comparer to implement `IAlternateEqualityComparer<,>`: `StringComparer.Ordinal`/`OrdinalIgnoreCase` do,
  a hand-written comparer usually doesn't.

```csharp
var lookup = dict.GetAlternateLookup<ReadOnlySpan<char>>();   // once, not per call
if (lookup.TryGetValue(input.AsSpan(0, 5), out var value)) { ... }
```

## Spans, and the methods that are already vectorized

- **Slice, don't copy.** `text[..separator]` on a `ReadOnlySpan<char>` is a view onto the same memory;
  `text.Substring(0, separator)` on a `string` is an allocation. `AsSpan()` is the bridge from
  `string`/`T[]`, and the only correct one — a range indexer applied to a `string` still allocates
  (CA1831).
- **A `Span<T>` does not vectorize your loop; the BCL's span methods vectorize theirs.** Rewriting a
  hand-written `for` loop to walk a span buys bounds-check-free access, not SIMD. The real win is handing
  the work to `MemoryExtensions` — `IndexOf`, `IndexOfAny`, `IndexOfAnyExcept`, `SequenceEqual`,
  `Contains`, `CopyTo`, `Reverse` — whose implementations are vectorized in ways the JIT will not derive
  from your loop.
- **`SearchValues<T>` (net8.0+) for a value set searched more than once** — it precomputes the search
  strategy, beating both a `c is ',' or ';' or …` chain and a fresh `IndexOfAny(",;|")` per call.
  **Build it once into a `static readonly` field**; constructing one per call is slower than what it
  replaces. net9.0+ adds string sets, which makes it a real tokenizer/keyword-scanner primitive:

```csharp
private static readonly SearchValues<char> Separators = SearchValues.Create(",;|\r\n");
private static readonly SearchValues<string> Keywords =
    SearchValues.Create(["class", "struct", "record"], StringComparison.Ordinal);

int at = text.IndexOfAny(Separators);
```

- **`params ReadOnlySpan<T>` (C# 13) over `params T[]`** on a variadic API called in a hot path — the
  compiler passes a span over temporary storage instead of allocating an array per call site. Logging,
  formatting, math helpers, and parser entry points are the usual candidates.
- C# 14's first-class span conversions bind more calls to a span overload than before. That is normally
  the faster overload, but **an SDK bump can change which method a call resolves to with no source
  change** — worth remembering when a benchmark moves on its own.

## Reaching a collection's storage

`CollectionsMarshal` hands out the backing storage `List<T>` and `Dictionary<TKey, TValue>` otherwise
keep private. It is GC-safe; it is not safe against **structural modification**.

- **`CollectionsMarshal.AsSpan(list)`** to walk a `List<T>` without the indexer's per-element bounds
  check or the enumerator's per-`MoveNext` version check, and to pass a list to a span-based API with no
  `ToArray()` copy.
- **`CollectionsMarshal.GetValueRefOrAddDefault(dict, key, out bool existed)`** collapses the
  lookup–copy–modify–store round trip into one hash probe. Worth reaching for when the value is a large
  struct or a counter updated in a tight loop, not for an occasional dictionary write.
- **The span or `ref` is invalidated by any `Add`/`Remove`/`Clear`/capacity change**, and nothing
  enforces it: the reference keeps pointing at the abandoned array, so writes through it are silently
  lost. Take it, use it, and drop it inside one loop — never store it in a field, never hand it to code
  that might mutate the collection.

```csharp
// DO — one probe, mutated in place through the ref
ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(_counts, symbol, out _);
count++;

// DON'T — the span outlives the Add that reallocated the backing array
Span<float> prices = CollectionsMarshal.AsSpan(_prices);
_prices.Add(newPrice);   // may reallocate
prices[0] = 1;           // written to the old array; silently lost
```

## Temporary buffers & `stackalloc`

- `stackalloc` for small, fixed-size, non-escaping buffers. The size must be a constant or provably
  capped (~1KB guidance) — **never sized by unchecked external input** (stack-overflow risk), and never
  inside a loop (the stack frame doesn't shrink until the method returns).
- `ArrayPool<T>.Shared` for larger temporaries that still don't escape: every `Rent` gets its `Return`
  in a `finally`, and the used slice is cleared first if it could hold sensitive data.
- **`Rent` may return an array longer than requested** — the pool hands back the smallest bucket that
  fits, not an exact-length array. Track the requested/used length separately (a local, a `Span` sliced
  to it) rather than trusting `buffer.Length`, which can read past the data actually written.

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

## `in`, `ref readonly`, and `ref struct`

- **`in`/`ref readonly` pays only above roughly three machine words (~24 bytes).** Below that the added
  indirection costs more than the copy it avoids — passing a small struct by `in` is a pessimization
  that looks like an optimization.
- **`in` on a non-`readonly` struct defensive-copies on every member call**, plain getters included,
  which is the exact opposite of the intent. Make the struct `readonly` first (`best-practices.md`).
- **`allows ref struct` (C# 13)** lets a generic method or interface accept `Span<T>` and other
  `ref struct`s, so an allocation-free abstraction no longer has to choose between being generic and
  being span-based. Boxing stays impossible, which is what preserves the guarantee.
- **`[InlineArray(N)]` (C# 12)** for a small fixed-size buffer embedded in a struct — the safe
  replacement for an `unsafe fixed` buffer, convertible to a `Span<T>` with no allocation. One field
  declares the whole thing:

```csharp
[InlineArray(16)]
public struct Buffer16<T> { private T _element0; }   // the single field is the declaration
```

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
  min/max, distances, element-wise math over spans) and `Vector<T>` cover most numeric kernels with
  hand-tuned, hardware-adaptive code. `TensorPrimitives` ships in the **`System.Numerics.Tensors` NuGet
  package**, not the shared framework — a missing `PackageReference` is why the namespace won't resolve;
  `Vector<T>` and `Vector128/256/512<T>` are built in. Hand-rolled `Vector128/256/512<T>` intrinsics come only after the library
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
- **A lookup table built once and read for the process's lifetime is a `FrozenDictionary`/`FrozenSet`**
  (net8.0+) — dispatch tables, static metadata, symbol maps. Construction is deliberately expensive in
  exchange for faster reads, so freeze at `static readonly` initialization and never inside a request:
  a `ToFrozenDictionary()` per call is strictly slower than the `Dictionary` it replaced. Shape and
  non-perf guidance in `best-practices.md`.
- **Never `string.Intern` an unbounded or user-supplied string** — the intern pool is never garbage
  collected for the process's lifetime, so interning attacker- or user-controlled values is the same
  unbounded-growth shape as an uncapped cache, just harder to spot because there's no visible
  `Dictionary` to flag.

## Garbage collector

**Never call `GC.Collect()` from production code.** The generational heuristics already run more
precisely than a manually-triggered full collection, and forcing one is almost always a slower, more
disruptive substitute for finding the actual allocation source (see "Allocation sources" above). The one
narrow exception — deliberately shrinking a very large, freshly-idle process — is rare enough to need its
own stated justification in the code, not a default reach when memory usage looks high.

## Measuring: `dotnet-trace` before optimizing

**Benchmark the straightforward version before rewriting it.** Every release the JIT absorbs more of
what hand-optimization used to buy — escape analysis that stack-allocates short-lived objects,
devirtualization, bounds-check elimination, dynamic PGO — so a plain `foreach` over a span on a current
target often compiles to what the hand-tuned version was reaching for. A TFM bump is itself a
performance change: re-measure across it rather than carrying old workarounds forward.

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
correct without something in the code or the request confirming the path is both hot and measured. A
`GC.Collect()` call with no stated justification, or `string.Intern` on unbounded/user-supplied input, is
🟡 regardless of hot/cold classification — these are unbounded-growth-shaped correctness concerns, not
hot-path speculation that needs a trace to back it up. A `CollectionsMarshal` span or `ref` held across a
structural modification is **🔴 and needs no benchmark** — it is a silent-data-loss bug that happens to
live in performance-motivated code. Conversely, a `SearchValues`/`FrozenDictionary` built per call rather
than once is 🟡 on sight: it is slower than the code it replaced, so no measurement is owed to say so.
