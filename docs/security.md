# .NET security guide

Default reference for `dotnet-code-review`'s `security` dimension. Overridable per-repo via
`.claude/dotnet-toolkit/security.md` (see `CLAUDE.md`).

## What this dimension can and can't verify

This plugin has no dedicated static-analysis security scanner (no CVE/dependency-vulnerability check, no
taint-tracking SQL-injection detector) — every finding here comes from reading source via `get_symbol`
(`include: "source"`) and tracing usage via `get_references`/`get_call_slice`, the same semantic tools
every other dimension uses. That means this dimension catches what's visible in the code under review; it
does not replace a dedicated SAST tool or a dependency-vulnerability scan, and should say so rather than
imply broader coverage than it has. Every 🔴 here still needs the same evidence bar as any other
dimension's finding: cite the actual line, not a category match.

## Secrets

- **A string literal that looks like a credential** (a connection string with an inline `Password=`, an
  API key, a token) committed to source is always 🔴 regardless of whether it's a real or placeholder
  value — the pattern itself is the risk, since a real one following the same shape is invisible in
  review. Configuration should come from `IConfiguration`, environment variables, `dotnet user-secrets`
  (development), or a secret store (production) — not a literal.
- **A `.env`, `appsettings.Development.json` with real-looking credentials, or similarly named file** being
  added to source control (visible via the diff/scope under review, not something this dimension scans the
  whole repo for) is 🔴.

## Input validation & injection

- **SQL built by string concatenation or interpolation into a raw-SQL API** (`ExecuteSqlRaw`,
  Dapper string building, ADO.NET command text) is 🔴 — parameterize instead. EF Core's own
  `FromSqlInterpolated`/LINQ query surface parameterizes automatically and is not itself a finding.
- **External input reaching domain/persistence logic without validation at the boundary** (an API
  endpoint, message handler, or file upload accepting a DTO with no validation attributes/FluentValidation
  and no manual check before use) is 🟡 — cite the specific unvalidated field and what could reach it
  unchecked, not a blanket "add validation" note.

## Authentication & authorization

- **An endpoint/controller with no explicit `[Authorize]` or `[AllowAnonymous]`** (or the minimal-API
  equivalent) is 🟡 — ambiguous auth intent is a finding even when the current global default happens to
  be safe, because the next person adding an endpoint nearby has no signal either way. State what the
  current effective behavior actually is (check the global default via `get_references`/`get_symbol` on
  the auth configuration, don't guess) before asserting severity.
- **A bare `[Authorize]` with no policy/role on an endpoint that clearly needs finer-grained access
  control** (an admin-only or resource-owner-only operation) is 🔵 — worth a question, not an assumed bug,
  since "any authenticated user" is sometimes the actual intended policy.

## Transport, CORS, and headers

- **`AllowAnyOrigin()` (or equivalent wildcard CORS)** reachable in a production-configured code path is
  🔴. The same call behind an explicit development-only branch is not a finding.
- **Missing HTTPS redirection/HSTS in production startup configuration** is 🟡 — cite the actual
  configuration method (or its absence) rather than assuming from convention alone.

## Logging

- **PII (email, name, IP address, auth token) logged at `Information` level or above** is 🟡 — 🔴 if the
  logged value is a credential/token itself rather than merely identifying information, since that value
  can be replayed. Cite the actual log call and field, not a general "avoid logging PII" note.

## Data protection

- **Hand-rolled encryption/hashing** (a custom XOR "encryption," an unsalted or fast general-purpose hash
  like unsalted MD5/SHA1 used for password storage) is 🔴 — the platform's Data Protection API or a
  purpose-built password hasher (e.g. one built on PBKDF2/BCrypt/Argon2) exists precisely to avoid this
  class of mistake.

## Evidence bar

A security finding without a cited line and a concrete reachable scenario is noise that trains people to
ignore this dimension's output — "this pattern is generally risky" is a lower-confidence 🔵 at most;
"this specific literal/call site does X, reachable from Y" is what earns 🔴/🟡.
