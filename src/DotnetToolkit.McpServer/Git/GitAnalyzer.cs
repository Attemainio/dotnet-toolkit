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

    public GitAnalyzer(SolutionLocator locator, ILogger<GitAnalyzer> log)
    {
        _locator = locator;
        _log = log;
    }

    public sealed record ChangedFile(string Path, string Status);

    /// <summary>True when the target root is inside a git work tree.</summary>
    public async Task<bool> IsRepositoryAsync(CancellationToken ct = default) =>
        (await RunAsync(["rev-parse", "--is-inside-work-tree"], ct)).Ok;

    /// <summary>Resolves a ref (branch, tag, sha, HEAD~2) to a full commit sha.</summary>
    public async Task<string?> ResolveRefAsync(string reference, CancellationToken ct = default)
    {
        var result = await RunAsync(["rev-parse", "--verify", $"{reference}^{{commit}}"], ct);
        return result.Ok ? result.Output.Trim() : null;
    }

    /// <summary>Number of commits in <c>from..to</c>.</summary>
    public async Task<int> CommitCountAsync(string fromRef, string toRef, CancellationToken ct = default)
    {
        var result = await RunAsync(["rev-list", "--count", $"{fromRef}..{toRef}"], ct);
        return result.Ok && int.TryParse(result.Output.Trim(), out var count) ? count : 0;
    }

    /// <summary>C# files that differ between two refs, with their git status letter.</summary>
    public async Task<IReadOnlyList<ChangedFile>> ChangedCSharpFilesAsync(string fromRef, string toRef, CancellationToken ct = default)
    {
        var result = await RunAsync(["diff", "--name-status", "-M", fromRef, toRef], ct);
        if (!result.Ok)
            return [];

        var files = new List<ChangedFile>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            // Rename entries are "R100\told\tnew" — the destination is the last field.
            var path = parts[^1].Trim();
            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                files.Add(new ChangedFile(path, parts[0].Trim()));
        }
        return files;
    }

    /// <summary>File content at a ref, or null when the path did not exist there.</summary>
    public async Task<string?> FileAtRefAsync(string reference, string relativePath, CancellationToken ct = default)
    {
        var result = await RunAsync(["show", $"{reference}:{relativePath}"], ct);
        return result.Ok ? result.Output : null;
    }

    private async Task<(bool Ok, string Output)> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _locator.Root,
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
