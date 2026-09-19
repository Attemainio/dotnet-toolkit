# `get_symbol` — where a symbol lives, and what it looks like from outside

A locator. Give it a name you already know — a type, a method, an overload, a `sym_…` id from any
earlier response — and it answers **where** (every declaration site, doc comment included, one entry
per part of a partial) and **what shape** (signature, modifiers, and on request the docs, members,
attributes, base type, interfaces, usings, a control-flow outline and reference counts).

**It returns no source text.** The text is one `Read` away, and the response carries the exact
range:

```
get_symbol(symbol: "Sample.Lib.Widget.Spin")
  → declarationSites: [{file: "Lib/Widget.cs", startLine: 12, endLine: 24, signatureLine: 15}]

Read(file: "Lib/Widget.cs", offset: 12, limit: 13)        # endLine - startLine + 1
```

A ranged `Read` draws no advisory from the read guard; a whole-file `Read` does. That is the whole
division of labour: the toolkit finds, `Read` fetches, `Edit` changes, `dotnet build` checks.

Don't know the name yet? `search_index` finds it; this tool locates what it found.

## The line contract

Every call returns `declarationSites`, computed live from the syntax tree — correct for a partial
split across files, and not dependent on the index being current.

| Field | Meaning |
|---|---|
| `file` | Repo-root-relative, forward slashes. |
| `startLine` | Where the declaration's leading `///` doc comment begins, when it has one. Otherwise the same line as `signatureLine`. **This is the `Read` offset.** |
| `signatureLine` | Where the declaration itself starts: the line with the `class`/method keyword. **Present only when it differs from `startLine`**, so absence means "same as `startLine`", not "not computed". This is the number to quote when answering "where is this declared" — `search_index` reports the same line as a hit's `line`. |
| `endLine` | The closing brace, or the last line of an expression-bodied member. `Read(limit: endLine - startLine + 1)` covers the whole declaration. |

Attribute lines sit between `startLine` and `signatureLine` when both a doc comment and attributes
are present; with attributes but no doc comment, `startLine` is the first attribute line.

A **partial** type carries one entry per part, in source order. Read the parts you need; a member
is always a single-file declaration and reads normally.

**`origin: "external"`** marks a BCL/NuGet symbol, resolvable only because this repo's code
uses it (`search_index(origin: "external")` finds such symbols). Its `declarationSites` is `[]`,
and the field exists to say why. Absent `origin` means the ordinary case.

**`generated: true`** marks a symbol whose every declaration is source-generator output under
`obj/`. `declarationSites` is `[]` — there is no editable text, the file is rewritten on every
build — and the flag says that this is why, not a lookup bug.

## `include` — which components

One argument, one grammar. It only ever adds; there is no `-` and no `:` in it.

1. **Omitted, or `"standard"`** — `xmlDoc` and `referenceCounts`. Start here.
2. **`"all"`** — every component below. Rarely the right call: `members` on a large type and
   `mechanicalFacts` both cost real tokens, and a navigation question seldom needs both at once.
3. **A comma list**, e.g. `"members,baseType"` — **replaces** the default set. Use it whenever
   `standard` is close but not quite right.

An unrequested component is absent from the JSON entirely, not `null`. A misspelled name is an
`invalid_component` error rather than being silently dropped.

`kind`, `displayString`, `modifiers`, `containingType` and `declarationSites` are unconditional.
There is no `accessibility` field — `modifiers` (`"public sealed"`) already carries it.

| Component | Returns |
|---|---|
| `xmlDoc` | `{summary, returns, remarks, value, inheritdoc, params, typeParams, exceptions}`, XML-stripped to plain text, each absent when that tag is not present. `params`/`typeParams` are `[{name, text}]`; `exceptions` is `[{type, text}]`; `inheritdoc` is `true` when `<inheritdoc/>` is present. Whole component absent when none of these tags exist. |
| `referenceCounts` | `{callers, tests}` for a member (never a type), plus `{implementations, overrides}` only where the kind makes a non-zero answer possible. An enum, static class or plain method omits them rather than reporting a structural `0`; the whole component is absent when nothing is left to report. |
| `members` | Type only: `[{symbolId, displayString, kind, line, shape}]` per member — **private included**, since `M` in `search_index`'s `shape` counts them too — plus `file` when a member is declared in a different file (a partial). A `shape` legend and, when the table mixes a blank and a named `file`, a `fileLegend` sit beside the list. **Capped at 40**: a type declaring more carries `totalMembers` and `truncated: true` alongside the first 40. `search_index(pathPrefix: <the file>, kinds: "method property field")` reaches the rest. |
| `bodyOutline` | Control-flow landmarks (`switch/case`, `if`, `for/foreach/while/do`, `catch`, `using`, `lock`) as `[{text, startLine, endLine, depth}]`, for mapping a long body before reading part of it. Purely syntactic. Absent, with `bodyOutlineNote`, for anything without an executable body (a type, a field, an auto-property). `bodyOutlineNote` also appears — rows still returned — when the outline is unlikely to earn its cost: a short body, or a mostly linear one. |
| `mechanicalFacts` | What the body *does to the world*: `throws`, `awaits`, `writes`, `locks`, `implementsMembers`, `overrides` — empty ones omitted. `null` if the body changed since they were computed. Reach for it over `bodyOutline` when the question is "is this safe under a lock", "does it do I/O", "which interface member is this" rather than "where in this body is the loop". Semantic tier; costs more than the outline. |
| `attributes` | This symbol's own (non-inherited) attributes as `[{name, arguments}]`; `name` strips a trailing `Attribute`. Compiler-supplied arguments (`[CallerMemberName]`) are dropped. Absent when none. |
| `baseType` | Type only: `{symbolId, displayString}` for the direct base only. The transitive chain is `get_type_hierarchy`. |
| `interfaces` | Type only: `[{symbolId, displayString}]` for the interfaces named on the declaration, not `AllInterfaces`. |
| `usings` | The file's `using` directives, in source order. |

