# C# styling guide

Default styling reference for `dotnet-reviewer`. Targets modern C# (net8.0+) with nullable reference types
enabled. A consuming repo can override this wholesale by placing its own
`.claude/dotnet-toolkit/styling.md` — see the override note in `docs/common-antipatterns.md`'s sibling
docs and `CLAUDE.md`.

## File organization

- **File-scoped namespaces** (`namespace Foo.Bar;`) over block-scoped — reduces indentation for no
  semantic cost.
- **One type per file**, file name matches the type name (`OrderService.cs` → `class OrderService`).
  Exception: small, tightly-related private/internal helper types used only inside one file may stay
  nested rather than split out.
- **Member ordering** within a type: constants → static fields → instance fields → constructors →
  properties → public methods → protected/internal methods → private methods → nested types. Group related
  members together over strict alphabetical order.
- **`using` directives**: `System.*` first, then third-party, then the project's own namespaces, each
  group separated by a blank line if the file has more than ~5 usings. Remove unused usings (or defer to
  `dotnet format` — this is a formatting task, not a review finding worth its own line).

## Type declarations

- **`sealed` by default** on classes not explicitly designed for inheritance. Enables JIT devirtualization
  and signals intent — a class without `sealed` is an implicit invitation to subclass it.
- **`internal` by default**, `public` only when the type is a genuine part of the consumer-facing surface.
- **Records** for immutable data carriers (DTOs, value objects, event payloads) — get `Equals`/`GetHashCode`
  /`ToString` for free and communicate immutability at the declaration site. Plain classes for anything with
  real behavior or mutable state.
- **Primary constructors** are appropriate for simple DI-holder classes where the constructor body would
  otherwise be pure field assignment. Don't force a primary constructor onto a type whose constructor does
  real work (validation, computed defaults) — a conventional constructor body reads more clearly there.

## Expressions & control flow

- **Switch expressions and pattern matching** over `if`/`else if` chains once there are 3+ branches on the
  same value — more compact, and the compiler flags non-exhaustive switches on enums/sealed hierarchies.
- **Expression-bodied members** (`=>`) for single-expression properties/methods; a full `{ }` body once
  there's more than one statement or the logic needs a local variable.
- **Collection expressions** (`[1, 2, 3]`, `[]`) over `new List<T> { ... }`/`Array.Empty<T>()` where the
  target type is inferable.
- **`var`** when the right-hand side already makes the type obvious (`var list = new List<string>()`,
  `var result = GetSymbol(name)` where `GetSymbol`'s return type is discoverable at the call site); an
  explicit type when the right-hand side doesn't spell it out (`var`-from-a-`Task<T>`-returning-method
  whose `T` isn't visible in the line) or when the explicit type meaningfully aids the reader.

## Nullable reference types

- Treat `Nullable` as `enable` (matching this plugin's own `Directory.Build.props`) as the expected
  baseline for any consuming repo this doc applies to.
- A `?`-annotated parameter/return means "the caller/callee must handle null," not "I didn't think about
  it" — flag `?` added defensively without a real null case, and flag missing `?` where null is a real,
  reachable outcome (e.g. a dictionary lookup surfaced as a plain, non-nullable return).
- Avoid `!` (null-forgiving operator) as a way to silence the compiler without justifying why the value is
  actually guaranteed non-null at that point — a one-line comment above the `!` explaining the invariant is
  the bar.

## Formatting

- Allman brace style (opening brace on its own line) — matches this plugin's own source and is the
  historical .NET default; consistency within one file matters more than which style a given repo picked.
- 4-space indentation, no tabs.
- One statement per line; no `if (x) DoThing();` single-line-without-braces shortcuts — always brace an
  `if`/`for`/`while` body, even a one-liner, to avoid the classic dangling-else/next-line-added-without-
  braces bug class.

## Documentation

- Public types and public members that aren't self-explanatory from their signature get an XML `<summary>`.
  A one-line summary is enough — this repo's own convention (see `OutlineBuilder`'s `DocSummary` extraction)
  treats the `<summary>` as the canonical short description surfaced everywhere else, so it should earn
  that role by being accurate and concise, not padded.
- Don't document *what* obvious code does; document *why* when the why isn't visible from the code itself
  (a workaround, a non-obvious invariant, a constraint from an external system).
