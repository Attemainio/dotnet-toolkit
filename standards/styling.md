# C# styling

Canonical styling standard, targeting modern C# (net8.0+) with nullable reference types enabled. Loaded
on demand per `standards/index.md`'s table; read it before writing C#, and `dotnet-code-review`
validates against it (aspect `[correctness]`).

## File organization

- **File-scoped namespaces** (`namespace Foo.Bar;`) over block-scoped — less indentation for no semantic
  cost.
- **One type per file**, file name matching the type name (`OrderService.cs` → `class OrderService`).
  Exception: small, tightly-related private/internal helper types used only inside one file may stay
  nested rather than split out.
- **Member ordering** within a type: constants → static fields → instance fields → constructors →
  properties → public methods → protected/internal methods → private methods → nested types. Group
  related members together over strict alphabetical order.
- **`using` directives**: `System.*` first, then third-party, then the project's own namespaces, each
  group separated by a blank line once a file has more than ~5 usings. Remove unused usings (or defer to
  `dotnet format`).

## Type declarations

- **`sealed` by default** on classes not explicitly designed for inheritance — enables JIT
  devirtualization and signals intent; an unsealed class is an implicit invitation to subclass it. This
  applies to `record`/`record struct` too — a record is a class under the hood and is **not** sealed by
  default; sealing one takes the same explicit `sealed record` as a plain class.
- **`internal` by default**, `public` only when the type is genuine consumer-facing surface — a
  narrower surface is fewer things another assembly can come to depend on, and fewer things a later
  refactor has to keep source-compatible; widening to `public` later is free, narrowing a shipped
  `public` type is a breaking change.
- **Records** for immutable data carriers (DTOs, value objects, event payloads) — free
  `Equals`/`GetHashCode`/`ToString`, and immutability is stated at the declaration site. Plain classes
  for anything with real behavior or mutable state.
- **Primary constructors** only for simple DI-holder classes where the constructor body would otherwise
  be pure field assignment. A constructor that does real work (validation, computed defaults) gets a
  conventional body.
- **The `field` keyword** (C# 14) for a property needing validation, normalization, or change
  notification — it removes the hand-written backing field and the getter that only returned it. A
  property with no such logic stays a plain auto-property; one whose *getter* does real work is a method.
  **Inside an accessor `field` now binds to the backing field**, so a variable of that name in the same
  type must be renamed (or escaped as `@field`) before it silently means something else. A non-nullable
  `field`-backed property whose setter does the null check still warns (CS9264) — the compiler can't see
  the setter's guarantee — so mark it `required` or initialize it, rather than suppressing the warning.

```csharp
// DO — no _name field, no explicit getter
public string Name
{
    get;
    set => field = value?.Trim() ?? throw new ArgumentNullException(nameof(value));
}
```

## Expressions & control flow

- **Switch expressions / pattern matching** over `if`/`else if` chains once there are 3+ branches on the
  same value — the compiler also flags non-exhaustive switches on enums and sealed hierarchies.
- **Expression-bodied members** (`=>`) for single-expression properties/methods; a full `{ }` body once
  there's more than one statement or a local variable is needed.
- **Collection expressions** (`[1, 2, 3]`, `[]`) over `new List<T> { ... }`/`Array.Empty<T>()` where the
  target type is inferable.
- **Null-conditional assignment** (C# 14) — `customer?.CurrentOrder = order;`, `cache?[key] = value;` —
  over an `if (x is not null)` whose only body is that assignment. **The right-hand side is not evaluated
  when the target is null**, so nothing with a side effect belongs there (`?.Count = Next()` skips
  `Next()` entirely). Compound forms (`+=`, `??=`) work the same way; `++`/`--` do **not** compile on a
  null-conditional target (CS1059).
- **`var`** when the right-hand side already makes the type obvious; an explicit type when it doesn't:

```csharp
// DO — the type is visible either way
var list = new List<string>();
Dictionary<string, SymbolEntry> index = BuildIndex();

// DON'T — the reader can't see what 'result' is without leaving the line
var result = ProcessAsync();
```

## Nullable reference types

- Treat `Nullable` as `enable` as the baseline.
- A `?` annotation means "the caller/callee must handle null" — never add `?` defensively without a real
  null case, and never omit it where null is a real, reachable outcome (e.g. a dictionary lookup surfaced
  as a non-nullable return).
- `!` (null-forgiving) needs a one-line comment stating the invariant that guarantees non-null at that
  point — never use it just to silence the compiler.

```csharp
// DON'T — forcing past the compiler with no stated reason
var name = FindCustomer(id)!.Name;

// DO — either handle the null, or state the invariant the compiler can't see
var customer = FindCustomer(id)
    ?? throw new InvalidOperationException($"Customer {id} not found");
var name = customer.Name;
```

- **State a nullability contract the compiler can't infer with an attribute, not a comment**:
  `[NotNullWhen(true)]` on an `out` parameter of a `Try*` method, `[MemberNotNull(nameof(_field))]` on a
  helper that guarantees a field is non-null once it returns, `[NotNullIfNotNull(nameof(other))]` for a
  return that mirrors an argument's nullability. Without the attribute the compiler can't see the
  guarantee, so every caller re-earns a warning the method already resolved.

```csharp
// DO — lets a caller's `if (TryFind(id, out var order))` narrow `order` to non-null inside the branch
public bool TryFind(OrderId id, [NotNullWhen(true)] out Order? order)
```

## Formatting

- Allman brace style (opening brace on its own line) — consistency within one file matters more than
  which style a repo picked.
- 4-space indentation, no tabs.
- One statement per line.
- **No braces around a one-line `if` or `else` body.** When a branch is a single statement that fits on
  one line, write it unbraced on its own indented line; brace a branch only when it holds more than one
  statement or the statement spans several lines. Each branch of an `if`/`else` is judged on its own.
  A nested `if` is not a one-liner — brace the outer body, so an `else` can never bind to the wrong `if`.

```csharp
// DON'T
if (x)
{
    DoThing();
}
else
{
    DoOther();
}

if (x) DoThing();          // the statement goes on its own line

// DO
if (x)
    DoThing();
else
    DoOther();

if (frame.HasNaN())
    throw new InvalidDataException("NaN in frame.");
else
{
    frames.Add(frame);
    count++;
}
```

## Suppressions

- **`#pragma warning disable` pairs with `restore`**, scoped as tightly as possible (a handful of lines,
  never a whole file) and targets the specific warning code, never a bare disable-all. An unpaired
  `disable` silences that warning for every line for the rest of the file, including code added later
  that the original author never saw.
- **`[SuppressMessage]` carries a `Justification`** — the attribute without one records *that* something
  was suppressed but not *why*, which is exactly the information a reviewer needs to judge whether the
  suppression still applies.

## Documentation hooks

Public types and public members that aren't self-explanatory from their signature get an XML
`<summary>` — the full standard lives in `xml-documentation.md`; this file only states that styling
review treats a missing `<summary>` on public surface as in-scope.

## Review calibration

Styling findings are 🟡 at most, and formatting-only issues (`using` order, brace style in a file that's
internally consistent) are 🔵 or deferred to `dotnet format` — don't spend findings on what a formatter
fixes mechanically. Internal inconsistency within one file outranks deviation from this document.
