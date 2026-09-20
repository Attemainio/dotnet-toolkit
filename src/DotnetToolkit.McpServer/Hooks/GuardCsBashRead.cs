namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// <c>PreToolUse</c> guard on <c>Bash</c>: allows a shell command that reads a compiled <c>.cs</c>
/// file's raw bytes the way <c>Read</c> would (<c>cat</c>, <c>sed</c>, <c>head</c>, <c>grep</c>, …), but
/// surfaces the same advisory <see cref="GuardCsRead"/> carries rather than denying it.
/// </summary>
/// <remarks>
/// <see cref="GuardCsRead"/> only ever sees the <c>Read</c> tool by name — its matcher is <c>Read</c>,
/// so a shell command dumping the same file's content into the transcript is invisible to it. This guard
/// closes that gap by matching on <c>Bash</c> and inspecting what the command is about to do, reaching
/// the same allow-with-advice outcome <see cref="GuardCsRead"/> does, for the reasons stated there. The
/// membership question is identical to <see cref="GuardCsRead"/>'s, so both go through
/// <see cref="CsFileMembership"/> rather than answering it twice.
/// <para>
/// Commands outside the blocklist are never touched, so <c>git diff -- Foo.cs</c>, <c>git log Foo.cs</c>
/// and <c>find . -name '*.cs'</c> are all unaffected.
/// </para>
/// </remarks>
internal static class GuardCsBashRead
{
    /// <summary>Decides whether a <c>Bash</c> call gets an advisory note.</summary>
    /// <param name="payload">The parsed hook payload.</param>
    /// <param name="context">The repo root, the blocklist, and the docs to cite.</param>
    /// <returns>
    /// An allow outcome carrying advisory <c>permissionDecisionReason</c> text naming the first
    /// blocklisted command found reading a compiled <c>.cs</c> file; otherwise a plain
    /// <see cref="HookOutcome.Allow"/>.
    /// </returns>
    public static HookOutcome Evaluate(HookPayload payload, HookContext context, bool blocked = false)
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
            if (name is null)
            {
                continue;
            }

            // `find . -name '*.cs' -exec cat {} \;` reads every compiled file the walk matches while its
            // own first word is `find` -- a command the blocklist deliberately omits, since find alone
            // only lists names. The read belongs to the utility -exec names, so that is what is judged.
            if (BashCommandScanner.ExecutedUtility(segment) is { } executed
                && context.ReadBlocklist.Contains(executed.Utility))
            {
                if (ProjectUnder(executed.Roots, context, workingDirectory) is { } walked)
                {
                    return blocked
                        ? HookOutcome.Deny(BlockedTreeMessage(executed.Utility, walked.Target, walked.Project))
                        : HookOutcome.AllowAdvising(
                            TreeMessage(executed.Utility, walked.Target, walked.Project, context));
                }

                continue;
            }

            if (!context.ReadBlocklist.Contains(name))
            {
                continue;
            }

            // sed -n '12,40p', head -n 40, tail -n +12: a ranged read of one file is the intended way to
            // fetch a located symbol's text, exactly as a Read with offset/limit is, so it draws no note.
            if (BashCommandScanner.IsRangedRead(segment))
            {
                continue;
            }

            if (NamedCompiledFile(segment, context, workingDirectory) is { } named)
            {
                return blocked
                    ? HookOutcome.Deny(BlockedFileMessage(name, named.Argument, named.Project))
                    : HookOutcome.AllowAdvising(FileMessage(
                        name, named.Argument, named.Project, context,
                        ReadCostNote.For(named.Absolute, payload.SessionId)));
            }

