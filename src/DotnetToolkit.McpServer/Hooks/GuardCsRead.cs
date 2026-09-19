namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// <c>PreToolUse</c> guard on <c>Read</c>: allows a raw read of a <c>.cs</c> file a project actually
/// compiles, but surfaces advisory context pointing at the <c>dotnet-read</c> skill rather than denying it
/// — and only for a WHOLE-file read. A <c>Read</c> with an <c>offset</c> or <c>limit</c> is the intended
/// way to fetch a located symbol's text (its <c>declarationSites</c> range), so it draws no note at all,
/// in forced-block mode included.
/// </summary>
/// <remarks>
/// Read-side enforcement is advisory, not blocking: a wrong or wasteful read costs nothing irreversible,
/// and the 2026-08-21 performance run showed the MCP route is not reliably cheaper or more correct on
/// every kind of question (a small file, a whole-repo negative, raw text search) - a hard deny forced the
/// more expensive route even where it was clearly the wrong pick. There is no write-side guard at all:
/// <c>Edit</c> and <c>Write</c> are the write path for C#, and the post-write refresh hook keeps the
/// index in step with them.
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

        if (IsRanged(payload))
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
                Blocked whole-file Read on {file}: guard-cs-read is running in forced-block mode
                (set_hook_guards(state: "block")), and {file} is compiled by {project}. Locate the symbol
                with search_index or get_symbol, then Read its declarationSites range with offset/limit -
                a ranged Read is never blocked.

                Do this instead: invoke the dotnet-read skill.

                Forced-block is not this plugin's shipped default - it exists for dotnet-performance's MCP
                probe, so a raw read attempted through it fails loudly instead of silently contaminating the
                measurement. Call set_hook_guards(state: "restore") once that run is done.
                """);
        }

        return HookOutcome.AllowAdvising($"""
            {file} is compiled by {project}, and this is a whole-file read. Locating first usually costs
            less and misses nothing: search_index or get_symbol gives the symbol's declarationSites, and a
            Read with offset/limit over that range fetches exactly the text wanted, every part of a partial
            included. A ranged Read draws no note. This is advice, not a denial - a small file, or a
            question about the file rather than a symbol in it, is a fine reason to read it whole.

            Consider invoking the dotnet-read skill: it names the right tool for the question you were
            about to answer and the call shape that costs least. None of that is repeated in this message
            on purpose: a copy here would drift from the skill.
            """);
    }

    /// <summary>Whether the read is narrowed to a line range rather than the whole file.</summary>
    /// <param name="payload">The parsed hook payload, whose raw <c>tool_input</c> carries the arguments.</param>
    /// <returns>True when <c>offset</c> or <c>limit</c> is present with a value.</returns>
    internal static bool IsRanged(HookPayload payload)
    {
        if (payload.ToolInputRaw is not { Length: > 0 } raw)
        {
            return false;
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && (HasValue(doc.RootElement, "offset") || HasValue(doc.RootElement, "limit"));
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        static bool HasValue(System.Text.Json.JsonElement element, string name) =>
            element.TryGetProperty(name, out var value)
            && value.ValueKind is System.Text.Json.JsonValueKind.Number or System.Text.Json.JsonValueKind.String;
    }
}
