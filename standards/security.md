# .NET security

Canonical security standard. Loaded on demand per `standards/index.md`'s table; read it before writing
any C# that touches configuration, SQL, endpoints, auth, logging, or cryptography. `dotnet-code-review`
validates against it (aspect `[security]`).

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

**A regex matched against untrusted input needs a bounded execution time** — `[GeneratedRegex]` (compiled,
no interpreter surprises) with a pattern reviewed for nested quantifiers, or an explicit `TimeSpan`
timeout passed to `Regex`/`Regex.Match`. An unguarded pattern with catastrophic-backtracking potential
against attacker-controlled text is a ReDoS vector — a short adversarial input can hang a request thread
for minutes.

**A file upload is validated by allowlisted extension, an enforced size cap, and a magic-byte/content
sniff** — never trust the client-supplied `Content-Type` header, which the caller controls. Persist the
file under a server-generated name; writing it under the client-supplied filename is both a
path-traversal and an overwrite risk.

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

**Rate-limit authentication endpoints** (login, password reset, token refresh) separately from general API
throttling — `Microsoft.AspNetCore.RateLimiting` or equivalent. An unlimited login endpoint is a standing
invitation to credential-stuffing regardless of how strong the password hash underneath it is.

## Transport & CORS

- HTTPS redirection + HSTS in production startup configuration — no exceptions.
- **Never `AllowAnyOrigin()`** (or equivalent wildcard CORS) in a production-configured code path. The
  same call behind an explicit development-only branch is fine.
- **`SetIsOriginAllowed(_ => true)` combined with `AllowCredentials()` is the same hole as
  `AllowAnyOrigin()`** — it just doesn't look like it at a glance. Whenever credentials (cookies, an
  `Authorization` header passthrough) are enabled, the origin predicate must apply a real allowlist check.

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

**The algorithm family alone doesn't make a password hash safe — the work factor has to clear a floor
too.** PBKDF2 needs roughly 600,000+ iterations with SHA-256 (this figure rises over time — treat it as a
floor to check the configured value against, not a constant to hardcode). Argon2id at its library's
default settings needs no manual iteration tuning and is the simpler choice when the platform offers it.

**AES-GCM (or any AEAD cipher) needs a fresh, random nonce on every encryption call under the same key.**
Reusing a nonce with GCM doesn't just weaken the encryption — two ciphertexts under the same key/nonce
pair can fully recover the authentication key. Generate the nonce from a CSPRNG per call; never derive it
from a counter that could reset or a value that could repeat.

**Compare a hash, token, or MAC with `CryptographicOperations.FixedTimeEquals`, never `==` or
`SequenceEqual`.** Both short-circuit on the first differing byte, leaking how many leading bytes matched
through response timing — a non-constant-time comparison of a real secret is a working timing side
channel, not a theoretical one.

## Deserialization

**Never use `BinaryFormatter`** on any input that could originate outside full trust — it is obsolete,
throws by default on modern TFMs unless explicitly re-enabled, and is a well-documented remote-code-
execution vector: deserializing it can construct and invoke arbitrary types from the payload. Treat
`DataContractSerializer` and a reflection-based `Newtonsoft.Json` configuration the same way for anything
crossing a process/trust boundary — prefer `System.Text.Json`, which does not invoke arbitrary
constructors from a type name embedded in the payload. `api-design.md`'s DTO section covers the same
choice from the design side; this is the security angle on it.

## What review of this standard can and can't verify

This plugin has no static-analysis security scanner behind it — no CVE/dependency-vulnerability check, no
taint tracking. Findings come from reading source via `get_symbol` and tracing usage via
`get_references`/`get_call_slice`, so a `security` review covers what's visible in the code under review
and does not replace a SAST tool or dependency scan — say so rather than implying broader coverage.

## Review calibration

Credential-shaped literals, string-built raw SQL, wildcard CORS (including `SetIsOriginAllowed(_ =>
true)` combined with `AllowCredentials()`) reachable in production, hand-rolled crypto, an
unbounded-execution-time regex against untrusted input, an upload endpoint accepting arbitrary
extension/content-type unchecked, a non-constant-time comparison of a real secret,
`BinaryFormatter`/insecure deserialization of untrusted input, and logged credentials are 🔴. Unmarked
endpoint auth, unvalidated boundary input (cite the specific field and what reaches it), missing
HTTPS/HSTS configuration, a demonstrated AES-GCM nonce reuse, a password hasher's iteration count below
the current floor, a missing rate limit on an authentication endpoint, and PII at `Information`+ are 🟡 —
state what the current effective behavior actually is (check the global auth default via
`get_references`/`get_symbol`, don't guess) before asserting severity. A bare `[Authorize]` where a
finer-grained policy plausibly belongs is 🔵 — a question, not an assumed bug. A security finding without
a cited line and a concrete reachable scenario is noise that trains people to ignore the aspect:
"this pattern is generally risky" earns 🔵 at most; "this literal/call site does X, reachable from Y" is
what earns 🔴/🟡.
