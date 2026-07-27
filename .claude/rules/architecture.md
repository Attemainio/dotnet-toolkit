---
paths:
  - "**/*.cs"
---

# .NET architecture

Canonical architecture standard. Loaded on demand per `csharp-standards.md`'s index; read it before
adding a project reference, a new abstraction, or a change that crosses a project/namespace boundary.
`dotnet-code-review` validates against it as part of the `[correctness]` aspect — it is not a separate
review aspect, the same way `naming.md`/`styling.md` fold in. A consuming repo overrides it via
`.claude/dotnet-toolkit/architecture.md`.

## Dependency direction

**A lower-level project never references a higher-level one.** Domain/core logic has no project
reference to infrastructure (EF Core, HTTP clients, file I/O) or presentation; infrastructure depends on
domain abstractions, not the other way around. `get_project_graph` shows the actual reference edges —
check it rather than assuming the intended direction holds.

```csharp
// DON'T — Domain project referencing Infrastructure to use a concrete repository
namespace Orders.Domain;
using Orders.Infrastructure; // wrong direction: Domain -> Infrastructure

// DO — Domain declares the abstraction; Infrastructure implements it and depends inward
namespace Orders.Domain;
public interface IOrderRepository { Task<Order?> FindAsync(OrderId id, CancellationToken ct); }
```

**No circular project references.** `detect_circular_dependencies` finds cycles directly — a cycle
signals two projects that should be one, or a piece that needs to move to a third project both can
depend on.

## Boundaries and cohesion

**A project/namespace boundary should track a real seam** — a deployment unit, a team ownership line, a
genuinely swappable implementation — not an arbitrary split of related code. Splitting one cohesive
concept across projects "for organization" adds reference-management cost without a corresponding
benefit; merging unrelated concepts into one project to avoid creating a new one does the same in
reverse.

**Depend on the smallest interface a caller actually needs**, not the concrete type or a fat interface
covering every implementer's members — see `antipatterns.md`'s leaky-abstraction and
service-locator entries, which this file gives the full standard for.

```csharp
// DON'T — caller only reads; forcing it to depend on the full read/write repository
public sealed class OrderSummaryQuery(IOrderRepository repo) { ... }

// DO — depend on exactly what's used
public interface IOrderReader { Task<Order?> FindAsync(OrderId id, CancellationToken ct); }
public sealed class OrderSummaryQuery(IOrderReader repo) { ... }
```

## Composition over inheritance

Prefer a small, composed set of collaborators over a deep inheritance chain to share behavior — a base
class enforces one shape on every derived type forever, while a composed dependency can be swapped or
tested independently. Reach for inheritance only for genuine is-a substitutability
(`get_type_hierarchy` shows the actual chain when judging whether one already exists and is being
extended sensibly, versus grown past its original purpose).

## Layering inside a project

Keep the same inward-dependency rule *within* a project's own folders, not just across project
references — a `Controllers/` or `Endpoints/` folder depends on an `Application`/`Services` folder,
which depends on `Domain`, never the reverse. A domain type referencing a controller or a DTO defined in
the presentation layer is the same violation as a bad project reference, just invisible to
`get_project_graph`.

## Review calibration

A `Domain`/`Core` project with a project reference to `Infrastructure`/`Web`, or a cycle
`detect_circular_dependencies` reports, is 🔴 — cite the actual edge, not a suspicion. A god class/god
method, leaky abstraction, or service-locator use (per `antipatterns.md`) inside one file is 🟡 unless
it's already crossing a project boundary, which raises it to 🔴. A single-implementer interface with no
visible testability or planned-second-implementer intent is 🔵 — a question, not an assumed mistake;
`search_log` first for a recorded reason before flagging it.
