# C# in this repo: the dotnet-toolkit tool protocol

This repo has the dotnet-toolkit plugin — a Roslyn-powered MCP server. Its tools are the default path
for C#, not Grep, Glob, `find`, `ls`, `cat`, bare `Read`, or `Edit`/`Write`.

There is deliberately **no `paths:` frontmatter** on this file: that is what makes it always-loaded.
The coding standards beside it are path-scoped and therefore read on demand — `csharp-standards.md`
is their master index, and is always-loaded alongside this file.

Grep and Read give **wrong answers** on C#, not merely slower ones: text search cannot see interface,
virtual, or delegate dispatch, counts comment and string matches as real hits, under-reports silently
when output is truncated, and returns one fragment of a partial class with no signal the rest exists.

| Instead of | Use |
| --- | --- |
| `grep`/Grep for a type or member name | `search_index` (all terms in ONE call — OR-ed and ranked; `limit` is global, so read `termsWithNoHits`) |
| `Read` on a `.cs` file | `get_symbol` (whole symbol across partials; `include` picks the fields) |
| `grep` for callers or implementors | `get_references` (Roslyn semantic model) |
| `find`/`ls`/Glob to map a subsystem | `get_scope` |
| Tracing a call chain by hand | `get_call_slice` |
| Who eventually calls/is called by a symbol, several levels deep | `get_call_hierarchy` |
| A type's full base chain, interfaces, and derived/implementing types | `get_type_hierarchy` |
| Opening every `.csproj` to trace project references by hand | `get_project_graph` |
| Manually tracing project references looking for a cycle | `detect_circular_dependencies` |
| `git diff` to judge a change | `get_semantic_diff` |
| Guessing why code looks the way it does | `search_log` |
| Wondering whether the index/workspace is warm | `workspace_status`, then `reload_workspace` if stale |
| `Edit`/`Write` then `dotnet build` | `validate_patch` |
| Search-and-replace, or one patch per call site, to rename something | `rename_symbol` |

`PreToolUse` hooks enforce all three sides: `Read` on a compiled `.cs` file, a `Bash` command reading
the same bytes (`cat`/`grep`/`sed`/etc.), and `Edit`/`Write` on an existing one are all blocked —
reaching for any of them costs a round trip and returns nothing. The hooks travel with the plugin;
nothing repo-local to maintain.

## Exploring — delegate the sweep when the symbol set is unknown

**Before writing or changing C#, when the set of symbols a task touches is not already known,
delegate the sweep to the `dotnet-explore` agent** rather than fanning out `search_index` /
`get_references` in this context. It spends the wide responses in its own context and hands back
`symbolId`s, use sites, and the blast radius; it is read-only and cannot start editing instead.

**Skip it** when the symbol is already known, or when the next step needs `contentVersion`s — the
agent deliberately relays none, so a narrow lookup you are about to patch from is cheaper done here.
An unfamiliar subsystem is what this is for; a two-call lookup onto a known target is not.

## Writing

`validate_patch` is the write path and the **only** writer to the development log. An edit that
bypasses it is a change whose reasoning is gone when the conversation ends — `search_log` cannot
recover it, and the next session re-derives or silently contradicts it.

The call: `get_symbol` for `contentVersion` + the `declarationSites` span, then `validate_patch` with
`baseVersions`, line-span `edits`, `applyOnSuccess: true`, and an `intent` in user terms. On failure,
amend the returned `draftId` rather than rebuilding the patch.

- **A body edit needs a body-carrying `contentVersion`** — the default `get_symbol` leases the
  declaration only, and a body patch against it is rejected as `unleased_body`. Use `include: "all"`,
  which is what you wanted anyway when editing the text.
- **"Too large or interleaved to decompose" is not a reason to use `Edit`.** Split it into more
  `validate_patch` calls, one per touched symbol, sharing one `intent`. Only new-file creation is
  outside this: `Write` the file, then change it through `validate_patch`.
- **A pure rename is `rename_symbol`**, not a set of patches — it derives every reference edit from
  the compiler's graph, including the cross-project and interface/virtual/delegate dispatch a
  hand-written patch set misses, and writes the same log entry.
- **`.editorconfig` decides what blocks** — validation grades exactly as `dotnet build` does. Never
  suppress with a pragma or edit `.editorconfig` to get past a rule; raise it and let the user decide.
- **A passing result states its own scope.** Report what the response's `checks` and `notAssessed`
  actually say; never turn a clean rung into a broader claim than the scope it names.

Procedure, arguments, and every failure mode: `${CLAUDE_PLUGIN_ROOT}/docs/tools/validate_patch.md`,
`${CLAUDE_PLUGIN_ROOT}/docs/tools/rename_symbol.md`, and the `dotnet-change` skill — all read on
demand, so none costs anything until it is needed.

## Coding standards

Read the relevant ones before the first C# edit of a session. The index, the per-file trigger
conditions, and the write-time checklist all live in `csharp-standards.md` beside this file — it is
always-loaded too, so there is no second copy of that list here to drift out of step.

## Everything the MCP surface doesn't cover

Shell and plain file tools: `dotnet build` / `dotnet test` / `dotnet publish`, `git`, and reading or
editing non-C# files (Markdown, JSON, `.cmd`, `.csproj`, skill and agent definitions).

Which tool answers which question: `${CLAUDE_PLUGIN_ROOT}/docs/tools/_index.md` (the router — start
here). How to call one: `${CLAUDE_PLUGIN_ROOT}/docs/tools/<tool>.md`. Read the single tool you need,
not the directory.
