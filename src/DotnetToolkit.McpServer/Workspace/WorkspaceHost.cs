using System.Diagnostics;
using DotnetToolkit.McpServer.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace DotnetToolkit.McpServer.Workspace;

public enum WorkspaceState
{
    NotStarted,
    Loading,
    Loaded,
    Failed,
    NoSolution,
}

/// <summary>
/// Semantic knowledge tier: owns the MSBuildWorkspace. Loading starts in the background
/// after the MCP transport is up (startup must stay under the ~5s handshake timeout);
/// semantic tools await <see cref="GetSolutionAsync"/> with a bounded timeout.
/// Edited files reported by the syntax index are patched into the current Solution
/// without a full reload; added/removed files queue a background reload.
/// </summary>
public sealed class WorkspaceHost : IDisposable
{
    private readonly SolutionLocator _locator;
    private readonly ILogger<WorkspaceHost> _log;
    private readonly object _gate = new();
    private readonly List<string> _loadDiagnostics = [];
    private readonly Stopwatch _loadWatch = new();

    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private Task _loadTask = Task.CompletedTask;
    private int _reloading;

    public WorkspaceState State { get; private set; }

    public TimeSpan LoadElapsed => _loadWatch.Elapsed;

    public IReadOnlyList<string> LoadDiagnostics
    {
        get { lock (_gate) return [.. _loadDiagnostics]; }
    }

    public int ProjectCount
    {
        get { lock (_gate) return _solution?.ProjectIds.Count ?? 0; }
    }

    public WorkspaceHost(SolutionLocator locator, ProjectIndex index, ILogger<WorkspaceHost> log)
    {
        _locator = locator;
        _log = log;
        index.FilesChanged += OnFilesChanged;
    }

    public void StartLoading() => _loadTask = Task.Run(LoadAsync);

    private async Task LoadAsync()
    {
        var entry = _locator.WorkspaceEntry;
        if (entry is null)
        {
            State = WorkspaceState.NoSolution;
            return;
        }

        State = WorkspaceState.Loading;
        _loadWatch.Restart();
        try
        {
            var workspace = MSBuildWorkspace.Create();
            workspace.RegisterWorkspaceFailedHandler(e =>
            {
                lock (_gate)
                {
                    if (_loadDiagnostics.Count < 100)
                        _loadDiagnostics.Add($"{e.Diagnostic.Kind}: {e.Diagnostic.Message}");
                }
            });

            if (entry.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                await workspace.OpenProjectAsync(entry);
            }
            else
            {
                try
                {
                    await workspace.OpenSolutionAsync(entry);
                }
                catch (Exception ex) when (entry.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogWarning(ex, "OpenSolutionAsync failed for .slnx; opening projects individually");
                    foreach (var project in SlnxParser.ProjectPaths(entry))
                        await workspace.OpenProjectAsync(project);
                }
            }

            lock (_gate)
            {
                _workspace?.Dispose();
                _workspace = workspace;
                _solution = workspace.CurrentSolution;
            }
            State = WorkspaceState.Loaded;
            _log.LogInformation("Workspace loaded: {Projects} projects in {Elapsed:F1}s",
                ProjectCount, _loadWatch.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            State = WorkspaceState.Failed;
            lock (_gate)
                _loadDiagnostics.Add($"Load failed: {ex.Message}");
            _log.LogError(ex, "Workspace load failed");
        }
        finally
        {
            _loadWatch.Stop();
        }
    }

    /// <summary>Returns the current solution, or null if loading exceeds the timeout.</summary>
    public async Task<Solution?> GetSolutionAsync(TimeSpan? timeout = null)
    {
        var completed = await Task.WhenAny(_loadTask, Task.Delay(timeout ?? TimeSpan.FromSeconds(30)));
        if (completed != _loadTask)
            return null;
        lock (_gate)
            return _solution;
    }

    private void OnFilesChanged(IReadOnlyList<string> changedRelPaths, bool structural)
    {
        if (State != WorkspaceState.Loaded)
            return;

        if (structural)
        {
            TriggerReload();
            return;
        }

        lock (_gate)
        {
            if (_solution is null)
                return;
            foreach (var rel in changedRelPaths)
            {
                var abs = _locator.AbsPath(rel);
                if (!File.Exists(abs))
                    continue;
                foreach (var id in _solution.GetDocumentIdsWithFilePath(abs))
                    _solution = _solution.WithDocumentText(id, SourceText.From(File.ReadAllText(abs)));
            }
        }
    }

    /// <summary>Disposes the current workspace and re-opens the solution in the background.</summary>
    public void TriggerReload()
    {
        if (Interlocked.CompareExchange(ref _reloading, 1, 0) != 0)
            return;
        _loadTask = Task.Run(async () =>
        {
            try
            {
                lock (_gate)
                    _loadDiagnostics.Clear();
                // Config may have gained a `solution` override since startup; re-resolve before loading.
                _locator.Rescan();
                await LoadAsync();
            }
            finally
            {
                Interlocked.Exchange(ref _reloading, 0);
            }
        });
    }

    public void Dispose() => _workspace?.Dispose();
}
