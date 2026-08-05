# C# in this repo: the dotnet-toolkit index

This repo has the dotnet-toolkit plugin — a Roslyn-powered MCP server. Its tools are the default path
for C#, not Grep, Glob, `find`, `ls`, `cat`, bare `Read`, or `Edit`/`Write`. This file is the single
always-loaded rule and the only router: it maps a question to a tool, names the reference file for
how to call it, and says which standards apply when.

Grep and Read give **wrong answers** on C#, not merely slower ones: text search cannot see interface,
virtual, or delegate dispatch, counts comment and string matches as real hits, under-reports silently
when output is truncated, and returns one fragment of a partial class with no signal the rest exists.

Tool names are prefixed `mcp__plugin_dotnet-toolkit_dotnet__`.

## Which tool, and where its manual is

| Instead of | Use | Read |
| --- | --- | --- |
| Grep/Glob over `.cs`; one call per word | `search_index` — **all terms in ONE call**, OR-ed and ranked, each term guaranteed a floor share of `limit`; that floor is shallow, so still read `termsWithNoHits` | `search_index.md` |
| `Read` on a `.cs` file | `get_symbol` — whole symbol across partials; `include` picks the fields | `get_symbol.md` |
| Reading a file for a type's member list | `get_symbol(include: "members")` | `get_symbol.md` |
| `Read`/`sed` for one region of a long member | `get_symbol(include: "source:code@120-160")` | `get_symbol.md` |
| `get_references` at high fan-in — 8× the tokens at 105 callers | `get_call_hierarchy(maxDepth: 1)` for just the caller list. Below ~a dozen callers it inverts: use `get_references` and get the sites free | `get_call_hierarchy.md` |
| Grep for callers — misses interface dispatch, returns comment hits | `get_references(direction: "callers")` — file, line, snippet | `get_references.md` |
| Grep for `: IFoo` | `get_references(direction: "implementations"` / `"overrides")` | `get_references.md` |
| Grep a type name for its uses — hits comments, misses aliases | `get_references(direction: "callers")` on the type | `get_references.md` |
| Guessing whether a helper applies, or grepping for one | `get_scope` — what is callable at a line, including inherited and extension methods | `get_scope.md` |
| Walking outward with repeated `get_references` | `get_call_slice` — whether known X reaches known Y, and through what | `get_call_slice.md` |
| Chaining `get_references` by hand, several levels up | `get_call_hierarchy` | `get_call_hierarchy.md` |
| Guessing from `get_symbol`'s one-hop `containingType` | `get_type_hierarchy` — full base chain and every implementer | `get_type_hierarchy.md` |
| Opening every `.csproj` to trace project references | `get_project_graph` | `get_project_graph.md` |
| Manually tracing project references looking for a loop | `detect_circular_dependencies` | `detect_circular_dependencies.md` |
| Reading `git diff` and inferring | `get_semantic_diff` — what a commit or branch actually changed | `get_semantic_diff.md` |
| Guessing why code looks the way it does | `search_log` | `search_log.md` |
| `Edit`/`Write` then `dotnet build` | `validate_patch` — **the only way to change a `.cs` file** | `validate_patch.md` |
| Search-and-replace, or one patch per call site | `rename_symbol` — renames a symbol and every reference | `rename_symbol.md` |
| Wondering where the tokens went | `get_retrieval_metrics` | `get_retrieval_metrics.md` |
| Wondering whether the index/workspace is warm | `workspace_status`, then `reload_workspace` if stale | `server.md` |
| Wondering whether the server is answering at all | `ping` | `server.md` |

### Resolving a filename in the `Read` column

Every filename in this file — the `Read` column above and the standards table below — is bare,
because a rule is delivered **literally** into whatever repo it is installed in and cannot hold a
path to the plugin. Resolve one like this:

1. **Call `workspace_status` once per session.** It returns a `pluginRoot:` line — the plugin's
   installation directory, derived at runtime, correct on any machine.
2. **Join it with the subdirectory for that kind of file:**
   - a tool manual → `<pluginRoot>/docs/tools/<name>` — e.g. `<pluginRoot>/docs/tools/get_symbol.md`
   - a coding standard → `<pluginRoot>/standards/<name>` — e.g. `<pluginRoot>/standards/styling.md`
3. `Read` the joined path. These are Markdown, so `Read` is the right tool — the guards only block
   `.cs`.

Never write `${CLAUDE_PLUGIN_ROOT}` into a path yourself: the harness substitutes it into
`.mcp.json` args, hook commands and skill content, but **not** into a rule file or an agent
definition, so it would stay a literal string and the read would fail. `workspace_status` is the
supported way to learn the value, and it is on the read-only allowlist, so it never prompts.

Each tool manual ends with a **Next steps** section naming what to call with what it just returned.
The `dotnet-code-query` skill does all of the above for you if you would rather delegate the lookup.

