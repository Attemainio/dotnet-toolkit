namespace DotnetToolkit.McpServer;

/// <summary>
/// Resolves the directory the plugin is installed in, so callers can reach the files that ship
/// beside the server — <c>standards/</c> and <c>docs/tools/</c>.
/// </summary>
/// <remarks>
/// This exists because <c>${CLAUDE_PLUGIN_ROOT}</c> is substituted by the harness only into
/// <c>.mcp.json</c> args, hook command strings, and skill content — never into a rule file or an
/// agent definition, and it is not exported as an environment variable to arbitrary processes. A
/// session therefore has no way to name these paths itself; the server reports them instead, which
/// is correct on every machine and every plugin version because it is derived at runtime rather
/// than stored. Shared by <see cref="Hooks.HookContext"/> and <c>workspace_status</c> so the two can
/// never disagree on the answer.
/// </remarks>
internal static class PluginLocation
{
    /// <summary>Resolves the plugin's installation directory.</summary>
    /// <param name="fallback">Returned when the directory above the running assembly cannot be
    /// determined — normally the target repository root.</param>
    /// <returns>
    /// The value of <c>CLAUDE_PLUGIN_ROOT</c> when the harness set it, otherwise the directory
    /// containing the running assembly's own directory: the server and the hooks both run from
    /// <c>&lt;pluginRoot&gt;/dist/</c>, so its parent is the plugin root.
    /// </returns>
    public static string Resolve(string fallback)
    {
        var pluginRoot = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_ROOT");
        if (!string.IsNullOrWhiteSpace(pluginRoot))
        {
            return pluginRoot;
        }

        return Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar)) ?? fallback;
    }
}
