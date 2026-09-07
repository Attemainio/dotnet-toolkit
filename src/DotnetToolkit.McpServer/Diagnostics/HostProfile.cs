using System.Runtime.InteropServices;

namespace DotnetToolkit.McpServer.Diagnostics;

/// <summary>How an agent host and the OS running its tool processes are paired.</summary>
/// <remarks>
/// The distinction the phrase "same host" hides. A Windows desktop app delegating execution into WSL2
/// keeps its Windows-owned profile while every path it stores has to be opened by a Linux process, so
/// which profile is read and which namespace paths must be written in stop agreeing — and nothing in
/// either tool's inventory output says so.
/// </remarks>
internal enum ExecutionTopology
{
    /// <summary>Windows agent host, Windows tool processes. Paths are drive-rooted throughout.</summary>
    NativeWindows,

    /// <summary>Linux or macOS throughout, with no Windows filesystem in the picture.</summary>
    NativeUnix,

    /// <summary>WSL2 throughout, including the agent profile. Paths are Unix, possibly under <c>/mnt</c>.</summary>
    StandaloneWsl,

    /// <summary>
    /// A Windows-owned agent profile read by processes executing in WSL2 — the case where a stored
    /// Windows path and the process that must open it disagree.
    /// </summary>
    WindowsProfileThroughWsl,
}

/// <summary>
/// What the process running this diagnostic can observe about its own execution environment: the OS it
/// runs on, whether that is WSL, and which agent profile directories are in effect.
/// </summary>
/// <param name="OsDescription">The runtime's own description of the operating system.</param>
/// <param name="IsWsl">Whether this Linux process is running under the Windows Subsystem for Linux.</param>
/// <param name="WslDistro">The WSL distribution name, when one was published to the environment.</param>
/// <param name="CodexHome">The Codex profile directory in effect for this process.</param>
/// <param name="CodexHomeIsExplicit">
/// Whether <see cref="CodexHome"/> came from <c>CODEX_HOME</c> rather than the default under the user
/// profile. An explicit value is how a Windows-owned profile reaches a Linux process at all.
/// </param>
/// <param name="ClaudePluginRoot">The plugin directory Claude Code published, when this process was started by it.</param>
/// <param name="ProjectDirOverride">The repository root an agent named explicitly, when it named one.</param>
/// <seealso cref="PathNamespace"/>
internal sealed record HostProfile(
    string OsDescription,
    bool IsWsl,
    string? WslDistro,
    string CodexHome,
    bool CodexHomeIsExplicit,
    string? ClaudePluginRoot,
    string? ProjectDirOverride)
{
    /// <summary>Environment variable naming the Codex profile directory, overriding the default location.</summary>
    public const string CodexHomeVariable = "CODEX_HOME";

    /// <value>Which agent-host and executor pairing this process is part of.</value>
    /// <remarks>
    /// A Codex home reached through <c>/mnt</c> is the tell for the delegated-execution case: the
    /// profile lives on a Windows volume, so the application that owns it is the Windows one, while this
    /// process — the one that will launch the MCP server and open the paths that profile stores — is
    /// Linux.
    /// </remarks>
    public ExecutionTopology Topology
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return ExecutionTopology.NativeWindows;
            }

            if (!IsWsl)
            {
                return ExecutionTopology.NativeUnix;
            }

            return CodexHome.StartsWith("/mnt/", StringComparison.Ordinal)
                ? ExecutionTopology.WindowsProfileThroughWsl
                : ExecutionTopology.StandaloneWsl;
        }
    }

    /// <value>The path namespace every path this process opens has to be written in.</value>
    public string RequiredPathNamespace => OperatingSystem.IsWindows() ? @"C:\..." : "/... (POSIX)";

    /// <summary>Observes the current process's execution environment.</summary>
    /// <returns>The profile as the environment describes it at this moment.</returns>
    public static HostProfile Detect()
    {
        var explicitHome = Environment.GetEnvironmentVariable(CodexHomeVariable);
        var hasExplicitHome = !string.IsNullOrWhiteSpace(explicitHome);

        return new HostProfile(
            OsDescription: RuntimeInformation.OSDescription.Trim(),
            IsWsl: DetectWsl(),
            WslDistro: NullIfBlank(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")),
            CodexHome: hasExplicitHome
                ? explicitHome!
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            CodexHomeIsExplicit: hasExplicitHome,
            ClaudePluginRoot: NullIfBlank(Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_ROOT")),
            ProjectDirOverride: NullIfBlank(Environment.GetEnvironmentVariable("DOTNET_TOOLKIT_PROJECT_DIR"))
                ?? NullIfBlank(Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR")));
    }

    /// <summary>Decides whether this Linux process is running under WSL.</summary>
    /// <returns>True when either WSL's own environment marker or the kernel release says so.</returns>
    /// <remarks>
    /// <c>WSL_DISTRO_NAME</c> alone is not enough: it is set by the interactive login shell, so a process
    /// launched directly by a desktop application may not inherit it. The kernel release string carries
    /// <c>microsoft</c> on every WSL2 kernel and is readable without a shell, which is why both are
    /// consulted.
    /// </remarks>
    private static bool DetectWsl()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return false;
        }

        if (Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is { Length: > 0 })
        {
            return true;
        }

        try
        {
            return RuntimeInformation.OSDescription.Contains("microsoft", StringComparison.OrdinalIgnoreCase)
                || (File.Exists("/proc/version")
                    && File.ReadAllText("/proc/version").Contains("microsoft", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            // A procfs read that fails leaves the WSL question unanswered, which is a strictly better
            // outcome than a diagnostic that cannot start.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Normalizes a blank environment variable to null.</summary>
    /// <param name="value">The raw variable value.</param>
    /// <returns>The value, or null when it was absent or whitespace.</returns>
    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
