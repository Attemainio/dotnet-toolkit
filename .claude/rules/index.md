# C# in this repo: the dotnet-toolkit index

This repo has the dotnet-toolkit plugin — a Roslyn-powered MCP server. Its tools are the default path
for C#, not Grep, Glob, `find`, `ls`, `cat`, bare `Read`, or `Edit`/`Write`. This file is the single
always-loaded rule and the only router.

Grep and Read give **wrong answers** on C#, not merely slower ones: text search cannot see interface,
virtual, or delegate dispatch, counts comment and string matches as real hits, under-reports silently
when output is truncated, and returns one fragment of a partial class with no signal the rest exists.

Tool names are prefixed `mcp__plugin_dotnet-toolkit_dotnet__`.

## Which tool

| Instead of | Use |
| --- | --- |
| Grep/Glob over `.cs`; one call per word | `search_index` — **every term in ONE call**, OR-ed and ranked; the per-term floor is shallow, so still read `termsWithNoHits` |
| `Read` on a `.cs` file | `get_symbol` — whole symbol across partials; `include` picks the fields |
| Reading a file for a type's member list | `get_symbol(include: "members")` |
| `Read`/`sed` for one region of a long member | `get_symbol(include: "source:code@120-160")` to read; drop the `:code` to patch from — a stripped fetch hides lines your edit span would still overwrite |
| Grep for callers | `get_references(direction: "callers")` — file, line, snippet per site |
| Grep for `: IFoo` | `get_references(direction: "implementations"` / `"overrides")` |
| Grep a type name for its uses | `get_references(direction: "callers")` on the type |
| Just the caller list at high fan-in — 8× the tokens at 105 callers | `get_call_hierarchy(maxDepth: 1)`; below ~a dozen callers it inverts and `get_references` gives the sites free |
| Chaining `get_references` by hand, several levels up | `get_call_hierarchy` |
| Walking outward with repeated `get_references` | `get_call_slice` — whether known X reaches known Y, and through what |
| Guessing whether a helper applies, or grepping for one | `get_scope` — what is callable at a line, including inherited and extension methods |
| Guessing from `get_symbol`'s one-hop `containingType` | `get_type_hierarchy` — full base chain and every implementer |
| Opening every `.csproj` to trace project references | `get_project_graph` |
| Tracing project references looking for a loop | `detect_circular_dependencies` |
| Reading `git diff` and inferring | `get_semantic_diff` — what a commit or branch actually changed |
| Guessing why code looks the way it does | `search_log` |
| `Edit`/`Write` then `dotnet build` | `validate_patch` — **the only way to change a `.cs` file** |
| Search-and-replace, or one patch per call site | `rename_symbol` — renames a symbol and every reference |
| Wondering where the tokens went | `get_retrieval_metrics` |
| Wondering whether the index/workspace is warm | `workspace_status`, then `reload_workspace` if stale |
| Wondering whether the server is answering at all | `ping` |
| Wondering how to get plain JSON instead of TOON | `set_output_format` — `compact`/`json` for the session |

### Loading a tool: take the name from this table

Schemas are deferred, so a tool must be loaded before it can be called. **Load it by exact name —
`ToolSearch("select:mcp__plugin_dotnet-toolkit_dotnet__get_references")` — never by describing what
you want.** Keyword search ranks unrelated tools above the right one: measured here, "who calls this
method" never surfaced `get_references` at all. The table picks the tool; ToolSearch only fetches it.

**The schema is then enough for an ordinary call.** Reach for a manual only for an advanced selector,
an unfamiliar response field, or error recovery. Each ends with a **Next steps** section naming what
to call with what it just returned.

### Reaching a plugin file

**Every tool's manual is `<tool>.md`** — `get_references.md`, `validate_patch.md` — except the four
server/meta tools (`workspace_status`, `reload_workspace`, `ping`, `set_output_format`), which share
`server.md`. Standards are named by the table below.

Those names are bare because a rule is delivered **literally** into whatever repo it is installed in
and cannot hold a path to the plugin. Call `workspace_status` once per session, take its `pluginRoot:`
line, and join — `<pluginRoot>/docs/tools/<name>` for a manual, `<pluginRoot>/standards/<name>` for a
standard. `Read` the joined path; the guards only block `.cs`.

**Never write `${CLAUDE_PLUGIN_ROOT}` into a path yourself.** The harness substitutes it into
`.mcp.json` args, hook commands and skill content, but **not** into a rule or an agent definition, so
it would stay literal and the read would fail. `workspace_status` is the supported way, and it never
prompts.

`Read` a `.cs` file only for lines you are about to edit that `get_symbol` did not return. Non-C#
files (`.csproj`, `.json`, `.md`, `.cmd`) are normal `Read`/`Grep` territory. `PreToolUse` hooks block
`Read`, `Edit`/`Write` and shell reads (`cat`/`grep`/`sed`/…) on a compiled `.cs` file.

## Workspace readiness — `limitedBy`

