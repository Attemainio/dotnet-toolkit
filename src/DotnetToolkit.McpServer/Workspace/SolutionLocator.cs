using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DotnetToolkit.McpServer.Workspace;

/// <summary>
/// Resolves the target repository root, per-repo config, and the workspace entry point
/// (.slnx/.sln/.csproj). The root comes from DOTNET_TOOLKIT_PROJECT_DIR, then
/// CLAUDE_PROJECT_DIR (set by Claude Code), then the process working directory.
/// </summary>
public sealed class SolutionLocator
{
    private static readonly string[] SkipDirs = ["bin", "obj", "node_modules", ".git", ".vs", "dist"];

    private readonly ILogger<SolutionLocator> _log;

    public string Root { get; }
    public ToolkitConfig Config { get; private set; }
    public string? WorkspaceEntry { get; private set; }
    public IReadOnlyList<string> Candidates { get; private set; }

    public string ToolkitDir => Path.Combine(Root, ".claude", "dotnet-toolkit");
    public string CacheDir => Path.Combine(ToolkitDir, "cache");
    public bool IsAmbiguous => WorkspaceEntry is null && Candidates.Count > 1;

    public SolutionLocator(ILogger<SolutionLocator> log, string? rootOverride = null)
    {
        Root = Path.GetFullPath(
            rootOverride
            ?? Environment.GetEnvironmentVariable("DOTNET_TOOLKIT_PROJECT_DIR")
            ?? Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR")
            ?? Directory.GetCurrentDirectory());

        _log = log;
        Config = LoadConfig(log);
        (WorkspaceEntry, Candidates) = ResolveEntry(log);
        log.LogInformation("Root: {Root}; workspace entry: {Entry}", Root, WorkspaceEntry ?? "(none)");
    }

    /// <summary>
    /// Re-reads <c>config.json</c> and re-resolves the workspace entry. Called by
    /// <see cref="WorkspaceHost.TriggerReload"/> so that writing a <c>solution</c> override into an
    /// ambiguous repo takes effect on reload rather than requiring a server restart — resolution
    /// otherwise happens once in the constructor, and a singleton locator would stay stuck on the
    /// original (null) entry forever.
    /// </summary>
    public void Rescan()
    {
        Config = LoadConfig(_log);
        (WorkspaceEntry, Candidates) = ResolveEntry(_log);
        _log.LogInformation("Re-resolved workspace entry: {Entry}", WorkspaceEntry ?? "(none)");
    }

    private ToolkitConfig LoadConfig(ILogger log)
    {
        var path = Path.Combine(ToolkitDir, "config.json");
        if (!File.Exists(path))
            return new ToolkitConfig();
        try
        {
            return JsonSerializer.Deserialize<ToolkitConfig>(File.ReadAllText(path)) ?? new ToolkitConfig();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to parse {Path}; using defaults", path);
            return new ToolkitConfig();
        }
    }

    private (string? Entry, IReadOnlyList<string> Candidates) ResolveEntry(ILogger log)
    {
        if (Config.Solution is { } configured)
        {
            var abs = Path.GetFullPath(Path.Combine(Root, configured));
            if (File.Exists(abs))
                return (abs, [abs]);
            log.LogWarning("Configured solution {Path} does not exist", abs);
        }

        foreach (var pattern in new[] { "*.slnx", "*.sln", "*.csproj" })
        {
            var hits = GlobDepth2(pattern);
            if (hits.Count == 1)
                return (hits[0], hits);
            if (hits.Count > 1)
                return (null, hits);
        }
        return (null, []);
    }

    private List<string> GlobDepth2(string pattern)
    {
        var hits = new List<string>(Directory.EnumerateFiles(Root, pattern));
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            if (SkipDirs.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase))
                continue;
            hits.AddRange(Directory.EnumerateFiles(dir, pattern));
        }
        hits.Sort(StringComparer.Ordinal);
        return hits;
    }

    /// <summary>Creates the cache dir and drops a self-excluding .gitignore into it.</summary>
    public string EnsureCacheDir()
    {
        Directory.CreateDirectory(CacheDir);
        var gitignore = Path.Combine(CacheDir, ".gitignore");
        if (!File.Exists(gitignore))
            File.WriteAllText(gitignore, "*\n");
        return CacheDir;
    }

    public string RelPath(string absolute)
    {
        var rel = Path.GetRelativePath(Root, absolute);
        return rel.Replace('\\', '/');
    }

    /// <summary>Resolves a root-relative path, rejecting escapes above the root.</summary>
    public string AbsPath(string relative)
    {
        var abs = Path.GetFullPath(Path.Combine(Root, relative));
        if (!abs.StartsWith(Root, StringComparison.Ordinal))
            throw new ArgumentException($"Path escapes the project root: {relative}");
        return abs;
    }

    public static bool ShouldSkipDir(string name) =>
        SkipDirs.Contains(name, StringComparer.OrdinalIgnoreCase);
}
