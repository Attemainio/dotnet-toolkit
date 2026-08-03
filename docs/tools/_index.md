# dotnet-toolkit tool router

**This file is the router. It is the only place that maps a question to a tool.** Read one
`docs/tools/<tool>.md` for how to call it — not this whole directory, and never the `.cs` file.

Tool names are prefixed `mcp__plugin_dotnet-toolkit_dotnet__`.

## Which tool answers which question

| You want | Call | Detail | Do NOT |
|---|---|---|---|
| Find symbols when you don't know exact names | `search_index` — **all terms in one call** | `search_index.md` | Grep/Glob over `.cs`; one call per word |
| A type or member's shape, docs, source, location | `get_symbol` | `get_symbol.md` | Read the `.cs` file |
| A type's member list | `get_symbol(include: "members")` | `get_symbol.md` | Read the file |
| One region of a long member | `get_symbol(include: "source:code@120-160")` | `get_symbol.md` | `Read`/`sed` on the file |
| Who calls it — just the list, one hop, **high fan-in** | `get_call_hierarchy(maxDepth: 1)` | `get_call_hierarchy.md` | `get_references` — 8× the tokens at 105 callers. Below ~a dozen callers it inverts: take `get_references` and get the sites free |
| Where exactly it's called — file, line, snippet | `get_references(direction: "callers")` | `get_references.md` | Grep the name — misses interface dispatch, returns comment hits |
| Implementations, derived types, overrides | `get_references(direction: "implementations"\|"overrides")` | `get_references.md` | Grep for `: IFoo` |
| Where a **type** is used — a class, record or delegate as a field, parameter, return or event type | `get_references(direction: "callers")` on the type | `get_references.md` | Grep the type name — hits comments, misses aliases |
| What is callable at a cursor — locals, inherited, extension methods | `get_scope` | `get_scope.md` | Guess, or grep for a helper that may not apply |
| Whether known X reaches known Y, and through what | `get_call_slice` | `get_call_slice.md` | Walk outward with repeated `get_references` |
| Who *eventually* calls this, several levels up | `get_call_hierarchy` | `get_call_hierarchy.md` | Chain `get_references` by hand |
| A type's full base chain and every implementer | `get_type_hierarchy` | `get_type_hierarchy.md` | Guess from `get_symbol`'s one-hop `containingType` |
| The project reference graph | `get_project_graph` | `get_project_graph.md` | Open every `.csproj` |
| Circular project references | `detect_circular_dependencies` | `detect_circular_dependencies.md` | Manually trace looking for a loop |
| What a commit or branch actually changed | `get_semantic_diff` | `get_semantic_diff.md` | Read `git diff` and infer |
| Why past code looks the way it does | `search_log` | `search_log.md` | Guess from the code |
| **To change a `.cs` file** | `validate_patch` | `validate_patch.md` | `Edit`/`Write`/`dotnet build` |
| **To rename a symbol and all its references** | `rename_symbol` | `rename_symbol.md` | `validate_patch` per call site; search-and-replace |
| Where your tokens went | `get_retrieval_metrics` | `get_retrieval_metrics.md` | — |
| Is the index/workspace warm | `workspace_status`, then `reload_workspace` | `server.md` | — |
| Is the server answering at all | `ping` | `server.md` | — |

Read a `.cs` file only for lines you are about to edit that `get_symbol` did not return. Non-C#
files (`.csproj`, `.json`, `.md`, `.cmd`) are normal `Read`/`Grep` territory.

## Typical chains

Each tool file ends with a **Next steps** section naming what to call with what it just returned.
The common routes:

- **"Does this class have references anywhere?"** → `search_index` (get the `symbolId`) →
  `get_references`. One hop; stop there.
- **"Who eventually calls this?"** → `get_call_hierarchy` (not `get_references` repeated).
- **"How does X reach Y?"** → `get_call_slice` — both endpoints must already be named.
- **"I need to change this method."** → `get_symbol` (keep `contentVersion` +
  `declarationSites`) → `validate_patch` with `applyOnSuccess: true` and an `intent`.
- **"Rename this."** → `get_symbol` (keep `contentVersion`) → `rename_symbol` dry run to see the
  blast radius → the same call with `applyOnSuccess: true` and an `intent`. Never a chain of
  `validate_patch` calls: the reference rewrite is Roslyn's job, not yours to author.
- **"What did this branch change, and why?"** → `get_semantic_diff` → `search_log`.

## Workspace readiness

`limitedBy` names what the answer could **not** draw on. It applies to every retrieval tool, so it
is stated once, here.

- **absent** — fully informed. Silence is the healthy case.
- **`index_only`** — answered from the syntax tier. Reference counts and semantic resolution are
  unavailable, **not zero**.
- **`stale`** — the file changed on disk since the workspace read it. Call `reload_workspace`, then
  re-read: line spans will have moved. Never build a patch on a `stale` response.
- **`degraded`** — the workspace loaded but projects failed. Results may be silently **wrong**, not
  just thin. Call `workspace_status`, fix the build, then `reload_workspace`. Do not report findings
  from a degraded workspace without saying so.

`search_index`/`get_symbol` answer from the syntax index immediately. `get_references` needs live
semantics and returns `error: "workspace_loading"` until ready. After a large git operation, call
`reload_workspace`.

## Reading responses

Responses are **TOON** by default (same field names and nesting as the JSON shown in each tool
file, more compact encoding). `set_output_format(format: "compact"|"json")` switches for the
session; `defaultFormat` in `.claude/dotnet-toolkit/config.json` sets what a fresh server starts
with.

Fields that are absent carry no information: `limitedBy` appears only when something limited the
answer, `changed` only when `false`, `truncated` only when true. **Absent is not zero** — an absent
`tests` means "not computed", not "no tests".

The write path states the same distinction positively rather than by omission: `validate_patch` and
`rename_symbol` return a **`checks`** block on every call, listing which validation rungs ran and over
what, the analyzer pass's findings by severity, and an explicit `notAssessed` list. Report the scope it
names — a clean rung is clean over what `scope` says, nothing wider. See `validate_patch.md`.
