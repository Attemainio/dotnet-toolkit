namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// Entry point for the <c>hook</c> verb: runs one Claude Code hook and exits, instead of starting the
/// MCP server.
/// </summary>
/// <remarks>
/// These hooks were shell scripts until they had to run on Windows. Claude Code registers a hook as a
/// command line, so a <c>.sh</c> file with a shebang is unrunnable wherever <c>bash</c> is not the
/// shell, and the scripts' <c>node</c>/<c>python3</c>/<c>jq</c> JSON-extraction chain was absent or
/// stubbed there too. Shipping them inside the server binary the plugin already publishes removes both
/// dependencies: <c>hooks.json</c> now invokes <c>dotnet &lt;dll&gt; hook &lt;name&gt;</c>, which is a
/// command every supported platform can run.
/// <para>
/// <b>Fails open by design.</b> These are workflow guards, not a security boundary: an unparseable
/// payload, an unresolvable root, or an unexpected exception must not wedge the user's editing, so
/// every uncertain path allows. What is deliberately no longer possible is failing open <i>silently
/// because the JSON never got parsed</i> — that was the Windows bug, not the design.
/// </para>
/// <para>
/// Denial is exit-2-plus-stderr rather than a <c>permissionDecision</c> JSON object. Both are
/// supported by Claude Code, and this way the guards need no JSON writer on the path that has to be
/// reliable.
/// </para>
/// </remarks>
internal static class HookCli
{
    /// <summary>The first argument that selects hook mode.</summary>
    public const string Verb = "hook";

    /// <summary>Runs a hook if the command line asks for one.</summary>
    /// <param name="args">The process arguments.</param>
    /// <returns>
    /// The process exit code when <paramref name="args"/> starts with <see cref="Verb"/>, or null when
    /// it does not — in which case the caller should start the MCP server as usual.
    /// </returns>
    public static async Task<int?> TryRunAsync(string[] args)
    {
        if (args.Length < 2 || args[0] != Verb)
        {
            return null;
        }

        HookOutcome outcome;
        try
        {
            outcome = await EvaluateAsync(args[1]);
        }
        catch (Exception)
        {
            // Top-level boundary for a guard that must never block the user's work on its own failure.
            return 0;
        }

        if (outcome.Stdout is { } stdout)
        {
            Console.Out.WriteLine(stdout);
        }

        if (outcome.Stderr is { } stderr)
        {
            Console.Error.WriteLine(stderr);
        }

        return outcome.ExitCode;
    }

    private static async Task<HookOutcome> EvaluateAsync(string name)
    {
        var raw = await Console.In.ReadToEndAsync();
        var payload = HookPayload.TryParse(raw);
        if (payload is null)
        {
            return HookOutcome.Allow;
        }

        var context = HookContext.FromEnvironment();
        return name switch
        {
            "guard-cs-edit" when payload.ToolName is "Edit" or "Write" or "NotebookEdit" =>
                GuardCsEdit.Evaluate(payload, context),
            "guard-cs-read" when payload.ToolName == "Read" =>
                GuardCsRead.Evaluate(payload, context),
            "guard-cs-bash-read" when payload.ToolName == "Bash" =>
                GuardCsBashRead.Evaluate(payload, context),
            "hint-reload-new-cs-file" =>
                await ReloadHint.EvaluateAsync(payload, context),
            _ => HookOutcome.Allow,
        };
    }
}
