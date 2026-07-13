# C# naming conventions

Default naming reference for `dotnet-reviewer`. Overridable per-repo via
`.claude/dotnet-toolkit/naming-conventions.md` (see `CLAUDE.md`).

## Casing

- **PascalCase**: types, namespaces, public/protected/internal members (methods, properties, events,
  public fields), constants, enum members.
- **camelCase**: parameters, local variables, private fields (with the `_` prefix below).
- **`_camelCase`** for private instance fields (`_locator`, `_log`, `_gate`) — the underscore
  distinguishes a field from a local/parameter at the call site without needing `this.` everywhere.
- **`SCREAMING_CASE` is not used** in C# — `const` fields are PascalCase like any other member
  (`MaxLimit`, not `MAX_LIMIT`).

## Prefixes & suffixes

- **`I` prefix** for interfaces (`IOrderService`, not `OrderServiceInterface`).
- **`Async` suffix** on any method returning `Task`/`Task<T>`/`ValueTask`/`ValueTask<T>` whose synchronous
  counterpart exists or could exist (`GetAsync`, not `Get` returning a `Task`). Exception: methods that are
  *always* asynchronous in this codebase with no sync counterpart and where the suffix would be pure noise
  on every single method in a small, obviously-async-only type — use judgment, but default to the suffix.
- **`T`, `TKey`, `TValue`, `TResult`, `TEntity`** for generic type parameters — single descriptive word with
  a `T` prefix, not single letters beyond plain `T` once there's more than one type parameter needing a
  distinguishing name.

## Booleans

- Boolean properties/methods read as a question: `IsPublic`, `HasValue`, `CanExecute`, `ShouldRetry`. Avoid
  bare nouns/adjectives for booleans (`Valid` instead of `IsValid`) and avoid negative-sounding names
  (`IsNotReady` instead of the affirmative `IsReady` — double negatives at call sites like `!IsNotReady`
  are a readability tax).

## Method & parameter naming

- Method names are verb phrases describing what happens (`FindSymbol`, `EnsureFreshAsync`,
  `TotalMatching`), not noun phrases.
- Avoid vague names on anything broader than a 2-3 line block: `temp`, `data`, `result`, `val`, `obj`,
  `item` (when the loop body is nontrivial), `thing`. A local scoped to one obvious line
  (`var result = await GetSymbolAsync(name);` immediately returned) is fine — the bar is whether a reader
  three lines later can still tell what the name refers to.
- Parameter names should make a call site readable without needing to check the signature:
  `Search(string? query, string? affectedClass, string? domain, ...)` is legible at the call site even
  positionally, because each parameter name states its role.

## Namespaces & folders

- Namespace mirrors folder structure from the project root (`Devlog/DevlogStore.cs` →
  `namespace DotnetToolkit.McpServer.Devlog`).
- Folder names are plural for collections of similar things (`Tools/`, `Devlog/` is a domain name not a
  collection, so singular there is correct) — judge by whether the folder holds "a bunch of X" (plural) or
  "the Y subsystem" (domain name, either casing is fine, be consistent within the repo).

## Constants & magic values

- Any numeric or string literal whose meaning isn't obvious from context becomes a named constant.
  `0`, `1`, `-1`, and empty-string/empty-collection checks are generally fine unadorned; a timeout value,
  a retry count, a specific status code, or a business-rule threshold is not.
- Named constants get a name describing *what the value means*, not just repeating the value
  (`MaxRetryAttempts = 3`, not `Three = 3`).
