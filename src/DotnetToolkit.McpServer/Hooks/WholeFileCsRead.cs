namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// Recognises the one call shape the read guards are about: a compiled <c>.cs</c> file put into the
/// transcript whole, by <c>Read</c> without a range or by a shell utility whose output is the file.
/// </summary>
/// <remarks>
/// Shared by the two read guards (which advise on it) and the tool-call meter (which counts it), so
/// "how many whole-file reads did this agent make" is measured by the same predicate that decided
/// whether to advise. The benchmark used to prove the MCP probe stayed off raw reads by forcing the
/// guards to deny — a guard-state switch the harness's auto mode classifies as self-modification and
/// refuses. A count in the meter answers the same question after the fact, needs no permission, and
/// works for every probe at once.
/// </remarks>
internal static class WholeFileCsRead
{
    /// <summary>Utilities whose output is the file itself, so a read of one is sized by the file.</summary>
    public static readonly IReadOnlySet<string> Dumpers = new HashSet<string>(
        ["cat", "tac", "nl", "bat", "less", "more", "xxd", "od", "hexdump", "base64", "strings", "dd"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this call reads a compiled <c>.cs</c> file whole.</summary>
    /// <param name="payload">The hook payload of a <c>Read</c> or <c>Bash</c> call; anything else is false.</param>
    /// <param name="context">The repo root the membership check is scoped to.</param>
    /// <returns>True for an unranged <c>Read</c>, or a dumping utility naming the file, on a compiled file.</returns>
    public static bool Matches(HookPayload payload, HookContext context)
    {
        switch (payload.ToolName)
        {
            case "Read":
                {
                    var file = payload.FilePath;
                    if (string.IsNullOrEmpty(file) || !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || GuardCsRead.IsRanged(payload))
                    {
                        return false;
                    }
                    var absolute = Path.GetFullPath(Path.Combine(context.WorkingDirectory, file));
                    return File.Exists(absolute) && CsFileMembership.TryResolveOwningProject(absolute, context.Root, out _);
                }
            case "Bash":
                {
                    if (payload.Command is not { Length: > 0 } command)
                    {
                        return false;
                    }
                    var workingDirectory = context.WorkingDirectory;
                    foreach (var segment in BashCommandScanner.Segments(command))
                    {
                        if (BashCommandScanner.CdTarget(segment) is { } moved)
                        {
                            workingDirectory = Path.GetFullPath(Path.Combine(workingDirectory, moved));
                            continue;
                        }
                        if (BashCommandScanner.CommandName(segment) is not { } name || !Dumpers.Contains(name))
                        {
                            continue;
                        }
                        if (BashCommandScanner.IsRangedRead(segment)
                            || BashCommandScanner.FindWriteTargetCsArgument(segment) is not null
                            || BashCommandScanner.FindReadTargetCsArgument(segment) is not { } argument)
                        {
                            continue;
                        }
                        var absolute = Path.GetFullPath(Path.Combine(workingDirectory, argument));
                        if (File.Exists(absolute) && CsFileMembership.TryResolveOwningProject(absolute, context.Root, out _))
                        {
                            return true;
                        }
                    }
                    return false;
                }
            default:
                return false;
        }
    }
}