`limitedBy` names what the answer could **not** draw on. It applies to every retrieval tool.

- **absent** — fully informed. Silence is the healthy case.
- **`index_only`** — the syntax tier answered. Reference counts and semantic resolution are
  unavailable, **not zero**.
- **`stale`** — the file changed on disk since the workspace read it. `reload_workspace`, then
  re-read: line spans will have moved. Never build a patch on a `stale` response.
- **`degraded`** — projects failed to load. Results may be silently **wrong**, not merely thin. Call
  `workspace_status`, fix the build, then `reload_workspace`. Never report a finding from a degraded
  workspace without saying so.

`get_references` needs live semantics and returns `error: "workspace_loading"` until ready. After a
large git operation, call `reload_workspace`.

## Reading responses

Responses are **TOON** by default — same field names as the JSON in each tool file, more compact.
`set_output_format(format: "compact"|"json")` switches for the session.

**Absent is not zero.** An absent field carries no information: an absent `tests` means "not
computed", not "no tests". A `null` is dropped rather than written as `"field":null`, so check for the
key's absence, not its value. The one deliberate exception is the `shape` column, whose legend says
so: an absent letter means zero or not-applicable, never "not computed".

`validate_patch` and `rename_symbol` state it positively, returning a **`checks`** block on every call
— which rungs ran and over what, plus an explicit `notAssessed` list. **Report the scope it names**: a
clean rung is clean over what `scope` says, nothing wider.

Every tool that records telemetry takes an optional **`taskId`**. No tool takes a `sessionId`.

## Exploring — delegate the sweep when the symbol set is unknown

**Before writing or changing C#, when the set of symbols a task touches is not already known,
delegate the sweep to the `dotnet-explore` agent** rather than fanning out `search_index` /
`get_references` here. It spends the wide responses in its own context and hands back `symbolId`s, use
sites and the blast radius; it is read-only and cannot start editing instead.

**Skip it** when the symbol is already known, or when the next step needs a `contentVersion` — the
agent relays none. An unfamiliar subsystem is what this is for; a two-call lookup is not.

## Writing

**Before the first `.cs` change of a session, invoke the `dotnet-change` skill.** A precondition, not
a suggestion: it carries the write procedure, the pre-edit standards step and the failure modes.
Reaching for `validate_patch` without it is how a patch gets built on a declaration-only
`contentVersion` and rejected, or applied with no `intent` recorded.

`validate_patch` is the write path and the **only** writer to the development log; a pure rename is
`rename_symbol`, which derives every reference edit from the compiler's graph. An edit that bypasses
them is a change whose reasoning is gone when the conversation ends — `search_log` cannot recover it,
and the next session re-derives or silently contradicts it.

## Coding standards

Read the relevant ones before the first C# edit of a session, resolving each as above:
`<pluginRoot>/standards/<name>`. Plugin-owned and the only copy — there is no per-repo override.
`dotnet-change` walks this for you.

| Read | When |
| --- | --- |
| `naming.md`, `styling.md`, `best-practices.md`, `xml-documentation.md` | every C# change — the baseline set |
| `architecture.md` | new/changed project or namespace boundaries, dependency direction, layering, a new abstraction |
| `api-design.md` | a public or internal API surface change: new/changed method signature, nullability, collection return types, async shape, cancellation |
| `error-handling.md` | exceptions, result/error patterns, retries, timeouts, failure propagation across a boundary |
| `resource-management.md` | `IDisposable`/`IAsyncDisposable`, streams, unmanaged resources, pooling, ownership transfer |
| `security.md` | endpoints, auth, SQL, configuration/credentials, logging, crypto |
| `performance.md` | hot paths: tight loops, per-request/per-tick code, buffers, SIMD, `unsafe` |
| `concurrency.md` | anything that awaits, locks, spawns work, or shares state across threads |
| `testing.md` | writing or modifying tests |
| `antipatterns.md` | the shared catalog — skim once per session; cited by name everywhere else |

**The "When" column is also the `dotnet-code-review` agent's load rule.** It reads a fixed core —
`naming.md`, `styling.md`, `best-practices.md`, `xml-documentation.md`, `antipatterns.md`,
`security.md` — every time, then only the rows the retrieved code triggers. So a "When" cell must
state an **observable property of the code** (it awaits, it is a hot path, it is a public surface
change), not a topic — one that cannot be matched against retrieved source gets skipped or over-loaded.
An untriggered aspect is reported not-assessed, never clean.

## Skills — when to invoke

Their descriptions are already loaded. These are the mandates a description cannot express.

- **Before the first C# edit of a session** → `dotnet-change`.
- **Any review request** → `dotnet-review`. Never review C# inline.

## Everything the MCP surface doesn't cover

Shell and plain file tools: `dotnet build` / `dotnet test` / `dotnet publish`, `git`, and reading or
editing non-C# files (Markdown, JSON, `.cmd`, `.csproj`, skill and agent definitions).
