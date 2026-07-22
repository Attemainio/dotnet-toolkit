---
paths:
  - "**/*.cs"
---

# .NET security

Canonical security standard. Loaded on demand per `csharp-standards.md`'s index; read it before writing
any C# that touches configuration, SQL, endpoints, auth, logging, or cryptography. `dotnet-code-review`
validates against it (aspect `[security]`). A consuming repo overrides it via
`.claude/dotnet-toolkit/security.md`.

## Secrets

**Never put a credential-shaped literal in source** — a connection string with an inline `Password=`, an
API key, a token — even as a placeholder. The pattern itself is the risk: a real value following the same
shape is invisible in review. Configuration comes from `IConfiguration`, environment variables,
`dotnet user-secrets` (development), or a secret store (production).

```csharp
// DON'T — placeholder-looking or not, this shape never ships
private const string ConnectionString =
    "Server=db;Database=orders;User Id=sa;Password=ChangeMe123!";

// DO
var connectionString = configuration.GetConnectionString("Orders")
    ?? throw new InvalidOperationException("Orders connection string not configured");
```

Never add a `.env`, an `appsettings.*.json` with real-looking credentials, or a similarly named file to
source control.

## Input validation & injection

**Never build SQL by concatenation or interpolation into a raw-SQL API** (`ExecuteSqlRaw`, Dapper string
building, ADO.NET command text) — parameterize. EF Core's LINQ surface and `FromSqlInterpolated`
parameterize automatically and are safe as-is.

```csharp
// DON'T — injection via the raw-SQL API
db.Database.ExecuteSqlRaw($"DELETE FROM Orders WHERE Region = '{region}'");

// DO — parameterized (or FromSqlInterpolated, which parameterizes the interpolation)
db.Database.ExecuteSqlRaw("DELETE FROM Orders WHERE Region = {0}", region);
```

**Validate external input at the boundary** — an API endpoint, message handler, or file upload accepting
a DTO gets validation attributes/FluentValidation or an explicit check before the value reaches
domain/persistence logic. Don't re-validate the same value at every internal layer; the boundary owns it.

## Authentication & authorization

**Every controller/endpoint states its auth explicitly** — `[Authorize]` (with a policy/role where
finer-grained access applies) or `[AllowAnonymous]`, or the minimal-API equivalent. An unmarked endpoint
relying on the global default is ambiguous even when the default happens to be safe: the next endpoint
added nearby inherits the ambiguity.

```csharp
// DON'T — auth intent unstated, inherited from whatever the global default is today
public sealed class RefundsController : ControllerBase

// DO — intent stated at the surface
[Authorize(Policy = "FinanceOperator")]
public sealed class RefundsController : ControllerBase
```

## Transport & CORS

- HTTPS redirection + HSTS in production startup configuration — no exceptions.
- **Never `AllowAnyOrigin()`** (or equivalent wildcard CORS) in a production-configured code path. The
  same call behind an explicit development-only branch is fine.

## Logging & PII

- Never log PII (email, name, IP address) at `Information` level or above; never log a credential/token
  at any level — a logged token is a replayable one.
- Structured-logging placeholders don't sanitize anything — choosing *what* goes into the log line is the
  control.

## Data protection

**Never hand-roll encryption or password hashing.** Use the platform's Data Protection API for
encryption-at-rest needs and a purpose-built password hasher (PBKDF2/BCrypt/Argon2-based, e.g. ASP.NET
Core Identity's) for passwords. A custom XOR "encryption" or an unsalted general-purpose hash (MD5/SHA1)
for passwords is always wrong.

## What review of this standard can and can't verify

This plugin has no static-analysis security scanner behind it — no CVE/dependency-vulnerability check, no
taint tracking. Findings come from reading source via `get_symbol` and tracing usage via
`get_references`/`get_call_slice`, so a `security` review covers what's visible in the code under review
and does not replace a SAST tool or dependency scan — say so rather than implying broader coverage.

## Review calibration

Credential-shaped literals, string-built raw SQL, wildcard CORS reachable in production, hand-rolled
crypto, and logged credentials are 🔴. Unmarked endpoint auth, unvalidated boundary input (cite the
specific field and what reaches it), missing HTTPS/HSTS configuration, and PII at `Information`+ are 🟡 —
state what the current effective behavior actually is (check the global auth default via
`get_references`/`get_symbol`, don't guess) before asserting severity. A bare `[Authorize]` where a
finer-grained policy plausibly belongs is 🔵 — a question, not an assumed bug. A security finding without
a cited line and a concrete reachable scenario is noise that trains people to ignore the aspect:
"this pattern is generally risky" earns 🔵 at most; "this literal/call site does X, reachable from Y" is
what earns 🔴/🟡.