## Several symbols in one call

`symbols` fetches a list instead of `symbol` fetching one — the same `include` applied to every
entry:

```
get_symbol(symbols: ["Sample.Lib.Widget", "Sample.Lib.IWidget"])
```

`results` is an array, one entry per requested symbol. A successful entry has `symbolId`,
`limitedBy` and `content`; a symbol that did not resolve has `error` instead (`symbol_not_found`,
`ambiguous_symbol`) — one bad lookup does not fail the batch. Whatever **every** entry repeats
verbatim lifts into a `shared` block beside `results`, but only when that is genuinely smaller. The
batch's win is round trips; on tokens it is roughly a wash.

## Resolution failures

`ambiguous_symbol` hoists the prefix every candidate shares (`sharedPrefix`) so each candidate's
`displayString` carries only the part that differs — concatenate the two, or pass the candidate's
`symbolId` instead. Candidates are capped at 10; `totalCandidates` states the true match count,
with `truncated: true` when the cap bit. **A name like `Run` can match fifty members in a test
tree — an absent candidate is not an absent symbol.** Qualify with the containing type or
namespace, or append a parameter list (`Widget.Spin(int)`).

`symbol_not_found` carries **`didYouMean`** — up to 5 ranked near-misses for the failed name's last
segment, each `{symbolId, name, kind}`. It is a ranked index lookup first (recovers a wrong namespace
or a dropped containing type), kept to hits whose own unqualified name still contains the segment you
typed, then an edit-distance scan (≤3 edits, case-insensitive) only if that leaves nothing. Absent
when the segment is under 3 characters, the name was a `sym_…` id, or nothing ranked.

**Every symbol-resolving tool returns these same shapes** — `get_references`, `get_call_hierarchy`,
`get_call_slice`, `get_type_hierarchy`, `rename_symbol` too.

## `limitedBy`

| Value | Means |
|---|---|
| absent | Answered from the live compilation. |
| `index_only` | The workspace is not loaded yet; the answer came from the syntax index alone. `declarationSites` is still right; `referenceCounts`, `mechanicalFacts`, `baseType`/`interfaces` and `members`' symbol ids are not available or provisional (`symidx_…` ids). Check `workspace_status`, retry once it reports `loaded`. |
| `stale` | The file changed since the sweep last saw it. The next sweep (within a couple of seconds) clears it; a re-call after that is current. |
| `degraded` | A project failed to load. Everything from the other projects is fine; anything that depends on the failed one is missing, not thin. `workspace_status` names the project. |

## Gate expansion on `referenceCounts`

`{callers, tests, implementations, overrides}` decides whether an expansion is worth the tokens:

- **0 callers** → usually nothing to find — **but not if the symbol can be invoked without being
  named.** `callers` counts static call sites in the loaded solution; anything a framework invokes by
  reflection (an entry point, a DI-registered implementation, a serialization target, a test) is
  invisible to it. **Never conclude "dead code" from a 0 alone.**
- **1–5 and a signature change is planned** → `get_references`, then read each site.
- **more than 5** → `get_references` for the list, then read only what you will actually change.

Before writing a helper that plausibly already exists, `search_index` first.

## Reference

| Arg | Meaning |
|---|---|
| `symbol` / `symbols` | Fully-qualified name, unique suffix, `Name(ParamType)` to pick an overload, or a `sym_…` id from any earlier response. Exactly one of the two. |
| `include` | Omitted/`"standard"` (default) \| `"all"` \| a comma list of component names that replaces the default. |

Default call, real response:

```
get_symbol(symbol: "SymbolStore.SymbolsDeclaredIn")
```
```json
{"symbolId":"sym_c25d7c88b0e916b0",
 "content":{"kind":"Method",
   "displayString":"IReadOnlyList<string> SymbolStore.SymbolsDeclaredIn(string file)",
   "modifiers":"public",
   "containingType":{"symbolId":"sym_fc346a8c5efa6a88","displayString":"SymbolStore"},
   "declarationSites":[{"file":"src/DotnetToolkit.McpServer/Store/SymbolStore.Update.cs",
                         "startLine":27,"endLine":38,"signatureLine":30}],
   "xmlDoc":{"summary":"The ids of every symbol whose declaration lies in the given file."},
   "referenceCounts":{"callers":2,"tests":3}}}
```

## Next steps

- **Read the code** → `Read(file, offset: startLine, limit: endLine - startLine + 1)`.
- **Read part of a long member** → `include: "bodyOutline"`, then a `Read` over the landmark's
  `startLine`–`endLine`.
- **Expand to call sites** → `get_references` — `get_references.md`.
- **Open-ended caller tree** → `get_call_hierarchy` — `get_call_hierarchy.md`.
- **Change it** → `Edit`, anchored on the text you read; `dotnet build` afterwards. A rename goes
  through `rename_symbol` — `rename_symbol.md`.
