namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// <c>PreToolUse</c> guard on harness-native edit tools: keeps C# edits on the
/// <c>validate_patch</c> write path in Claude Code and Codex.
/// </summary>
/// <remarks>
/// CLAUDE.md has carried this rule since the plugin existed and it is still the one broken most often,
/// because CLAUDE.md is context, not enforcement, and adherence decays over a long session. The compile
/// check is the cheap half of what <c>validate_patch</c> does; the development-log entry is the half
/// that cannot be recovered later, so an <c>Edit</c> that slips through is a permanent hole in the log
/// rather than a slower route to the same place.
/// </remarks>
internal static class GuardCsEdit
{
    /// <summary>Decides whether an edit tool call may proceed.</summary>
    /// <param name="payload">The parsed hook payload.</param>
    /// <param name="context">The repo paths the membership check is scoped to.</param>
    /// <returns>A denial for a C# file that already exists; otherwise <see cref="HookOutcome.Allow"/>.</returns>
    public static HookOutcome Evaluate(HookPayload payload, HookContext context)
    {
        if (payload.ToolName == "apply_patch")
        {
            foreach (var change in ApplyPatchCommand.Parse(payload.Command))
            {
                var outcome = EvaluateFile(
                    payload.ToolName,
                    change.Path,
                    context,
                    allowNewFile: change.Kind == ApplyPatchCommand.ChangeKind.Add);
                if (outcome.ExitCode != 0)
                {
                    return outcome;
                }
            }

            return HookOutcome.Allow;
        }

        var file = payload.FilePath;
        return EvaluateFile(payload.ToolName, file, context, allowNewFile: payload.ToolName == "Write");
    }

    private static HookOutcome EvaluateFile(
        string toolName,
        string? file,
        HookContext context,
        bool allowNewFile)
    {
        if (string.IsNullOrEmpty(file) || !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            // Everything else is explicitly the plain-tool path: csproj, json, md, sh. .csx/.cshtml/.razor
            // are not what validate_patch operates on either.
            return HookOutcome.Allow;
        }

        // Creating a new file is not a validate_patch case: it needs baseVersions keyed by a symbolId,
        // and a symbol that does not exist yet has no version to lease against. Write it, then make
        // every subsequent change through the tool.
        var absolute = Path.GetFullPath(Path.Combine(context.WorkingDirectory, file));
        if (allowNewFile && !File.Exists(absolute))
        {
            return HookOutcome.Allow;
        }

        // A .cs file no project compiles is one validate_patch cannot write either: it resolves edits
        // through the loaded solution and answers file_not_in_solution for anything outside it. Denying
        // the plain tool there leaves NO write path at all -- the case that found this is this repo's own
        // tests/.../fixtures/SampleSolution, deliberately excluded from the build so tests can load it
        // as a workspace of its own. GuardCsRead has always gated on membership; this guard did not, so
        // the same file was readable and uneditable. Membership, not the extension, is the real test.
        if (File.Exists(absolute)
            && !CsFileMembership.TryResolveOwningProject(absolute, context.Root, out _))
        {
            return HookOutcome.Allow;
        }

        return HookOutcome.Deny($"""
            Blocked {toolName} on {file}: C# edits go through validate_patch, not {toolName}.

            validate_patch is the write path for .cs files, not a faster dotnet build. It is also the ONLY thing
            that appends to the development log - an edit made with {toolName} is a change whose reasoning
            is gone the moment this conversation ends, and search_log can never recover it.

            Do this instead: invoke the dotnet-write skill.

            It carries the fetch-to-patch loop, the standards step that runs before the first edit of a session,
            the judgment call in each argument, and every failure mode with its recovery - including the pure-rename
            path, which derives its call-site edits from the compiler's own graph instead of a hand-authored patch
            set. None of that is repeated in this message on purpose: a copy here would drift from the skill.

            A change that feels too large or too interleaved to decompose is still not a reason to fall back to
            {toolName}; the skill covers that case too.

            If this genuinely is not a validate_patch case (the workspace failed to load, or you are reverting a
            partial write), say so and ask the user to allow it explicitly rather than retrying the same call.
            """);
    }
}
