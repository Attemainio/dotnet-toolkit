# .NET API design

Canonical API-design standard. Loaded on demand per `standards/index.md`'s table; read it before
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

## DTOs and serialization contracts

A DTO — a type whose entire job is to cross a boundary as JSON/XML/protobuf — is a **value**, not an
object with behavior. Declare it as a `record` with `init` accessors:

```csharp
// DON'T — mutable after construction; nothing stops a handler halfway down the
// pipeline rewriting a field the caller already validated
public class OrderDto
{
    public string Ticker { get; set; }
    public decimal Amount { get; set; }
}

// DO — fixed once constructed, and `with` covers the case `set` was really serving
public record OrderDto
{
    public required string Ticker { get; init; }
    public decimal Amount { get; init; }
}
```

Why `record` over `class`: value equality makes a DTO comparable and cacheable without a hand-written
`Equals`; `init` makes "constructed valid, stays valid" enforceable rather than conventional; and `with`
gives non-destructive update, which is what `set` was usually being used for.

**The positional form (`public record OrderDto(string Ticker, decimal Amount);`) is preferred when the
DTO has no property defaults** and is bound from a JSON body — it is terser and documents cleanly with
`<param>`. Use the `init`-property form when any case below applies, since property initializers
(`public int Max { get; init; } = 250;`) survive it and positional parameters express defaults less
readably.

### Three cases where a mutable `class` is correct, and a review must not flag it

This rule is not mechanical. Verify each before proposing a conversion:

1. **Bound from query, form, or route.** ASP.NET Core's MVC model binder (`[FromQuery]`, `[FromForm]`,
   `[FromRoute]`) requires a parameterless constructor and settable properties. A positional record
   binds nothing and **fails silently** — every property arrives at its default, and no exception is
   raised. `[FromBody]` JSON binding is unaffected (System.Text.Json supports parameterized
   constructors), so check the binding source rather than assuming from the fact that it is a request
   type.
2. **Populated incrementally.** A DTO a producer builds across several steps (`dto.Progress = x` in a
   loop, a status object mutated as work advances) cannot take `init` without restructuring that
   producer. The restructuring may be the right change, but it is a separate change with its own risk —
   not a type-declaration swap.
3. **Deserialized by a serializer needing a settable surface.** Some `Newtonsoft.Json` configurations,
   `XmlSerializer`, and certain OR/M projections require a parameterless constructor plus settable
   properties.

Weigh the schema ripple too: a positional record's parameters usually surface as **required** in a
generated OpenAPI schema where `get; set;` surfaced as optional, changing generated clients. That is
often a correctness improvement, but it is client-visible and belongs in the finding rather than being
discovered at the consumer's build.

### Polymorphic and cross-process JSON

A DTO hierarchy serialized as JSON (`OrderDto` with `CashOrderDto`/`MarginOrderDto` subtypes) needs an
explicit discriminator, never the CLR type name: `[JsonPolymorphic(TypeDiscriminatorPropertyName =
"type")]` on the base plus `[JsonDerivedType(typeof(CashOrderDto), "cash")]` per subtype. A payload
embedding `"$type": "MyApp.Orders.CashOrderDto, MyApp"` breaks the moment that class is renamed or moved
namespace — a string literal in `[JsonDerivedType]` doesn't move with it.

For a hot or AOT-published serialization path, register the DTO graph in a source-generated
`JsonSerializerContext` (`[JsonSerializable(typeof(OrderDto))]`) rather than relying on
`JsonSerializer`'s reflection-based fallback — reflection-based (de)serialization is slower, and it is
the one path trimming/AOT can silently break at runtime instead of flagging at compile time.

Map between a DTO and its domain/entity counterpart with an explicit method (a `ToDto()`/`ToEntity()`
extension, or a small static mapper) — never a reflection-based mapper (AutoMapper, Mapster). A missed or
misnamed property is a compile error in an explicit mapper and a silent runtime null/default in a
reflection-based one. Never use `BinaryFormatter` to (de)serialize a DTO — see `security.md`'s
deserialization entry; it applies to every DTO the same way it applies to any other type.

### Review calibration

A **new** DTO declared as a mutable `class` with none of the three cases above applying is 🟡. An
**existing** one is 🔵 — migration backlog — and the finding must name which of the three cases was
checked and ruled out, or state plainly that the producer was not traced. Mixing both styles inside one
DTO file is 🟡 regardless of age, because a reader cannot tell which convention is current. A DTO
carrying behavior beyond computed projections of its own properties (a method doing I/O, mutating other
state, or enforcing a business rule) is 🟡 — that logic belongs in the service, and its presence usually
means the type is not really a DTO. A polymorphic DTO hierarchy serialized with the bare type name as its
discriminator, or a reflection-based mapper (AutoMapper/Mapster) newly introduced, is 🟡; `BinaryFormatter`
anywhere in a DTO's (de)serialization path is 🔴 regardless of age (see `security.md`).

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

**When the break can be staged rather than shipped immediately**, mark the old member
`[Obsolete("message pointing at the replacement", error: false)]` for at least one release before
removing it — a compiler warning every consumer sees ahead of time beats a surprise removal.
`error: true`, and the removal itself, belong to the release after that, not the same one.

## Review calibration

A public method returning a mutable concrete collection, leaking an internal/EF-Core type, or an
`async` public method with no `CancellationToken` on an I/O path is 🟡. A `!`-suppressed nullable warning
on a public boundary with no visible justification, or a `CancellationToken` accepted but not forwarded
to an awaited call, is 🔴 — the second one is a silent correctness bug, not a style note. Three or more
optional parameters, or two-plus `bool` parameters, is 🔵 unless `get_references` shows call sites
already confused by it (positional `true, false, true`), which raises it to 🟡.
