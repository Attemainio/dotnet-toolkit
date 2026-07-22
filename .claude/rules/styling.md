---
paths:
  - "**/*.cs"
---

# C# styling

Canonical styling standard, targeting modern C# (net8.0+) with nullable reference types enabled. Loaded
on demand per `csharp-standards.md`'s index; read it before writing C#, and `dotnet-code-review`
validates against it (aspect `[correctness]`). A consuming repo overrides it via
`.claude/dotnet-toolkit/styling.md`.

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
  devirtualization and signals intent; an unsealed class is an implicit invitation to subclass it.
- **`internal` by default**, `public` only when the type is genuine consumer-facing surface.
- **Records** for immutable data carriers (DTOs, value objects, event payloads) — free
  `Equals`/`GetHashCode`/`ToString`, and immutability is stated at the declaration site. Plain classes
  for anything with real behavior or mutable state.
- **Primary constructors** only for simple DI-holder classes where the constructor body would otherwise
  be pure field assignment. A constructor that does real work (validation, computed defaults) gets a
  conventional body.

## Expressions & control flow

- **Switch expressions / pattern matching** over `if`/`else if` chains once there are 3+ branches on the
  same value — the compiler also flags non-exhaustive switches on enums and sealed hierarchies.
- **Expression-bodied members** (`=>`) for single-expression properties/methods; a full `{ }` body once
  there's more than one statement or a local variable is needed.
- **Collection expressions** (`[1, 2, 3]`, `[]`) over `new List<T> { ... }`/`Array.Empty<T>()` where the
  target type is inferable.
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

## Formatting

- Allman brace style (opening brace on its own line) — consistency within one file matters more than
  which style a repo picked.
- 4-space indentation, no tabs.
- One statement per line, and **always brace** an `if`/`for`/`while` body, even a one-liner — the
  unbraced form is the classic next-line-added-without-braces bug.

```csharp
// DON'T
if (x) DoThing();

// DO
if (x)
{
    DoThing();
}
```

## Documentation hooks

Public types and public members that aren't self-explanatory from their signature get an XML
`<summary>` — the full standard lives in `xml-documentation.md`; this file only states that styling
review treats a missing `<summary>` on public surface as in-scope.

## Review calibration

Styling findings are 🟡 at most, and formatting-only issues (`using` order, brace style in a file that's
internally consistent) are 🔵 or deferred to `dotnet format` — don't spend findings on what a formatter
fixes mechanically. Internal inconsistency within one file outranks deviation from this document.
