# C# in this repo: the dotnet-toolkit router

This repo has the dotnet-toolkit plugin — a Roslyn-powered MCP server whose tools are the default
path for C#, not Grep, Glob, `find`, `ls`, `cat`, bare `Read`, or `Edit`/`Write`. Text search gives
**wrong answers** on C#, not merely slower ones: it cannot see interface, virtual, or delegate
dispatch, counts comment and string matches as real hits, under-reports silently when truncated, and
returns one fragment of a partial class with no signal the rest exists. On an exact, distinctive name
a `grep` does sometimes land the right answer cheaply — but its output looks identical either way, so
which question you are in is knowable only after the fact, which is why the route is settled per
session rather than per question.

This file is the single always-loaded rule, and it is a **router only**. It names no tools. Each
skill below carries its own tool set, when to reach for each one, the cheap-route table, and the
failure modes — loaded when you need it and not before.

## Invoke the skill first

| When you intend to | Invoke |
|---|---|
| **Read or navigate** `.cs` files — find a symbol, read source, trace callers, inspect a hierarchy, check project references, ask why code looks the way it does | `dotnet-read` |
| **Write, add, edit, modify, rename, or delete** `.cs` files | `dotnet-write` |
| **Survey an unfamiliar area** before deciding what to change, when the set of symbols the task touches is not already known | `dotnet-explore` |
| **Review** C#/.NET code, a PR, a diff, or a subsystem — or **audit whether a convention holds** across existing code ("are all DTOs records", "does everything public have XML docs") | `dotnet-review` |
| **Find what the system no longer needs** — stale or superseded code, backwards-compatibility layers, scattered implementations that want one abstraction, stale tests; or clean up after a substantial refactor | `dotnet-architecture` |

Rules for the table:

- **`dotnet-write` is a precondition, not a suggestion**, before the first `.cs` change of a session.
  It carries the write path, the pre-edit standards step, and the failure modes. An edit made without
  it is a change whose reasoning is gone when the conversation ends.
- **`dotnet-read` before the first `.cs` read of a session.** It is also what turns a vague question
  ("who calls this", "what changed", "what's in scope here") into the one right tool and the one
  right call shape.
- **Agents are launched by skills, never directly.** `dotnet-explore`, `dotnet-review` and
  `dotnet-architecture` own the briefing, the scoping, and the workspace-readiness check their agents
  depend on.
- **Reading is a prerequisite for writing, not a substitute.** A task that edits C# invokes
  `dotnet-write`; that skill routes back to `dotnet-read` for the fetches it needs.

## If you cannot read a response

Tool responses are **TOON** by default: the same field names JSON would carry, with far fewer
delimiters. That compression is a large part of why these tools cost less than the file reads they
replace, which is why it is the default and not an option. But it is a rarer format than JSON, so
reading it confidently is a real capability boundary — and a half-read response is worse than a
slower one read correctly, because a misread field becomes a wrong edit rather than a wasted call.

- **Never guess at a response you cannot parse.** If the structure is not unambiguous to you,
  `dotnet-read` names the one call that switches the session to compact JSON or plain JSON. It holds
  for the rest of the session, and taking it is a correct decision, not a fallback to apologise for.
- **If the shape is clear but a field's meaning is not**, the fix is that tool's manual, not another
  call with different arguments. `dotnet-read` names those paths too.
- **Re-issuing an identical call returns identical bytes.** If a response disappointed you, change
  the question or the tool — repeating it only spends the tokens twice.

## Everything the plugin doesn't cover

Shell and plain file tools, unchanged: `dotnet build` / `dotnet test` / `dotnet publish`, `git`, and
reading or editing non-C# files (Markdown, JSON, `.cmd`, `.csproj`, skill and agent definitions).

**`Edit` and `Write` are the write path for `.cs` files too** — string-anchored, on text you have
read. What the plugin adds around a write is locating the code and its blast radius first
(`dotnet-write` routes to the reads), a `rename_symbol` tool for the one edit a string replacement
cannot make safely, and a `PostToolUse` hook that re-indexes every file a tool writes, so the next
`search_index` or `get_references` already sees the edit. `dotnet build` is the gate after a burst of
edits; nothing compiles as you go.

`Read` and shell reads (`cat`/`grep`/`sed`/…) on a compiled `.cs` file are **not** blocked. A hook
adds an advisory note when a *whole* file is read raw, since a `get_symbol` and a ranged `Read` of its
`declarationSites` usually cost less and miss nothing; a ranged read draws no note, because it is the
intended way to fetch text once a symbol is located.
