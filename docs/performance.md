# .NET performance guide

Default reference for `dotnet-code-review`'s `performance` dimension. Overridable per-repo via
`.claude/dotnet-toolkit/performance.md` (see `CLAUDE.md`).

## Hot/cold-path classification (apply in this order)

1. **Explicit marker wins.** If a method's XML doc or a code comment declares it hot (`<remarks>Hot
   path</remarks>`, `// HOT PATH`, or an equivalent project-specific convention), trust it over any
   heuristic below.
2. **Main-agent hint.** If the agent invoking this review states which folders/methods are hot or cold for
   this run, use that.
3. **Heuristic fallback** (only when neither of the above applies):
   - **Hot**: called from a tight loop processing a collection/stream; called once per incoming
     request/message in a service that handles meaningful throughput; called once per tick/frame in a
     simulation, game loop, or trading/pricing engine; anything explicitly benchmarked or profiled in the
     surrounding code (a `BenchmarkDotNet` class, a `Stopwatch` around the call).
   - **Cold**: one-time startup/initialization, configuration loading, logging, CLI argument parsing,
     background jobs that run on a slow cadence (minutes/hours), admin/rarely-used endpoints.
   - When genuinely uncertain, say so in the finding rather than guessing — a wrongly-hot classification
     produces noise (flagging idiomatic LINQ in code that runs once a day); a wrongly-cold one misses a
     real allocation problem.

**Cold-path code gets LINQ, `foreach`, and readability-first treatment without complaint** — only flag a
cold path for something genuinely egregious (an O(n³) loop where O(n) is trivial, a full-table scan in a
loop). Do not apply hot-path rules uniformly across a codebase; that produces false-positive noise that
trains people to ignore this agent's findings.

## Allocation sources to flag in hot paths

- String interpolation (`$"..."`), `string.Format`, `ToString()` calls, and string concatenation via `+`
  inside a loop — each allocates. `StringBuilder` for loop-accumulated strings.
- `params T[]` call sites — each call allocates an array for the variadic arguments; an explicit overload
  avoids it when the call site is in a hot loop.
- `foreach` over an `IEnumerable<T>`-typed (interface-typed, not concrete) sequence may allocate/box the
  enumerator and blocks some JIT optimizations — prefer a concrete `List<T>`/array/`Span<T>` typed loop, or
  a `for` loop, in a proven hot path.
- LINQ operators (`Select`, `Where`, `OrderBy`, …) — each allocates an enumerator/closure per call; fine on
  cold paths, a real cost inside a hot loop (see `common-antipatterns.md`).
- Closures capturing outer variables in a lambda/delegate passed into a hot-path call — each invocation
  allocates the closure class. Prefer passing captured state as an explicit parameter or using a static
  lambda where the loop's call rate matters.
- `Task`/`async` state-machine allocation per iteration of a hot per-item loop — batch async work instead
  of `await`-ing per element where the workload allows it.

## Temporary buffers

- `stackalloc` for small, fixed-size, non-escaping buffers — the maximum size should be evident or
  documented at the call site (unbounded `stackalloc` sized by external input is a stack-overflow risk).
- `ArrayPool<T>.Shared.Rent`/`.Return` for larger temporary buffers that don't escape the method — every
  `Rent` needs a `Return` in a `finally`, and the returned/used slice should be cleared first if it could
  hold sensitive data.
- A plain `new T[...]` is fine when the buffer's lifetime is unclear or it escapes the method (returned,
  stored, captured) — pooling something that outlives its allocation site is a correctness risk, not an
  optimization.

## Async & I/O

- Sync-over-async and async-over-sync are both anti-patterns — see `common-antipatterns.md`. In a proven
  hot/high-throughput path, sync-over-async risking thread-pool starvation is a 🔴, not a 🟡.
- Batch I/O over per-item I/O in a loop — one query fetching N rows beats N queries fetching one row each
  (the N+1 shape in `common-antipatterns.md`).
- `ValueTask`/`ValueTask<T>` only where it measurably avoids an allocation the caller cares about (a method
  that usually completes synchronously) — using it reflexively everywhere adds API complexity
  (`ValueTask` can only be awaited once) without the benefit that justifies it.

## Caching

- A pure function/lookup recomputed identically on every call, called often enough to matter, is a caching
  candidate — but caching adds invalidation complexity, so this is a suggestion (🔵) unless there's evidence
  (a profiler trace, a stated performance complaint) that the recomputation is actually costing something.
- Any cache needs a bound (size, TTL, or both) — an unbounded cache keyed by unbounded input is a slow
  memory leak, not a caching win (see `common-antipatterns.md`).

## Evidence bar

Loop-structure changes, allocation-site changes, or data-structure changes in **hot** code without
measurement evidence in the surrounding context (a `BenchmarkDotNet` result, a trace, a stated profiling
finding) get flagged 🟡 ("verify this actually matters") rather than asserted as a definite win — this
agent recommends and explains, it doesn't assume its guess about hot-path cost is automatically correct
without something in the code or the request confirming the path is actually hot and actually measured.
