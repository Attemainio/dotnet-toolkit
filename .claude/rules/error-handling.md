---
paths:
  - "**/*.cs"
---

# .NET error handling

Canonical error-handling standard. Loaded on demand per `csharp-standards.md`'s index; read it before
writing a `try`/`catch`, a retry loop, a timeout, or anything crossing a failure boundary (an external
call, a queue handler, a background job). `dotnet-code-review` validates against it as part of the
`[correctness]` aspect. A consuming repo overrides it via `.claude/dotnet-toolkit/error-handling.md`.

## Exceptions vs. expected outcomes

**Reserve exceptions for the unexpected.** A normal, anticipated outcome — not found, validation failed,
business rule declined — is a return value (`bool`/`Try*`, a nullable, a small result type), not a
thrown-and-caught exception; see `antipatterns.md`'s exceptions-for-control-flow entry, which this file
gives the full standard for. An exception is for a state the method's contract didn't promise to handle:
a network failure, a violated invariant, a bug.

```csharp
// DON'T — expected "not found" modeled as an exception
public Order GetOrder(OrderId id)
{
    var order = _repo.Find(id);
    if (order is null) throw new OrderNotFoundException(id);
    return order;
}

// DO — expected outcome, ordinary return
public Order? FindOrder(OrderId id) => _repo.Find(id);
```

## Catching

**Never catch bare `Exception` (or `catch { }`) except at a top-level boundary that must not crash** — a
host's unhandled-exception hook, a background-job runner reporting failure and continuing, a message
handler that must ack/dead-letter regardless. Everywhere else, catch the specific exception type(s) the
call is documented to throw. A broad catch that swallows the failure (see `antipatterns.md`) hides bugs
in whatever it wraps.

**Preserve the stack trace on rethrow**: `throw;` re-raises the caught exception with its original stack
trace intact; `throw ex;` resets it to the rethrow site, destroying the information needed to find where
the failure actually happened.

```csharp
// DON'T — loses the original stack trace
catch (HttpRequestException ex) { LogError(ex); throw ex; }

// DO
catch (HttpRequestException ex) { LogError(ex); throw; }
```

**Wrap, don't discard, when adding context** — a caught exception rethrown as a different type carries
the original as `InnerException`, never dropped:

```csharp
catch (SqlException ex)
{
    throw new OrderPersistenceException($"Failed to save order {order.Id}", ex);
}
```

## Custom exceptions

A custom exception type earns its existence when a caller needs to catch *that specific failure* and
react differently from a generic failure — not as a renamed wrapper around a message string. Include the
identifying data (an `OrderId`, a field name) as a property, not only interpolated into `Message`, so a
catching caller can act on it without parsing text.

## Retries

**Retry only an operation known to be idempotent, or made idempotent for the retry** (an idempotency key,
a natural upsert). Retrying a non-idempotent side-effecting call (charge a card, send an email) on
transient failure risks doing it twice. Use exponential backoff with jitter for a retried remote call,
not a fixed-interval loop, and cap the attempt count — an unbounded retry loop turns a transient failure
into an outage. Retry only exception types known to be transient (timeout, connection reset,
`HttpRequestException` on a 5xx) — never retry a 4xx/validation failure, which will fail identically
every time.

## Timeouts

Every outbound call with unbounded latency potential (HTTP, database, queue) gets an explicit timeout —
a `CancellationToken` with a deadline, not reliance on the callee's own default (which may be much longer
than this caller can tolerate, or absent). A timeout that fires is a normal, expected outcome to handle,
not a surprise.

## Failure boundaries

Decide, at the boundary that owns the decision, whether a failure should **fail fast** (propagate and
stop — most application logic) or **fail safe** (log, degrade, continue — a non-critical background
enrichment, a best-effort cache warm). Don't let an inner layer silently choose fail-safe by swallowing
an exception the caller above expected to see; make the choice explicit at the layer that owns it.

## Review calibration

A bare `catch (Exception)`/`catch { }` outside a documented top-level boundary, `throw ex;` destroying a
stack trace, or a retry loop on a non-idempotent side-effecting call is 🔴. An exception used for an
expected outcome (see `antipatterns.md`), a caught-and-wrapped exception that drops the original as
`InnerException`, or a remote call with no timeout/cancellation is 🟡. A custom exception type that adds
nothing over a generic one with a message, or a fixed-interval retry without backoff, is 🔵 unless it's
on a hot/high-volume path, which raises it to 🟡.
