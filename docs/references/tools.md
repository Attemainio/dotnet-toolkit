# dotnet-toolkit tool reference

**This file is an index, not the reference.** The per-tool catalog was split into
`docs/tools/` so a caller reads only the tool it is about to use instead of ~19k tokens of
catalog. Read them in this order:

1. **`docs/tools/_index.md`** — the router: which tool answers which question, the common
   call chains, workspace readiness, and how to read responses. Start here.
2. **`docs/tools/<tool>.md`** — one file per tool: when to reach for it, its arguments, a real
   call against this plugin's own repo with its real response, and what to call next.

| Tool | File |
|---|---|
| `get_symbol` | `docs/tools/get_symbol.md` |
| `get_references` | `docs/tools/get_references.md` |
| `search_index` | `docs/tools/search_index.md` |
| `get_scope` | `docs/tools/get_scope.md` |
| `get_call_slice` | `docs/tools/get_call_slice.md` |
| `get_call_hierarchy` | `docs/tools/get_call_hierarchy.md` |
| `get_type_hierarchy` | `docs/tools/get_type_hierarchy.md` |
| `get_project_graph` | `docs/tools/get_project_graph.md` |
| `detect_circular_dependencies` | `docs/tools/detect_circular_dependencies.md` |
| `get_semantic_diff` | `docs/tools/get_semantic_diff.md` |
| `search_log` | `docs/tools/search_log.md` |
| `validate_patch` | `docs/tools/validate_patch.md` |
| `rename_symbol` | `docs/tools/rename_symbol.md` |
| `get_retrieval_metrics` | `docs/tools/get_retrieval_metrics.md` |
| `ping`, `set_output_format`, `workspace_status`, `reload_workspace` | `docs/tools/server.md` |

## Conventions that hold across every tool

Tool names are prefixed `mcp__plugin_dotnet-toolkit_dotnet__` when called.

No tool takes a `sessionId` — every call in a server process shares one ambient session id
automatically. Every tool that records telemetry does take an optional **`taskId`**, omitted from
the per-tool argument tables because it means the same thing everywhere: it attributes the call to
a caller you name, so `get_retrieval_metrics` can read those calls back on their own. See
`docs/tools/get_retrieval_metrics.md`.

Responses are deliberately terse: a field that is absent carries no information and costs no
tokens. This applies to plain objects too, not just top-level envelopes — a `null` field is dropped
from JSON entirely rather than written as `"field":null`, so check for the key's absence, not its
value. One TOON-only exception: when most rows of an array carry a field and a few don't, the gaps are
rendered as **empty cells** so the array keeps its tabular form — a single ragged row otherwise drops
every row into the far more expensive per-item shape. An empty cell there means the same "absent" the
missing key means, and the JSON/compact formats still omit it outright.

Responses are **TOON** (Token-Oriented Object Notation) by default, not JSON text.
`set_output_format(format: "compact")` gives minified JSON, `format: "json"` pretty-printed; either
persists for the session. `defaultFormat` in `.claude/dotnet-toolkit/config.json` sets what a fresh
server starts with. Field names and nesting are identical in all three formats — only the wire
encoding changes (see `search_log(query: "contract")` for the 3.10 rationale). Every multi-item
response is a plain array of objects in every format; there is no separate columns/rows table shape
to learn.

`limitedBy` and workspace readiness are documented once, in `docs/tools/_index.md`.

## Maintaining these files

When a tool's name, arguments, defaults or response shape changes, update its `docs/tools/<tool>.md`
**and** the router table in `docs/tools/_index.md` if the question it answers changed. The
`dotnet-toolkit-consistency` skill checks both against `Tools/*.cs`.
