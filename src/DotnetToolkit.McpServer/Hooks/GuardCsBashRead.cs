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

        var workingDirectory = context.WorkingDirectory;

        foreach (var segment in BashCommandScanner.Segments(command))
        {
            // A cd earlier in the same command changes what a later relative path means. Resolved against
            // the hook's own directory instead, `cd ../other-repo && grep -rn x .` was denied as a read of
            // THIS repo, citing a csproj from a repository the command had already left.
            if (BashCommandScanner.CdTarget(segment) is { } moved)
            {
                workingDirectory = Path.GetFullPath(Path.Combine(workingDirectory, moved));
                continue;
            }

            var name = BashCommandScanner.CommandName(segment);
            if (name is null || !context.ReadBlocklist.Contains(name))
            {
                continue;
            }

            if (NamedCompiledFile(segment, context, workingDirectory) is { } named)
            {
                return HookOutcome.Deny(FileMessage(name, named.Argument, named.Project, context));
            }

            // A recursive or glob-scoped search names no single file, so the check above cannot see it --
            // and it reads every compiled file under the directory rather than one. Left unguarded, the
            // broader read was the one that got through: `grep -rn "x" --include=*.cs src/` carried its
            // only .cs token inside an option flag, which FindCsArgument discards as a flag.
            if (ScannedProject(segment, name, context, workingDirectory) is { } scanned)
            {
                return HookOutcome.Deny(TreeMessage(name, scanned.Target, scanned.Project, context));
            }

        }

        return HookOutcome.Allow;
    }

    /// <summary>Enumeration that survives an unreadable subdirectory instead of throwing mid-walk.</summary>
    private static readonly EnumerationOptions CsScan = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    /// <summary>The compiled <c>.cs</c> file a segment names outright, if it names one.</summary>
    /// <param name="segment">One segment from <see cref="BashCommandScanner.Segments"/>.</param>
    /// <param name="context">The repo root the guard is scoped to.</param>
    /// <param name="workingDirectory">The directory this segment runs in, after any earlier <c>cd</c>.</param>
    /// <returns>The argument as written and its owning project, or null when neither applies.</returns>
    private static (string Argument, string Project)? NamedCompiledFile(string segment, HookContext context, string workingDirectory)
    {
        var argument = BashCommandScanner.FindCsArgument(segment);
        if (argument is null)
        {
            return null;
        }

        var absolute = Path.GetFullPath(Path.Combine(workingDirectory, argument));
        if (!File.Exists(absolute) || !IsUnderRoot(absolute, context.Root))
        {
            return null;
        }

        return CsFileMembership.TryResolveOwningProject(absolute, context.Root, out var project)
            ? (argument, project)
            : null;
    }

    /// <summary>Whether a resolved path lies inside the repo this guard speaks for.</summary>
    /// <param name="absolute">An already-resolved absolute path.</param>
    /// <param name="root">The repo root from <see cref="HookContext"/>.</param>
    /// <returns>True when the path is the root or sits under it.</returns>
    /// <remarks>
    /// The guard's whole justification is that THIS repo's MCP tools answer the read better, which says
    /// nothing about a sibling checkout the server is not pointed at. Checking before enumerating also keeps
    /// the hook from walking an unrelated tree on every command.
    /// </remarks>
    private static bool IsUnderRoot(string absolute, string root)
    {
        var bounded = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return string.Equals(absolute, root, StringComparison.OrdinalIgnoreCase)
            || absolute.StartsWith(bounded, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The first compiled <c>.cs</c> file a recursive or glob-scoped segment would reach.</summary>
    /// <param name="segment">One segment from <see cref="BashCommandScanner.Segments"/>.</param>
    /// <param name="name">The segment's command name, which decides whether it recurses by default.</param>
    /// <param name="context">The repo root the guard is scoped to.</param>
    /// <param name="workingDirectory">The directory this segment runs in, after any earlier <c>cd</c>.</param>
    /// <returns>
    /// The directory as written and the project compiling something under it, or null when the segment
    /// scans no tree or the trees it scans hold nothing compiled — which is what keeps a search over
    /// docs/ or a non-project folder unaffected.
    /// </returns>
    private static (string Target, string Project)? ScannedProject(string segment, string name, HookContext context, string workingDirectory)
    {
        foreach (var root in BashCommandScanner.CsScanRoots(segment, recursesByDefault: name is "rg" or "ag"))
        {
            var absolute = Path.GetFullPath(Path.Combine(workingDirectory, root));
            if (!Directory.Exists(absolute) || !IsUnderRoot(absolute, context.Root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(absolute, "*.cs", CsScan))
            {
                if (CsFileMembership.TryResolveOwningProject(file, context.Root, out var project))
                {
                    return (root, project);
                }
            }
        }

        return null;
    }

    /// <summary>The denial for a segment reading one named compiled file.</summary>
    /// <param name="name">The blocklisted command.</param>
    /// <param name="argument">The file as the caller wrote it.</param>
    /// <param name="project">The project compiling it.</param>
    /// <param name="context">The docs to cite.</param>
    /// <returns>The message fed back to the agent.</returns>
    private static string FileMessage(string name, string argument, string project, HookContext context) =>
        $"""
        Blocked Bash command '{name}' reading {argument}: it is compiled by {project}, so this repo's
        dotnet-toolkit MCP tools answer it more cheaply and completely than raw shell text tools - no
        truncation risk, and no irrelevant methods pulled in alongside the one you want.

        This is the same rule Read is blocked under - running the same read through Bash instead of the
        Read tool is not a sanctioned way around it.

        Do this instead: invoke the dotnet-read skill.

        It names the right tool for the question you were about to answer, the call shape that costs least,
        and how to read the response. None of that is repeated in this message on purpose: a copy here would
        drift from the skill. If you cannot invoke a skill at all, start from {context.Doc("search_index")}.

        One case genuinely has no MCP equivalent: a search for arbitrary text - a string literal, or an API
        name this repo does not itself declare - rather than for a declared symbol. Say so and ask the user to
        allow the Bash command explicitly.

        If this genuinely needs raw shell access (the workspace failed to load, or the file's exact
        formatting/byte layout is itself what you need to see), say so and ask the user to allow it explicitly
        rather than retrying the same command.
        """;

    /// <summary>The denial for a segment searching a whole tree of compiled files.</summary>
    /// <param name="name">The blocklisted command.</param>
    /// <param name="target">The directory as the caller wrote it.</param>
    /// <param name="project">A project compiling something under it.</param>
    /// <param name="context">The docs to cite.</param>
    /// <returns>The message fed back to the agent.</returns>
    private static string TreeMessage(string name, string target, string project, HookContext context) =>
        $"""
        Blocked Bash command '{name}' searching {target}: it reads every .cs file under it, including files
        compiled by {project}. This repo's dotnet-toolkit MCP tools answer a symbol search over the whole
        solution in one call, with no truncation risk and without dumping the matched files' source into the
        transcript.

        This is the same rule Read and a single-file grep are blocked under. Searching the tree rather than
        naming a file reads MORE, not less, so it is not a sanctioned way around it.

        Do this instead: invoke the dotnet-read skill.

        It names the right tool for the question you were about to answer - including how to scope a search to
        one folder instead of walking it - and the call shape that costs least. None of that is repeated in
        this message on purpose: a copy here would drift from the skill. If you cannot invoke a skill at all,
        start from {context.Doc("search_index")}.

        One case genuinely has no MCP equivalent: a search for arbitrary text - a string literal, or an API
        name this repo does not itself declare - rather than for a declared symbol. Say so and ask the user to
        allow the Bash command explicitly.

        If this genuinely needs raw shell access, say so and ask the user to allow it explicitly rather than
        retrying the same command.
        """;

}
