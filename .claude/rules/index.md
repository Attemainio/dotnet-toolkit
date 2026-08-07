# C# in this repo: the dotnet-toolkit router

This repo has the dotnet-toolkit plugin — a Roslyn-powered MCP server whose tools are the default
path for C#, not Grep, Glob, `find`, `ls`, `cat`, bare `Read`, or `Edit`/`Write`. Text search gives
**wrong answers** on C#, not merely slower ones: it cannot see interface, virtual, or delegate
dispatch, counts comment and string matches as real hits, under-reports silently when truncated, and
returns one fragment of a partial class with no signal the rest exists.

This file is the single always-loaded rule, and it is a **router only**. It names no tools. Each
skill below carries its own tool set, when to reach for each one, the cheap-route table, and the
failure modes — loaded when you need it and not before.

## Invoke the skill first

| When you intend to | Invoke |
|---|---|
| **Read or navigate** `.cs` files — find a symbol, read source, trace callers, inspect a hierarchy, check project references, ask why code looks the way it does | `dotnet-read` |
| **Write, add, edit, modify, rename, or delete** `.cs` files | `dotnet-write` |
| **Survey an unfamiliar area** before deciding what to change, when the set of symbols the task touches is not already known | `dotnet-explore` |
| **Review** C#/.NET code, a PR, a diff, or a subsystem | `dotnet-review` |

Rules for the table:

- **`dotnet-write` is a precondition, not a suggestion**, before the first `.cs` change of a session.
  It carries the write path, the pre-edit standards step, and the failure modes. An edit made without
  it is a change whose reasoning is gone when the conversation ends.
- **`dotnet-read` before the first `.cs` read of a session.** It is also what turns a vague question
  ("who calls this", "what changed", "what's in scope here") into the one right tool and the one
  right call shape.
- **Agents are launched by skills, never directly.** `dotnet-explore` and `dotnet-review` own the
  briefing, the scoping, and the workspace-readiness check their agents depend on.
- **Reading is a prerequisite for writing, not a substitute.** A task that edits C# invokes
  `dotnet-write`; that skill routes back to `dotnet-read` for the fetches it needs.

## Everything the plugin doesn't cover

Shell and plain file tools, unchanged: `dotnet build` / `dotnet test` / `dotnet publish`, `git`, and
reading or editing non-C# files (Markdown, JSON, `.cmd`, `.csproj`, skill and agent definitions).
`PreToolUse` hooks block `Read`, `Edit`/`Write` and shell reads (`cat`/`grep`/`sed`/…) on a compiled
`.cs` file, and point you at the skill that covers what you were trying to do.
