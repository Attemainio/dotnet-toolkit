---
paths:
  - "**/*.cs"
---

# C# contact point: the highest-cost-if-caught-late checks

<!-- Deliberately short — a handful of items where catching the mistake at write-time is much cheaper
than catching it at review time, not a copy of docs/security.md or docs/testing.md. Full standards and
rationale live there and in docs/best-practices.md; dotnet-code-review's security/testing dimensions
check exhaustively at review time regardless of whether this fired. -->

Before writing or editing a `.cs` file, hold these without needing a review pass to catch them:

- **No credential-shaped literal in source** — connection strings, API keys, tokens. Configuration comes
  from `IConfiguration`/environment/a secret store, never a string literal, even a placeholder-looking one.
- **No string-concatenated/interpolated SQL** in a raw-SQL API call — parameterize. EF Core's LINQ surface
  and `FromSqlInterpolated` already do this safely.
- **Every controller/endpoint gets an explicit `[Authorize]` or `[AllowAnonymous]`** — never an unmarked
  endpoint relying on whatever the global default happens to be.
- **New tests exercise real dependencies, not an in-memory database substitute**, for anything asserting
  constraint/transaction/query-translation behavior that provider doesn't share with the real one.

Full standards: `docs/security.md`, `docs/testing.md`, `docs/best-practices.md`.
