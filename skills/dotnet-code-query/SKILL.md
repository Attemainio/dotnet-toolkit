---
name: dotnet-code-query
description: Use when searching, inspecting, or analyzing C# code in a .NET repo - finding a class/method/symbol, callers or references, interface implementations, type signatures/APIs, or build errors. Prefer these MCP tools over Read/Grep/dotnet build for .cs files; they return compact, token-minimal results.
---

# Token-efficient C# code queries

This repo has the dotnet-toolkit MCP server. For C# code questions, call its tools
instead of reading files or grepping — they answer from a Roslyn index and return a
fraction of the tokens.

## Decision table

| You want | Call | Do NOT |
|---|---|---|
| Locate a class/method/property by name | `mcp__plugin_dotnet-toolkit_dotnet__find_symbol` | Grep/Glob over .cs files |
| A type's API: signature, members, docs | `mcp__plugin_dotnet-toolkit_dotnet__get_symbol` (semantic) or `mcp__plugin_dotnet-toolkit_dotnet__outline` (whole file) | Read the .cs file |
| Callers / usages of a symbol | `mcp__plugin_dotnet-toolkit_dotnet__find_references` | Grep for the name (misses/false hits) |
| Implementations of an interface, derived classes, overrides | `mcp__plugin_dotnet-toolkit_dotnet__find_implementations` | Grep for `: IFoo` |
| Compile errors/warnings | `mcp__plugin_dotnet-toolkit_dotnet__diagnostics` | Run `dotnet build` and read its output |

Only Read a .cs file when you are about to edit specific lines, or when the outline
is genuinely insufficient (e.g. you need method bodies).

## Symbol addressing

Pass a fully-qualified name or a unique suffix: `OrderService`, `Contoso.OrderService`,
`OrderService.PlaceOrder`. Disambiguate overloads with a parameter list:
`OrderService.PlaceOrder(OrderRequest)`. If ambiguous, the tool returns the candidate
list — pick one and retry. If a symbol isn't found, run `find_symbol` first.

## Reading compact output

Tables: `label(shown/total) col1|col2|...` header, one pipe-separated row per line,
`…+N more (raise limit)` when truncated. Paths are repo-root-relative.

Outlines: one line per declaration, kind code + signature, `// ` prefixes the XML doc
summary. Kind codes: `C` class, `I` interface, `S` struct, `R` record, `E` enum,
`D` delegate, `M` method, `K` constructor, `P` property, `F` field, `V` event.

```
C OrderService : IOrderService  // Coordinates order lifecycle.
  M PlaceOrder(OrderRequest req) -> Task<OrderResult>  // Validates and persists an order.
```

All read tools accept `format: "json"` if you need structured output, and `limit`
to raise the row cap.

## Workspace readiness

`find_symbol`, `outline`, and the structure tools work immediately (syntax index).
`find_references`, `find_implementations`, `get_symbol`, and `diagnostics` need the
MSBuild workspace, which loads in the background after server start. If a tool replies
"workspace still loading", wait a moment and retry, or check
`mcp__plugin_dotnet-toolkit_dotnet__workspace_status`. After a big git operation
(checkout/pull/rebase), call `mcp__plugin_dotnet-toolkit_dotnet__reload_workspace`.