`Read` a `.cs` file only for lines you are about to edit that `get_symbol` did not return. Non-C#
files (`.csproj`, `.json`, `.md`, `.cmd`) are normal `Read`/`Grep` territory. `PreToolUse` hooks
block `Read`, `Edit`/`Write`, and shell reads (`cat`/`grep`/`sed`/…) on a compiled `.cs` file —
reaching for one costs a round trip and returns nothing.

## Workspace readiness — `limitedBy`

`limitedBy` names what the answer could **not** draw on. It applies to every retrieval tool.

- **absent** — fully informed. Silence is the healthy case.
- **`index_only`** — answered from the syntax tier. Reference counts and semantic resolution are
  unavailable, **not zero**.
- **`stale`** — the file changed on disk since the workspace read it. Call `reload_workspace`, then
  re-read: line spans will have moved. Never build a patch on a `stale` response.
- **`degraded`** — the workspace loaded but projects failed. Results may be silently **wrong**, not
  merely thin. Call `workspace_status`, fix the build, then `reload_workspace`. Never report a
  finding from a degraded workspace without saying so.

`search_index`/`get_symbol` answer from the syntax index immediately. `get_references` needs live
semantics and returns `error: "workspace_loading"` until ready. After a large git operation, call
`reload_workspace`.

## Reading responses

Responses are **TOON** by default — same field names and nesting as the JSON in each tool file, more
compact encoding. `set_output_format(format: "compact"|"json")` switches for the session.

**Absent is not zero.** A field that is absent carries no information: `limitedBy` appears only when
something limited the answer, `changed` only when `false`, `truncated` only when true — an absent
`tests` means "not computed", not "no tests". A `null` is dropped from JSON entirely rather than
written as `"field":null`, so check for the key's absence, not its value.

`search_index`'s `shape` column (and the same column on `get_symbol`'s `members` rows) is the one
deliberate exception, and its legend says so: every letter is emitted at its real value, so an absent
letter means the count is zero or cannot apply to that kind of symbol — never "not computed".

The write path states the same distinction positively: `validate_patch` and `rename_symbol` return a
**`checks`** block on every call — which validation rungs ran and over what, analyzer findings by
severity, and an explicit `notAssessed` list. Report the scope it names; a clean rung is clean over
what `scope` says, nothing wider.

Every tool that records telemetry takes an optional **`taskId`** attributing the call to a caller you
name, so `get_retrieval_metrics` can read those calls back on their own. No tool takes a `sessionId`.

## Exploring — delegate the sweep when the symbol set is unknown

**Before writing or changing C#, when the set of symbols a task touches is not already known,
delegate the sweep to the `dotnet-explore` agent** rather than fanning out `search_index` /
`get_references` here. It spends the wide responses in its own context and hands back `symbolId`s,
use sites, and the blast radius; it is read-only and cannot start editing instead.

**Skip it** when the symbol is already known, or when the next step needs a `contentVersion` — the
agent relays none, so a narrow lookup you are about to patch from is cheaper done here. An unfamiliar
subsystem is what this is for; a two-call lookup onto a known target is not.

## Writing

`validate_patch` is the write path and the **only** writer to the development log; a pure rename is
`rename_symbol`, which derives every reference edit from the compiler's graph. An edit that bypasses
them is a change whose reasoning is gone when the conversation ends — `search_log` cannot recover it,
and the next session re-derives or silently contradicts it.

Procedure, arguments, and every failure mode: invoke the **`dotnet-change`** skill.

## Coding standards

Read the relevant ones before the first C# edit of a session, resolving each filename by the rule
above: `<pluginRoot>/standards/<name>`. These are the plugin's standards and the only copy — there is
no per-repo override. The **`dotnet-change`** skill walks this for you and carries the write-time
checklist with it.

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
`security.md` — every time, then loads only the rows the retrieved code triggers. So a "When" cell
must state an **observable property of the code** (it awaits, it is a hot path, it is a public
surface change), not a topic: a row that cannot be matched against retrieved source is one the
reviewer will skip or over-load. An untriggered aspect is reported not-assessed, never clean.


## Skills — when to invoke

Their descriptions are already loaded; these are the mandates a description cannot express.

- **Before the first C# edit of a session** → `dotnet-change`. It carries the write procedure, the
  pre-edit standards step, and the write-time checklist.
- **To open any file named in the `Read` column above** → `dotnet-code-query`.
- **Any review request** → `dotnet-review`. Never review C# inline: it partitions the target into
  disjoint scopes and gives each reviewer the standards location.

## Everything the MCP surface doesn't cover

Shell and plain file tools: `dotnet build` / `dotnet test` / `dotnet publish`, `git`, and reading or
editing non-C# files (Markdown, JSON, `.cmd`, `.csproj`, skill and agent definitions).
