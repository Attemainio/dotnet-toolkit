namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// <c>PreToolUse</c> guard on <c>Bash</c>: denies a shell command that would overwrite a compiled
/// <c>.cs</c> file's content the way <c>Edit</c>/<c>Write</c> would.
/// </summary>
/// <remarks>
/// <see cref="GuardCsEdit"/> only ever sees the <c>Edit</c>/<c>Write</c>/<c>NotebookEdit</c> tools by
/// name, so a shell command that overwrites the same bytes — <c>echo ... &gt;&gt; Foo.cs</c>,
/// <c>sed -i</c>, <c>perl -i</c>, <c>tee Foo.cs</c>, a <c>cp</c>/<c>mv</c> onto an existing <c>.cs</c>
/// file — reaches disk with no compile check and no development-log entry, exactly the failure
/// <see cref="GuardCsEdit"/> exists to prevent. This guard closes that gap the way
/// <see cref="GuardCsBashRead"/> closes the read-side one: matching on <c>Bash</c> and inspecting what
/// the command is about to do. Unlike <see cref="GuardCsBashRead"/> it is a hard deny, not advisory —
/// the write side keeps the harder bar <see cref="GuardCsEdit"/> sets, because only
/// <c>validate_patch</c> appends to the development log, so an unblocked write is unrecoverable, not
/// merely a less efficient read.
/// <para>
/// <see cref="BashCommandScanner.FindWriteTargetCsArgument"/> is not a shell parser and does not claim
/// to recognize every write shape (an unspaced redirect, an obscure editor); see its own remarks for
/// what is and is not covered. That is the same fail-open posture every guard in this file takes, not a
/// security boundary meant to be airtight.
/// </para>
/// </remarks>
internal static class GuardCsBashWrite
{
    /// <summary>Decides whether a <c>Bash</c> call that would write to a compiled <c>.cs</c> file may proceed.</summary>
    /// <param name="payload">The parsed hook payload.</param>
    /// <param name="context">The repo root and working directory the membership check is scoped to.</param>
    /// <returns>
    /// A denial naming the first write-shaped segment targeting a file a project compiles; otherwise
    /// <see cref="HookOutcome.Allow"/>.
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
            // A cd earlier in the same command changes what a later relative path means - see
            // GuardCsBashRead for the finding that made this tracking necessary.
            if (BashCommandScanner.CdTarget(segment) is { } moved)
            {
                workingDirectory = Path.GetFullPath(Path.Combine(workingDirectory, moved));
                continue;
            }

            var target = BashCommandScanner.FindWriteTargetCsArgument(segment);
            if (target is null)
            {
                continue;
            }

            var absolute = Path.GetFullPath(Path.Combine(workingDirectory, target));

            // Same allowance guard-cs-edit makes for Write creating a new file: a file that does not
            // exist yet has no symbolId for validate_patch to lease a version against.
            if (!File.Exists(absolute))
            {
                continue;
            }

            if (!CsFileMembership.TryResolveOwningProject(absolute, context.Root, out var project))
            {
                continue;
            }

            return HookOutcome.Deny($"""
                Blocked Bash on {target}: this command would overwrite a file {project} compiles, and C#
                edits go through validate_patch, not a shell redirect, in-place editor, or copy.

                validate_patch is the write path for .cs files. It is also the ONLY thing that appends to
                the development log - a change made this way is a change whose reasoning is gone the moment
                this conversation ends, and search_log can never recover it.

                Do this instead: invoke the dotnet-write skill.

                It carries the fetch-to-patch loop, the standards step that runs before the first edit of a
                session, the judgment call in each argument, and every failure mode with its recovery. None
                of that is repeated in this message on purpose: a copy here would drift from the skill.

                If this genuinely is not a validate_patch case, say so and ask the user to allow it
                explicitly rather than retrying the same command.
                """);
        }

        return HookOutcome.Allow;
    }
}
