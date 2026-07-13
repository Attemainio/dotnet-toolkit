---
name: dotnet-navigation
description: Use when exploring or orienting in a .NET/C# codebase - understanding repository layout, what a folder/project/ensemble contains, or where a feature lives. Prefer these MCP tools over ls/Glob/Read for .cs files.
---

# Navigating a .NET codebase without reading files

This repo has the dotnet-toolkit MCP server with an always-fresh index of every .cs
file. Orient with these tools instead of listing directories and reading files.

## Workflow: wide to narrow

1. **Repository overview** — `mcp__plugin_dotnet-toolkit_dotnet__project_tree`
   (optional `path`, `depth`, default 3). Every folder line shows `(N files, M types)`.
2. **One folder / ensemble** — `mcp__plugin_dotnet-toolkit_dotnet__list_folder`
   with the folder path. Shows each file with its top-level types and a one-line doc
   summary — this answers "what is in this ensemble" in a few lines.
3. **One file** — `mcp__plugin_dotnet-toolkit_dotnet__outline` with the .cs path.
   Full member outline (signatures + doc summaries), typically ~10–20% of the tokens
   of reading the file. Pass `include_private: true` to see non-public members.
4. **Jump to a symbol** — `mcp__plugin_dotnet-toolkit_dotnet__find_symbol` when you
   know a name fragment but not the location; results include `file:line`.

## Rules

- Only Read a .cs file when you are about to edit specific lines or need method
  bodies; the outline covers everything else (types, signatures, docs, line numbers).
- For non-C# files (csproj, json, md), the normal Read/Grep tools are fine.
- Output format legend (kind codes, tables) is documented in the dotnet-code-query
  skill; both skills share it.
