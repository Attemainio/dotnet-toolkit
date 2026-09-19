---
name: dotnet-write
description: Use when writing, adding, editing, modifying, refactoring, renaming or deleting C#/.NET code — changing a method or type, changing a signature, adding a class or a file, fixing a compile error or an analyzer warning, or applying review findings. Carries the write path for .cs files (Edit/Write, string-anchored; rename_symbol for a rename), the standards step that runs before the first edit of a session, how the index follows your edits, and what to check before and after a change.
---

# Changing C# with dotnet-toolkit

**`Edit` and `Write` are the write path for `.cs` files.** An edit is anchored on the exact text you
already read, so there is no line arithmetic to get wrong. The plugin's part of a write is everything
around it: locating the code and its blast radius before, keeping the index in step during, and
`rename_symbol` for the one edit a string replacement cannot make safely.

Nothing compiles for you as you edit. **`dotnet build` is the gate**, run after a burst of related
edits rather than after each one — it reports every dependent break in every project, uncapped, and
`TreatWarningsAsErrors` repos turn analyzer findings into failures there too.

**Tool names carry a host-dependent prefix.** In Claude Code they are
`mcp__plugin_dotnet-toolkit_dotnet__<tool>`, because the plugin qualifies the server it ships. Codex
registers the same server under the bare name `dotnet` and namespaces by that instead, so the prefix
differs there. Every fully-qualified name below is written in the Claude Code form.

**Resolve the prefix once per session rather than assuming it.** Run
`ToolSearch("select:mcp__plugin_dotnet-toolkit_dotnet__workspace_status")`; if that returns nothing,
search for the bare tool name (`ToolSearch("workspace_status")`) and read the prefix off whatever
comes back, then apply that same prefix to every name in this skill. One probe settles it for the
whole session.

## Step 0 — `workspace_status` before any edit

**Call it first, every session, and again before editing after any git operation.** It is free and
takes no arguments. Three things depend on it:

1. **A change planned from a `degraded` or `stale` workspace is planned from the wrong content.** If
   `workspace` reports failed projects, fix the build and `reload_workspace` first — a
   `get_references` answer from a half-loaded compilation can miss call sites entirely, which is a
   wrong blast radius, not a thin one.
2. **`pluginRoot`**, which is how you reach the coding standards below and any tool manual. Join it
   yourself: `<pluginRoot>/standards/index.md`, `<pluginRoot>/docs/tools/rename_symbol.md`. **Never
   write `${CLAUDE_PLUGIN_ROOT}` into a path** — it is not expanded inside a rule or an agent
   definition, so it stays literal and the read fails.
3. **Confirmation the index is ready**, so `get_references` can tell you the blast radius rather than
   returning `workspace_loading`. The same line reports when the symbol store last refreshed and
   whether any files are still queued — see "How the index follows your edits" below.

## Step 1 — read the standards, once per session

`<pluginRoot>/standards/index.md` is the routing table: the baseline set every C# change needs, plus a
row per aspect keyed to an observable property of the code you are about to write. Read the baseline
set plus every row that matches your change, and skim `antipatterns.md` once per session so a rule
saying "avoid the X antipattern" resolves to something you have actually read.

They are plugin-owned, never copied into a repo, and there is no per-repo override. Nothing
auto-loads them — this step is what loads them. Hold them; don't re-read per edit.

**Writing to the standard beats fixing to it.** `dotnet-code-review` checks against these same files
afterward.

### The write-time checklist

The handful of items most expensive to catch late — credential-shaped literals, concatenated SQL,
unmarked endpoints, in-memory database substitutes in tests that assert relational behavior — arrive
from the **`hint-cs-write-checklist` hook** on the session's first `Edit`/`Write` of a compiled `.cs`
file. They are not repeated here: the hook reaches a caller who never loaded this skill, which is
exactly the case a copy here would miss, and two copies always drift.

## Step 2 — locate, then read the exact text you will replace

Reading is part of the write path, not a separate task. **`dotnet-read`** owns the tools: it is what
turns "the method that handles X" into a `symbolId`, a file and a line range. From there:

- **`get_symbol`** gives `declarationSites` — `file`, `startLine` (the line a leading `///` comment
  starts on, when there is one), `endLine` (the closing brace) and `signatureLine` — for every part of
  a partial. Read the text with `Read(file, offset: startLine, limit: endLine - startLine + 1)`.
- **Anchor the `Edit` on text from that read**, generous enough to be unique in the file. An edit
  that fails to match means the file moved since you read it: re-read, never guess.
- **Before changing a signature, a return type or a public contract**, `get_references` (callers,
  implementations, overrides) and, for anything virtual or on an interface, `get_call_hierarchy`.
  That is the list of files the same change has to reach, and it is the answer grep gets wrong.

