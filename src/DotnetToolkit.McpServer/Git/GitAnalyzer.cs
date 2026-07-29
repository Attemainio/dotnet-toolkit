using System.Diagnostics;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Extensions.Logging;

namespace DotnetToolkit.McpServer.Git;

/// <summary>
/// Thin wrapper over the <c>git</c> CLI (deliberately not LibGit2Sharp — no native dependency to ship,
/// and the repo already assumes git is present). Used by the semantic diff to enumerate what changed
/// between two refs and to read file contents at a ref without touching the working tree.
/// </summary>
public sealed class GitAnalyzer
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly SolutionLocator _locator;
    private readonly ILogger<GitAnalyzer> _log;
    private readonly string? _repositoryOverride;
    private readonly Lazy<IReadOnlyList<string>> _repositories;

    public GitAnalyzer(SolutionLocator locator, ILogger<GitAnalyzer> log)
        : this(locator, log, null)
    {
    }

    private GitAnalyzer(SolutionLocator locator, ILogger<GitAnalyzer> log, string? repositoryOverride)
    {
        _locator = locator;
        _log = log;
        _repositoryOverride = repositoryOverride;
        _repositories = new Lazy<IReadOnlyList<string>>(DiscoverRepositories);
    }

    /// <summary>
    /// Every repository this solution's history can be read from: the solution root itself when it sits
    /// inside a work tree, otherwise the repositories checked out directly beneath it.
    /// </summary>
    /// <remarks>
    /// A solution root is not always a repository. Projects belonging to separate repositories are
    /// routinely checked out side by side under one folder that was never itself one, and resolving git
    /// from the root alone reports "not a git repository" for a solution whose every project is
    /// versioned. Scanned once, one level deep, and cached.
    /// </remarks>
    /// <value>Absolute repository roots, ordered by path; empty when there is no repository at all.</value>
    public IReadOnlyList<string> Repositories => _repositories.Value;

    /// <summary>The directory this analyzer runs its git commands in.</summary>
    /// <value>The repository this analyzer was bound to, or the solution root when it was not bound.</value>
    public string RepositoryDirectory => _repositoryOverride ?? _locator.Root;

    /// <summary>Returns an analyzer that runs every command inside one specific repository.</summary>
    /// <param name="repositoryRoot">Absolute path of a repository, normally one of <see cref="Repositories"/>.</param>
    /// <returns>A new analyzer bound to that repository; this one is left unchanged.</returns>
    public GitAnalyzer For(string repositoryRoot) => new(_locator, _log, repositoryRoot);

    private IReadOnlyList<string> DiscoverRepositories()
    {
        // git resolves a work tree from any directory inside it, so a .git entry on the root or any
        // ancestor of it means the root is already the right place to run.
        for (var dir = new DirectoryInfo(_locator.Root); dir is not null; dir = dir.Parent)
        {
            if (Path.Exists(Path.Combine(dir.FullName, ".git")))
                return [_locator.Root];
        }

        try
        {
            return Directory.EnumerateDirectories(_locator.Root)
                .Where(d => Path.Exists(Path.Combine(d, ".git")))
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "Cannot scan {Root} for repositories", _locator.Root);
            return [];
        }
    }

    public sealed record ChangedFile(string Path, string Status);

    /// <summary>True when the target root is inside a git work tree.</summary>
    public async Task<bool> IsRepositoryAsync(CancellationToken ct = default) =>
        (await RunAsync(["rev-parse", "--is-inside-work-tree"], ct)).Ok;

    /// <summary>Resolves a ref (branch, tag, sha, HEAD~2) to a full commit sha.</summary>
    public async Task<string?> ResolveRefAsync(string reference, CancellationToken ct = default)
    {
        ValidateRef(reference);
        var result = await RunAsync(["rev-parse", "--verify", $"{reference}^{{commit}}"], ct);
        return result.Ok ? result.Output.Trim() : null;
    }

    /// <summary>Number of commits in <c>from..to</c>.</summary>
    public async Task<int> CommitCountAsync(string fromRef, string toRef, CancellationToken ct = default)
    {
        ValidateRef(fromRef);
        ValidateRef(toRef);
        var result = await RunAsync(["rev-list", "--count", $"{fromRef}..{toRef}"], ct);
        return result.Ok && int.TryParse(result.Output.Trim(), out var count) ? count : 0;
    }

    /// <summary>C# files that differ between two refs, with their git status letter.</summary>
    public async Task<IReadOnlyList<ChangedFile>> ChangedCSharpFilesAsync(string fromRef, string toRef, CancellationToken ct = default)
    {
        ValidateRef(fromRef);
        ValidateRef(toRef);
        var result = await RunAsync(["diff", "--name-status", "-M", fromRef, toRef], ct);
        if (!result.Ok)
            return [];

        var files = new List<ChangedFile>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            // Rename entries are "R100\told\tnew" -- the destination is the last field.
            var path = parts[^1].Trim();
            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                files.Add(new ChangedFile(path, parts[0].Trim()));
        }
        return files;
    }

    /// <summary>File content at a ref, or null when the path did not exist there.</summary>
    public async Task<string?> FileAtRefAsync(string reference, string relativePath, CancellationToken ct = default)
    {
        ValidateRef(reference);
        var result = await RunAsync(["show", $"{reference}:{relativePath}"], ct);
        return result.Ok ? result.Output : null;
    }

    /// <summary>
    /// Rejects a ref that looks like a CLI option rather than a ref name -- an unvalidated ref reaching
    /// git's positional-argument parser as e.g. <c>--output=/some/path</c> is interpreted as a flag,
    /// not a ref, giving whoever controls it an argument-injection primitive under this process's
    /// permissions.
    /// </summary>
    /// <exception cref="ArgumentException">The ref is empty or starts with '-'.</exception>
    private static void ValidateRef(string reference)
    {
        if (reference.Length == 0 || reference[0] == '-')
            throw new ArgumentException($"Invalid git ref: {reference}");
    }


    private async Task<(bool Ok, string Output)> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return (false, "");

            // Both pipes drained concurrently: draining one first deadlocks once the other fills.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CommandTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return (false, "");
            }

            var stdout = await stdoutTask;
            _ = await stderrTask;
            return (process.ExitCode == 0, stdout);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "git {Args} failed", string.Join(' ', args));
            return (false, "");
        }
    }
}
