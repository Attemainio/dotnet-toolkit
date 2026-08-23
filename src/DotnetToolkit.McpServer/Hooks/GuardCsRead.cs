namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// <c>PreToolUse</c> guard on <c>Read</c>: allows a raw read of a <c>.cs</c> file a project actually
/// compiles, but surfaces advisory context pointing at the <c>dotnet-read</c> skill rather than denying it.
/// </summary>
/// <remarks>
/// Read-side enforcement is advisory, not blocking: a wrong or wasteful read costs nothing irreversible,
/// and the 2026-08-21 performance run showed the MCP route is not reliably cheaper or more correct on
/// every kind of question (a small file, a whole-repo negative, raw text search) - a hard deny forced the
/// more expensive route even where it was clearly the wrong pick. <see cref="GuardCsEdit"/> stays a hard
/// deny because its stakes differ: only <c>validate_patch</c> appends to the development log, so an
/// unblocked edit is a change whose reasoning is unrecoverable, not merely a less efficient read.
/// <para>
/// One gap this cannot close: a file that <i>is</i> governed by a project while the server's own
/// workspace is still index-only or degraded. That is runtime state of the running process, invisible
/// to a static filesystem check for the same reason <c>WorkspaceHost</c> itself is.
/// </para>
/// </remarks>
internal static class GuardCsRead
{
    /// <summary>Decides whether a <c>Read</c> call gets an advisory note.</summary>
    /// <param name="payload">The parsed hook payload.</param>
    /// <param name="context">The repo root the membership check is scoped to, and the docs to cite.</param>
    /// <returns>
    /// An allow outcome carrying advisory <c>permissionDecisionReason</c> text when the file is compiled by
    /// a project of this repo's own solution; otherwise a plain <see cref="HookOutcome.Allow"/>.
    /// </returns>
    public static HookOutcome Evaluate(HookPayload payload, HookContext context, bool blocked = false)
    {
        var file = payload.FilePath;
        if (string.IsNullOrEmpty(file) || !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return HookOutcome.Allow;
        }

        var absolute = Path.GetFullPath(Path.Combine(context.WorkingDirectory, file));
        if (!File.Exists(absolute))
        {
            return HookOutcome.Allow;
        }

        if (!CsFileMembership.TryResolveOwningProject(absolute, context.Root, out var project))
        {
            return HookOutcome.Allow;
        }

        if (blocked)
        {
            return HookOutcome.Deny($"""
                Blocked Read on {file}: guard-cs-read is running in forced-block mode
                (set_hook_guards(state: "block")), and {file} is compiled by {project}. This repo's
                dotnet-toolkit MCP tools answer this kind of question more cheaply and completely than a raw
                file read - no truncation risk, and no irrelevant methods pulled in alongside the one you want.

                Do this instead: invoke the dotnet-read skill.

                Forced-block is not this plugin's shipped default - it exists for dotnet-performance's MCP
                probe, so a raw read attempted through it fails loudly instead of silently contaminating the
                measurement. Call set_hook_guards(state: "restore") once that run is done.
                """);
        }

        return HookOutcome.AllowAdvising($"""
            {file} is compiled by {project}. This repo's dotnet-toolkit MCP tools often answer this kind of
            question more cheaply and completely than a raw file read - no truncation risk, and no
            irrelevant methods pulled in alongside the one you want - though not on every question, so this
            is advice, not a denial.

            Consider invoking the dotnet-read skill instead. It names the right tool for the question you
            were about to answer, the call shape that costs least, and how to read the response. None of
            that is repeated in this message on purpose: a copy here would drift from the skill.
            """);
    }
}
