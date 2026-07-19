# XML documentation guide

Default reference for `dotnet-doc-reviewer`. Overridable per-repo via
`.claude/dotnet-toolkit/xml-documentation.md` (see `docs/review-workflow.md` for the override mechanism).

## Read before judging

**Read the full implementation of a member before documenting or critiquing its documentation.** Never
infer behavior from its name alone — wrong documentation is worse than none, and a `<summary>` that just
restates the method name adds nothing. For a method that calls others whose behavior affects what the docs
should say, read those too. For a property, read the getter/setter logic (or how an auto-property is set in
constructors/callers). For a class, read enough of it to state its real role, then use `get_references` to
see how it's actually used elsewhere — a summary must reflect real behavior, not an assumption from the
name. If the real behavior can't be determined (an external dependency, generated code), say so explicitly
rather than guessing.

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
`<inheritdoc cref="Type.Member"/>` to avoid duplicating docs already stated on an interface or base member,
`<typeparamref name="T"/>`, `<paramref name="x"/>`, `<c>` for inline code, `<para>`/`<list type="bullet
|number|table">` for structured `<remarks>` content. `<example>`/`<code>` sparingly — a base type or a
non-obvious API shape, not every member.

## Tag separation — the most common finding

- `<summary>` is purpose only. **Bad**: `<summary>Loads the workspace. Runs on a background thread to stay
  under the startup timeout.</summary>` — the second sentence is a `<remarks>`. **Good**:
  `<summary>Loads the MSBuild workspace for the target solution.</summary>` plus a `<remarks>` for the
  background-thread/timeout detail.
- `<remarks>` is everything else: rationale, performance notes, caveats, non-obvious constraints.
- `<value>` describes the property's meaning/range — don't repeat it verbatim in `<summary>` too.

## `<inheritdoc/>`

Use it to avoid duplicated/drifting docs: an interface implementation inherits from the interface member's
docs; an override inherits from the virtual/abstract base; when two unrelated types share an identical
member (same signature, same meaning), designate one as the documentation source and reference it with
`<inheritdoc cref="Source.Member"/>` from the other rather than copy-pasting. If the overriding/implementing
member's actual behavior differs from the source, write new docs instead — inheriting misleading
documentation is worse than duplicating accurate documentation.

## Cross-referencing

Type-level docs get `<seealso cref="..."/>` for closely related types (an interface's primary
implementation, a paired request/response type). Member docs get `<see cref="..."/>` for parameter/return
types and closely related methods. Property docs get `<see cref="..."/>` when the value's meaning depends
on another type.

## Inline comments (not XML doc, but this agent's concern)

Add a brief inline comment only when: a non-obvious branch exists, a non-self-evident algorithm step needs
explaining, a workaround needs justifying, or an invariant (bounds, ordering, a caller contract) isn't
visible from the code itself. Explain **why**, never **what** — a comment restating what the next line
obviously does is noise. Do not touch an existing comment that's already accurate.

## What counts as "missing" vs "not needed"

Not every member needs a doc comment. A `private` helper with an unambiguous name and a two-line body
doesn't need a `<summary>` restating its name. The bar is: **public and protected members whose purpose,
parameters, or return value aren't obvious from the signature alone** — that's the actual target, not every
declaration in the file. Flag genuinely undocumented public API surface; don't flag a well-named private
one-liner for lacking a comment nobody would write by hand either.