## `rename_symbol` — the one edit `Edit` cannot make

A rename is not a search-and-replace: it has to reach interface, virtual and delegate dispatch sites
and leave alone unrelated text that shares the name. `rename_symbol` derives every site from Roslyn's
own reference graph, so **a pure rename goes here, never through a pile of `Edit` calls.**

```
ToolSearch("select:mcp__plugin_dotnet-toolkit_dotnet__rename_symbol")
```

- `apply: false` (the default) is a dry run: every file that would change and how many occurrences
  in each, nothing written. **Worth it on a widely-referenced symbol**, because the blast radius is
  the thing you cannot predict.
- `apply: true` writes the files and refreshes the index in the same call, so the new name and its
  `newSymbolId` are searchable at once.
- The result is **not compiled**. `nameAlreadyExists` warns when the new name is already declared in
  the same container — a same-signature clash will not build; a different-signature one silently
  becomes an overload set. Run `dotnet build` afterwards, as after any edit.
- It does not rename the containing **file**; when that is wanted the response says so under
  `fileRenameHint`, and the move is a `git mv`.

Rename *plus* other edits: rename first, then `Edit` the rest against a fresh read.

Manual: `<pluginRoot>/docs/tools/rename_symbol.md`

## How the index follows your edits

Change detection is an mtime poll (inotify does not fire on WSL `/mnt/*`), and a `PostToolUse` hook
fires after every `Edit`, `Write`, `NotebookEdit`, `apply_patch` and `Bash` call, sending the files
it wrote to the running server. The server re-stats them, swaps the new text into the live Roslyn
solution, and re-indexes just those documents — so `search_index`, the `sym_` ids, `get_references`
and the hierarchies see your edit before your next call. **The hook is silent on success.** You will
see a note only when there is something to wait for:

- **A new or deleted `.cs` file starts a workspace reload**, because only a reload can add a
  document to the compilation. Call `workspace_status` and wait for `loaded` before `get_symbol`,
  `search_index` or `get_references` on that file.
- **A refresh is still running** ("queued"): `workspace_status` says how many files are pending.
- **The server was unreachable for a new file**: call `reload_workspace(scope: "all")` yourself.

An edit the hook does not recognise — an unusual shell shape, an external editor — converges on the
next sweep within a couple of seconds; `limitedBy: "stale"` on a read means that sweep has not yet
run, and the next call after it will be current.

## The cheap-route table

| Anti-pattern (route taken) | Cheap route |
|---|---|
| Counting lines to place an edit | Anchor `Edit` on the text itself; a line number is a `Read` offset, never an edit target |
| `get_symbol` with `source` just to find the text to change | `get_symbol` for `declarationSites`, then one `Read` of that range — the text you read is the anchor |
| Search-and-replace over the tree to rename something | `rename_symbol` |
| One `Edit` per call site of a renamed symbol | `rename_symbol` — every reference derived from the compiler's graph |
| `grep -n` for callers before a signature change | `get_references` (`callers`), then `get_call_hierarchy` for the virtual/interface fan-out grep cannot see |
| `dotnet build` after every single edit | Once per burst of related edits; the build reports every break at once |
| `reload_workspace` after every edit | Nothing — the refresh hook and the sweep already did it; reload only after adding/deleting files when the hook said the server was unreachable, or after a large git operation |
| Editing a new `.cs` file straight after `Write` | Wait for `workspace_status` to report `loaded` — the reload the hook started has to finish before the file is in the compilation |
| A `pragma` suppression to get past an analyzer rule | Fix the code, or raise lowering that rule's `.editorconfig` severity **with the user** — never suppress silently |

## When it fails

- **`Edit` reports no match.** The file moved since you read it (a previous edit of yours, a rename,
  a pull). Re-read the range from a fresh `get_symbol`; never retry with a guessed anchor.
- **`dotnet build` fails in files you did not touch.** That is the dependent break `get_references`
  would have listed: fix every site the build names, in one pass, then build again.
- **A read answers `limitedBy: "stale"` or `"degraded"`.** Stale clears on the next sweep; degraded
  means a project failed to load — `workspace_status` names it, and nothing planned from that
  compilation should be trusted until the build is fixed and `reload_workspace` has run.
- **`rename_symbol` answers `stale_workspace`.** The workspace's copy of a file the rename touches is
  behind disk, usually mid-reload: `workspace_status`, wait for `loaded`, retry.
- **An analyzer rule blocks the build.** Fix the code, **or** lower that rule's severity in
  `.editorconfig` if it is wrong for this repo. The second is a real answer, not a workaround — but it
  is the *user's* call, so raise it rather than editing `.editorconfig` unasked.
