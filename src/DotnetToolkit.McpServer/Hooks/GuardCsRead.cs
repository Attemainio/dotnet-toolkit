namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// <c>PreToolUse</c> guard on <c>Read</c>: blocks a raw read of a <c>.cs</c> file a project actually
/// compiles, pointing at <c>search_index</c>/<c>get_symbol</c> instead.
/// </summary>
/// <remarks>
/// Skills and CLAUDE.md are context, not enforcement, and adherence decays over a long session — the
/// same reasoning behind <see cref="GuardCsEdit"/>. A <c>Read</c> on a large multi-symbol file quietly
/// pulls in every method in it whether or not the task needs them, at a cost <c>get_symbol</c> (one
/// symbol, or a type's member list) and <c>search_index</c> (ranked hits with file and line, no
/// truncation) do not pay.
/// <para>
/// One gap this cannot close: a file that <i>is</i> governed by a project while the server's own
/// workspace is still index-only or degraded. That is runtime state of the running process, invisible
/// to a static filesystem check for the same reason <c>WorkspaceHost</c> itself is.
/// </para>
/// </remarks>
internal static class GuardCsRead
{
    /// <summary>Decides whether a <c>Read</c> call may proceed.</summary>
    /// <param name="payload">The parsed hook payload.</param>
    /// <param name="context">The repo root the membership check is scoped to, and the docs to cite.</param>
    /// <returns>
    /// A denial when the file is compiled by a project of this repo's own solution; otherwise
    /// <see cref="HookOutcome.Allow"/>.
    /// </returns>
    public static HookOutcome Evaluate(HookPayload payload, HookContext context)
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

        return HookOutcome.Deny($"""
            Blocked Read on {file}: it is compiled by {project}, so search_index/get_symbol answer this more
            cheaply and completely than a raw file read - no truncation risk, and no irrelevant methods pulled in
            alongside the one you want.

            Do this instead:
              - Don't know the exact symbol name: search_index(query: "term1 term2 ...") - one call, many terms.
              - Know the type/member name: get_symbol(symbol: "...").
              - Only need part of a long member: get_symbol(symbol: "...", include: "source:code@120-160").

            For arguments and worked examples, read the one file for the tool you are about to call:
              {context.Doc("search_index")}
              {context.Doc("get_symbol")}
            The always-loaded .claude/rules/index.md routes any other question to its tool and names
            its file. Read one file, not the directory.

            If this genuinely needs a raw read (the workspace failed to load, or the file's exact formatting/byte
            layout is itself what you need to see), say so and ask the user to allow it explicitly rather than
            retrying Read on this file.
            """);
    }
}
