namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// <c>PreToolUse</c> guard on <c>Bash</c>: blocks a shell command that reads a compiled <c>.cs</c>
/// file's raw bytes the way <c>Read</c> would (<c>cat</c>, <c>sed</c>, <c>head</c>, <c>grep</c>, …).
/// </summary>
/// <remarks>
/// <see cref="GuardCsRead"/> only ever sees the <c>Read</c> tool by name — its matcher is <c>Read</c>,
/// so a shell command dumping the same file's content into the transcript is invisible to it. That is
/// not a sanctioned escape hatch; it is a gap in which tool name the enforcement watches, and this
/// guard closes it by matching on <c>Bash</c> and inspecting what the command is about to do. The
/// membership question is identical to <see cref="GuardCsRead"/>'s, so both go through
/// <see cref="CsFileMembership"/> rather than answering it twice.
/// <para>
/// Commands outside the blocklist are never touched, so <c>git diff -- Foo.cs</c>, <c>git log Foo.cs</c>
/// and <c>find . -name '*.cs'</c> are all unaffected.
/// </para>
/// </remarks>
internal static class GuardCsBashRead
{
    /// <summary>Decides whether a <c>Bash</c> call may proceed.</summary>
    /// <param name="payload">The parsed hook payload.</param>
    /// <param name="context">The repo root, the blocklist, and the docs to cite.</param>
    /// <returns>
    /// A denial naming the first blocklisted command found reading a compiled <c>.cs</c> file;
    /// otherwise <see cref="HookOutcome.Allow"/>.
    /// </returns>
    public static HookOutcome Evaluate(HookPayload payload, HookContext context)
    {
        var command = payload.Command;
        if (string.IsNullOrWhiteSpace(command))
        {
            return HookOutcome.Allow;
        }

        foreach (var segment in BashCommandScanner.Segments(command))
        {
            var name = BashCommandScanner.CommandName(segment);
            if (name is null || !context.ReadBlocklist.Contains(name))
            {
                continue;
            }

            var argument = BashCommandScanner.FindCsArgument(segment);
            if (argument is null)
            {
                continue;
            }

            var absolute = Path.GetFullPath(Path.Combine(context.WorkingDirectory, argument));
            if (!File.Exists(absolute))
            {
                continue;
            }

            if (!CsFileMembership.TryResolveOwningProject(absolute, context.Root, out var project))
            {
                continue;
            }

            return HookOutcome.Deny($"""
                Blocked Bash command '{name}' reading {argument}: it is compiled by {project}, so
                search_index/get_symbol answer this more cheaply and completely than raw shell text tools - no
                truncation risk, and no irrelevant methods pulled in alongside the one you want.

                This is the same rule Read is blocked under - running the same read through Bash instead of the
                Read tool is not a sanctioned way around it.

                Do this instead:
                  - Don't know the exact symbol name: search_index(query: "term1 term2 ...") - one call, many terms.
                  - Know the type/member name: get_symbol(symbol: "...").
                  - Only need part of a long member (what sed -n '120,160p' would have done):
                    get_symbol(symbol: "...", include: "source:code@120-160").
                  - Looking for arbitrary text (a string literal, an API name not declared in this repo) rather than a
                    declared symbol: search_index only indexes declared symbols, so a genuine text search has no MCP
                    equivalent yet - say so and ask the user to allow the Bash command explicitly.

                For arguments and worked examples, read the one file for the tool you are about to call:
                  {context.Doc("search_index")}
                  {context.Doc("get_symbol")}
                {context.Doc("_index")} routes any other question to its tool.

                If this genuinely needs raw shell access (the workspace failed to load, or the file's exact
                formatting/byte layout is itself what you need to see), say so and ask the user to allow it explicitly
                rather than retrying the same command.
                """);
        }

        return HookOutcome.Allow;
    }
}
