---
paths:
  - "**/*.cs"
---

# XML documentation

Canonical XML-doc standard. Loaded on demand per `csharp-standards.md`'s index; read it before writing or
editing doc comments — every symbol touched through `validate_patch` that lacks a `<summary>` gains one
in the same edit (see the `dotnet-change` skill). `dotnet-code-review` validates against it (aspect
`[docs]`). A consuming repo overrides it via `.claude/dotnet-toolkit/xml-documentation.md`.

## Read before writing or judging

**Read the full implementation of a member before documenting it or critiquing its documentation.** Never
infer behavior from a name — wrong documentation is worse than none, and a `<summary>` that restates the
method name adds nothing. For a method calling others whose behavior affects the docs, read those too.
For a property, read the getter/setter logic (or how an auto-property is set by constructors/callers).
For a class, read enough to state its real role, then check `get_references` for how it's actually used.
If real behavior can't be determined (external dependency, generated code), say so explicitly rather than
guessing.

## Required tags, by member kind

| Tag | Where | Purpose |
|---|---|---|
| `<summary>` | Every public/protected type and member | **What it does** — purpose only, 1–2 sentences. Never performance/implementation/allocation detail. |
| `<param name="x">` | Methods, constructors with parameters | What the parameter controls/represents. Reference another parameter with `<paramref name="other"/>`. |
| `<returns>` | Non-void methods | What the return value represents, including edge cases (null, empty, a sentinel). |
| `<value>` | Properties | Property equivalent of `<returns>` — what the value represents. |
| `<exception cref="T">` | Methods that throw on a reachable path | Which exception, and under what condition. |
| `<typeparam name="T">` | Generic types/methods | What the type parameter represents, and any constraint's meaning. |

## Contextual tags — use when applicable

`<remarks>` (design rationale, performance characteristics, allocation strategy, non-obvious usage
patterns — this is where "optimized to avoid an allocation on the hot path" belongs, **never** in
`<summary>`), `<see cref="Type"/>` / `<seealso cref="Type"/>` for related types, `<inheritdoc/>` /
`<inheritdoc cref="Type.Member"/>` to avoid duplicating docs already stated on an interface or base
member, `<typeparamref name="T"/>`, `<paramref name="x"/>`, `<c>` for inline code,
`<para>`/`<list type="bullet|number|table">` for structured `<remarks>` content. `<example>`/`<code>`
sparingly — a base type or a non-obvious API shape, not every member.

## Tag separation — the most common mistake

- `<summary>` is purpose only.

```csharp
// DON'T — the second sentence is implementation detail
/// <summary>Loads the workspace. Runs on a background thread to stay under the startup timeout.</summary>

// DO — purpose in <summary>, mechanics in <remarks>
/// <summary>Loads the MSBuild workspace for the target solution.</summary>
/// <remarks>Runs on a background thread so the MCP handshake completes within the startup timeout.</remarks>
```

- `<remarks>` is everything else: rationale, performance notes, caveats, non-obvious constraints.
- `<value>` describes the property's meaning/range — don't repeat it verbatim in `<summary>` too.

## `<inheritdoc/>`

Use it to avoid duplicated, drifting docs: an interface implementation inherits from the interface
member; an override inherits from the virtual/abstract base; when two unrelated types share an identical
member (same signature, same meaning), designate one as the documentation source and reference it with
`<inheritdoc cref="Source.Member"/>` from the other. If the implementing member's actual behavior
differs from the source, write new docs instead — inheriting misleading documentation is worse than
duplicating accurate documentation.

## Cross-referencing

Type-level docs get `<seealso cref="..."/>` for closely related types (an interface's primary
implementation, a paired request/response type). Member docs get `<see cref="..."/>` for
parameter/return types and closely related methods. Property docs get `<see cref="..."/>` when the
value's meaning depends on another type.

## Inline comments

Add a brief inline comment only when: a non-obvious branch exists, a non-self-evident algorithm step
needs explaining, a workaround needs justifying, or an invariant (bounds, ordering, a caller contract)
isn't visible from the code itself. Explain **why**, never **what** — a comment restating the next line
is noise. Don't touch an existing comment that's already accurate.

## What counts as "missing" vs "not needed"

Not every member needs a doc comment. A `private` helper with an unambiguous name and a two-line body
doesn't need a `<summary>` restating its name. The bar: **public and protected members whose purpose,
parameters, or return value aren't obvious from the signature alone.** Flag genuinely undocumented
public API surface; don't flag a well-named private one-liner for lacking a comment nobody would write
by hand either.

## Review calibration

A present-but-wrong `<summary>` is 🔴 — it actively misleads. Missing docs on non-obvious public surface
are 🟡. Missed `<inheritdoc/>`/cross-referencing opportunities and inline-comment quality are 🔵. Survey
with `get_symbol` (`include: "xmlDoc"`) — `xmlDoc.summary` absent is the missing-doc signal — rather
than eyeballing source for `///` blocks.
