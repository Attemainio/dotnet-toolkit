---
paths:
  - "**/*.cs"
---

# C# naming

Canonical naming standard. Loaded on demand per `csharp-standards.md`'s index (path-scoping is inert in
MCP-mediated repos — see that file); read it before writing C#, and `dotnet-code-review` validates
against it (aspect `[correctness]`). A consuming repo overrides it via `.claude/dotnet-toolkit/naming.md`.

## Casing

| Element | Convention | Example |
| --- | --- | --- |
| Types, namespaces, enum members, constants | PascalCase | `OrderService`, `MaxRetryAttempts` |
| Public/protected/internal members | PascalCase | `TotalMatching`, `EnsureFreshAsync` |
| Parameters, locals | camelCase | `affectedClass`, `retryCount` |
| Private instance fields | `_camelCase` | `_locator`, `_gate` |

- The `_` prefix distinguishes a field from a local/parameter at the call site without `this.` everywhere.
- **`SCREAMING_CASE` is not C#** — `const` fields are PascalCase like any other member (`MaxLimit`, not
  `MAX_LIMIT`).

## Prefixes & suffixes

- **`I` prefix** for interfaces: `IOrderService`, never `OrderServiceInterface`.
- **`Async` suffix** on any method returning `Task`/`Task<T>`/`ValueTask`/`ValueTask<T>` whose synchronous
  counterpart exists or could exist (`GetAsync`, not `Get` returning a `Task`). Exception: a small,
  obviously-async-only type where the suffix would be pure noise on every member — use judgment, but
  default to the suffix.
- **`T`, `TKey`, `TValue`, `TResult`, `TEntity`** for generic type parameters — a single descriptive word
  with a `T` prefix once more than one parameter needs a distinguishing name; plain `T` alone is fine.

## Booleans

Boolean properties/methods read as a question: `IsPublic`, `HasValue`, `CanExecute`, `ShouldRetry`.

```csharp
// DON'T — bare adjective, and a negative name forcing double negation at call sites
public bool Valid { get; }
public bool IsNotReady { get; }
if (!order.IsNotReady) { ... }

// DO — affirmative question form
public bool IsValid { get; }
public bool IsReady { get; }
if (order.IsReady) { ... }
```

## Methods & parameters

- Method names are verb phrases describing what happens (`FindSymbol`, `EnsureFreshAsync`), not noun
  phrases.
- No vague names on anything broader than a 2–3 line block: `temp`, `data`, `result`, `val`, `obj`,
  `thing`, or `item` when the loop body is nontrivial. A local scoped to one obvious line
  (`var result = await GetSymbolAsync(name);` immediately returned) is fine — the bar is whether a reader
  three lines later still knows what the name refers to.
- Parameter names make a call site readable without checking the signature:
  `Search(string? query, string? affectedClass, string? domain)` is legible even positionally because each
  name states its role.

## Namespaces & folders

- Namespace mirrors folder structure from the project root (`Devlog/DevlogStore.cs` →
  `namespace DotnetToolkit.McpServer.Devlog`).
- Folder names are plural for collections of similar things (`Tools/`); a domain/subsystem name stays
  singular (`Devlog/`). Judge by whether the folder holds "a bunch of X" or "the Y subsystem", and be
  consistent within the repo.

## Constants & magic values

Any numeric or string literal whose meaning isn't obvious from context becomes a named constant. `0`,
`1`, `-1`, and empty-string/empty-collection checks are generally fine unadorned; a timeout, a retry
count, a status code, or a business-rule threshold is not.

```csharp
// DON'T
if (attempts > 3) throw new RetryLimitException();

// DO — the name states what the value means, not what it is
private const int MaxRetryAttempts = 3;
if (attempts > MaxRetryAttempts) throw new RetryLimitException();
```

## Review calibration

Naming findings are 🟡 (convention violation) unless the name actively misleads about behavior — a
`GetOrders` that mutates state, an `IsValid` that has side effects — which is 🔴. Don't flag a
project-established convention that deviates from this file without first checking `search_log` and the
surrounding code for evidence it's deliberate.
