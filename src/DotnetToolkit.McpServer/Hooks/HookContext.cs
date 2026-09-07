namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// The ambient values a hook handler needs: which repo it is guarding, where the tool docs it points
/// at live, and what directory a relative path in the payload resolves against.
/// </summary>
/// <remarks>
/// Passed in rather than read from the environment inside each handler, so tests can drive a handler
/// against a temporary directory without mutating process-wide state.
/// </remarks>
/// <param name="Root">The repo root, resolved the same way <c>SolutionLocator</c> resolves it.</param>
/// <param name="DocsDirectory">Absolute path of the plugin's <c>docs/tools</c> directory.</param>
/// <param name="WorkingDirectory">Directory a relative path in the payload is resolved against.</param>
/// <param name="ReadBlocklist">
/// Command names treated as raw file reads by <see cref="GuardCsBashRead"/>.
/// </param>
internal sealed record HookContext(
    string Root,
    string DocsDirectory,
    string WorkingDirectory,
    IReadOnlySet<string> ReadBlocklist)
{
    /// <summary>Shell utilities that dump a file's bytes, used when the environment names no others.</summary>
    /// <remarks>
    /// The list is about what a command <em>does to the transcript</em>, not about how it is usually used.
    /// The second and third rows were added after a red-team pass read compiled source through all of them
    /// with no advisory at all: a hex or base64 dump puts the same source in front of the model as
    /// <c>cat</c> does, only less legibly, and an interpreter one-liner does it without naming the file as
    /// an argument. Leaving them out made the advisory a matter of which utility the caller happened to
    /// reach for.
    /// </remarks>
    public static IReadOnlySet<string> DefaultReadBlocklist { get; } = new HashSet<string>(
        [
            "cat", "sed", "head", "tail", "less", "more", "awk", "gawk",
            "grep", "egrep", "fgrep", "rg", "ag", "nl", "tac", "bat",
            "xxd", "od", "hexdump", "base64", "strings", "dd",
            "python", "python3", "py", "perl", "ruby", "node", "deno", "pwsh", "powershell", "php",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds the context from the environment Claude Code invokes the hook process in.</summary>
    /// <returns>
    /// A context whose root follows <c>DOTNET_TOOLKIT_PROJECT_DIR</c>, then <c>CLAUDE_PROJECT_DIR</c>,
    /// then the current directory — the same precedence, in the same order, that
    /// <c>SolutionLocator</c>'s constructor applies, so a hook never guards a different tree than the
    /// server loaded.
    /// </returns>
    public static HookContext FromEnvironment()
    {
        var root = Path.GetFullPath(
            Environment.GetEnvironmentVariable("DOTNET_TOOLKIT_PROJECT_DIR")
            ?? Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR")
            ?? Directory.GetCurrentDirectory());

        var pluginRoot = PluginLocation.Resolve(root);

        var blocklist = Environment.GetEnvironmentVariable("DOTNET_TOOLKIT_READ_BLOCKLIST") is { } configured
            && !string.IsNullOrWhiteSpace(configured)
                ? new HashSet<string>(
                    configured.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries),
                    StringComparer.OrdinalIgnoreCase)
                : DefaultReadBlocklist;

        return new HookContext(
            root,
            Path.Combine(pluginRoot, "docs", "tools"),
            Directory.GetCurrentDirectory(),
            blocklist);
    }

    /// <summary>Path of one tool's reference doc, for citing in a denial message.</summary>
    /// <param name="tool">The tool name, e.g. <c>get_symbol</c>.</param>
    /// <returns>An absolute path to that tool's markdown file.</returns>
    public string Doc(string tool) => Path.Combine(DocsDirectory, tool + ".md");
}
