# .NET API design

Canonical API-design standard. Loaded on demand per `.claude/rules/index.md`'s standards table; read it before
adding or changing a `public`/`internal` member's signature — nullability, return type, async shape, or
parameters. `dotnet-code-review` validates against it as part of the `[correctness]` aspect.

## Surface area

**Default to `internal`; make a member `public` only when something outside the assembly needs it.** A
narrower surface is fewer things a caller can depend on and fewer things a later change can break.
Widening is cheap later; narrowing a shipped public member is a breaking change.

**Don't leak an internal/infrastructure type through a public signature** — an EF Core entity, a
DTO meant for one serializer, or a type from a dependency the caller shouldn't need to reference.

```csharp
// DON'T — leaks the EF Core entity and this project's internal Money type
public Task<OrderEntity> GetOrderAsync(Guid id);

// DO — a public-surface type owned by this API
public Task<OrderDto> GetOrderAsync(OrderId id, CancellationToken ct);
```

## Nullability

**Nullable reference types are the contract, not a compiler suggestion.** A parameter/return type
annotated non-nullable means the caller may assume a non-null value without a defensive check; annotate
`?` the moment `null` is a real, expected outcome (not found, optional configuration) rather than
suppressing a warning with `!`. A `!` at a public boundary should be rare enough that each one is a
deliberate, reviewable choice, not routine.

## Collections and return shapes

**Return the least specific type the caller actually needs**: `IReadOnlyList<T>`/`IEnumerable<T>` over
`List<T>`, `IReadOnlyDictionary<K,V>` over `Dictionary<K,V>`, unless the caller is documented to mutate
the result. Returning a mutable concrete collection invites a caller to mutate internal state through
the reference.

**Never return `null` for an empty collection** — return `Array.Empty<T>()`/an empty collection so every
caller can `foreach`/`.Count` without a null check the type system already promised isn't needed.

## Async shape

- Public async methods end in `Async` and return `Task`/`Task<T>`/`ValueTask`/`ValueTask<T>` —
  never `async void` outside an actual event handler (see `antipatterns.md`).
- **Accept a `CancellationToken` on any public async method that does I/O or can run long**, and
  propagate it to every awaited call inside — a token accepted but not forwarded is a broken contract
  that looks correct at the call site.
- Use `ValueTask`/`ValueTask<T>` only for a method proven to complete synchronously on a hot path often
  enough to matter (see `performance.md`); default to `Task`/`Task<T>` otherwise — a `ValueTask` has
  narrower rules (don't await it twice, don't store it) that a `Task` doesn't.

## Parameters

**Avoid more than one `bool` parameter, and avoid a bare `bool` where the call site reads as
`DoThing(true, false)`** — an enum or an options object states what the flags mean at the call site.

```csharp
// DON'T — unreadable at the call site, easy to swap
public void Save(Order order, bool validate, bool notify);

// DO
public void Save(Order order, SaveOptions options);
[Flags] public enum SaveOptions { None = 0, Validate = 1, Notify = 2 }
```

**Validate at the boundary, trust internally** — a public API validates its inputs once at the entry
point (see `security.md` for the injection/boundary-validation angle); a private/internal method one
call deep does not re-validate what its only caller already guaranteed.

## Breaking changes

Before changing a `public`/`protected` member's signature, return type, or observable behavior, check
`get_semantic_diff` against the stated baseline — it reports exactly which symbols moved and which
changes are breaking, rather than relying on a guess from the diff text. A breaking change to a shipped
public member needs the `intent` passed to `validate_patch` to say so explicitly, so `search_log` later
shows it was deliberate.

## Review calibration

A public method returning a mutable concrete collection, leaking an internal/EF-Core type, or an
`async` public method with no `CancellationToken` on an I/O path is 🟡. A `!`-suppressed nullable warning
on a public boundary with no visible justification, or a `CancellationToken` accepted but not forwarded
to an awaited call, is 🔴 — the second one is a silent correctness bug, not a style note. Three or more
optional parameters, or two-plus `bool` parameters, is 🔵 unless `get_references` shows call sites
already confused by it (positional `true, false, true`), which raises it to 🟡.