            // A recursive or glob-scoped search names no single file, so the check above cannot see it --
            // and it reads every compiled file under the directory rather than one. Left unguarded, the
            // broader read was the one that got through: `grep -rn "x" --include=*.cs src/` carried its
            // only .cs token inside an option flag, which FindCsArgument discards as a flag.
            if (ScannedProject(segment, name, context, workingDirectory) is { } scanned)
            {
                return blocked
                    ? HookOutcome.Deny(BlockedTreeMessage(name, scanned.Target, scanned.Project))
                    : HookOutcome.AllowAdvising(TreeMessage(name, scanned.Target, scanned.Project, context));
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
    /// <returns>The argument as written, its absolute path and its owning project, or null when none applies.</returns>
    private static (string Argument, string Absolute, string Project)? NamedCompiledFile(string segment, HookContext context, string workingDirectory)
    {
        var argument = BashCommandScanner.FindReadTargetCsArgument(segment);
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
            ? (argument, absolute, project)
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
    private static (string Target, string Project)? ScannedProject(string segment, string name, HookContext context, string workingDirectory) =>
        ProjectUnder(
            BashCommandScanner.CsScanRoots(segment, recursesByDefault: name is "rg" or "ag"),
            context,
            workingDirectory);

    /// <summary>The first compiled <c>.cs</c> file reachable under any of a walk's roots.</summary>
    /// <param name="roots">The directories the walk covers, as the caller wrote them.</param>
    /// <param name="context">The repo root the guard is scoped to.</param>
    /// <param name="workingDirectory">The directory this segment runs in, after any earlier <c>cd</c>.</param>
    /// <returns>
    /// The root as written and the project compiling something under it, or null when none of the roots
    /// lies inside this repository or holds anything compiled.
    /// </returns>
    /// <remarks>
    /// Shared by the two ways a segment can name a tree instead of a file — its own recursion flags, and
    /// a <c>-exec</c> template's roots — so both reach the identical membership answer. Answering it twice
    /// is how one route came to advise and the other to stay silent on the same directory.
    /// </remarks>
    private static (string Target, string Project)? ProjectUnder(
        IReadOnlyList<string> roots,
        HookContext context,
        string workingDirectory)
    {
        foreach (var root in roots)
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
    /// <summary>The advisory note for a segment reading one named compiled file.</summary>
    /// <param name="name">The blocklisted command.</param>
    /// <param name="argument">The file as the caller wrote it.</param>
    /// <param name="project">The project compiling it.</param>
    /// <param name="context">The docs to cite.</param>
    /// <param name="cost">The measured cost of this read and the session's tally, or null when unmeasurable.</param>
    /// <returns>The message fed back to the agent.</returns>
    private static string FileMessage(string name, string argument, string project, HookContext context, string? cost) =>
        $"""
        {argument} is compiled by {project}, and `{name}` here reads it whole.{(cost is null ? "" : " " + cost)}
        Locating first usually costs less and misses nothing: search_index or get_symbol gives the
        symbol's declarationSites, and a ranged read of that span (`sed -n 'a,bp'`, or Read with
        offset/limit) fetches exactly the text wanted - a ranged read draws no note. This is advice, not a
        denial.

        Consider invoking the dotnet-read skill instead. It names the right tool for the question you were
        about to answer, the call shape that costs least, and how to read the response. If you cannot invoke
        a skill at all, start from {context.Doc("search_index")}.

        One case genuinely has no MCP equivalent: a search for arbitrary text - a string literal, or an API
        name this repo does not itself declare - rather than for a declared symbol. `{name}` is the right
        call for that.
        """;

    /// <summary>The advisory note for a segment searching a whole tree of compiled files.</summary>
    /// <param name="name">The blocklisted command.</param>
    /// <param name="target">The directory as the caller wrote it.</param>
    /// <param name="project">A project compiling something under it.</param>
    /// <param name="context">The docs to cite.</param>
    /// <returns>The message fed back to the agent.</returns>
    private static string TreeMessage(string name, string target, string project, HookContext context) =>
        $"""
        `{name}` would be searching {target}, reading every .cs file under it including files compiled by
        {project}. This repo's dotnet-toolkit MCP tools can answer a symbol search over the whole solution
        in one call, with no truncation risk and without dumping the matched files' source into the
        transcript - though not on every question, so this is advice, not a denial.

        Consider invoking the dotnet-read skill instead. It names the right tool for the question you were
        about to answer - including how to scope a search to one folder instead of walking it - and the
        call shape that costs least. If you cannot invoke a skill at all, start from
        {context.Doc("search_index")}.

        One case genuinely has no MCP equivalent: a search for arbitrary text - a string literal, or an API
        name this repo does not itself declare - rather than for a declared symbol. `{name}` is the right
        call for that.
        """;

    /// <summary>The denial for a segment reading one named compiled file, in forced-block mode.</summary>
    /// <param name="name">The blocklisted command.</param>
    /// <param name="argument">The file as the caller wrote it.</param>
    /// <param name="project">The project compiling it.</param>
    /// <returns>The message fed back to the agent.</returns>
    private static string BlockedFileMessage(string name, string argument, string project) =>
        $"""
        Blocked Bash command '{name}' reading {argument} whole: guard-cs-bash-read is running in forced-block
        mode (set_hook_guards(state: "block")), and {argument} is compiled by {project}. Locate the symbol
        with search_index or get_symbol, then read its declarationSites range (`sed -n 'a,bp'`, or Read
        with offset/limit) - a ranged read is never blocked.

        Do this instead: invoke the dotnet-read skill.

        Forced-block is not this plugin's shipped default - it exists for dotnet-performance's MCP probe, so
        a raw read attempted through it fails loudly instead of silently contaminating the measurement. Call
        set_hook_guards(state: "restore") once that run is done.
        """;

    /// <summary>The denial for a segment searching a whole tree of compiled files, in forced-block mode.</summary>
    /// <param name="name">The blocklisted command.</param>
    /// <param name="target">The directory as the caller wrote it.</param>
    /// <param name="project">A project compiling something under it.</param>
    /// <returns>The message fed back to the agent.</returns>
    private static string BlockedTreeMessage(string name, string target, string project) =>
        $"""
        Blocked Bash command '{name}' searching {target}: guard-cs-bash-read is running in forced-block mode
        (set_hook_guards(state: "block")), and it reads every .cs file under {target}, including files
        compiled by {project}. This repo's dotnet-toolkit MCP tools answer a symbol search over the whole
        solution in one call, with no truncation risk and without dumping the matched files' source into
        the transcript.

        Do this instead: invoke the dotnet-read skill.

        Forced-block is not this plugin's shipped default - it exists for dotnet-performance's MCP probe, so
        a raw read attempted through it fails loudly instead of silently contaminating the measurement. Call
        set_hook_guards(state: "restore") once that run is done.
        """;

}
