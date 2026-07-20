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

    /// <summary>Paths that changed while the workspace could not take them, applied once it can.</summary>
    private readonly HashSet<string> _pendingChanges = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Loaded, but at least one project failed — so the semantic model is missing whatever those
    /// projects contribute, and any answer derived from it may be quietly wrong rather than merely
    /// absent. Distinct from <see cref="WorkspaceState.Failed"/>, which is an honest total failure.
    ///
    /// This is a routine condition for a plugin that loads someone else's build: the MSBuild it
    /// registers comes from the server's own SDK, so a repo whose obj/ was restored by a different one
    /// fails ResolvePackageAssets and lands here. Observed on this repo both when the launcher's
    /// ~/.dotnet SDK and the SDK on PATH differed by a feature band, and when this checkout's obj/ was
    /// last restored on the Windows side of its `/mnt/c` path (e.g. by Visual Studio), which bakes in a
    /// Windows-only NuGet fallback folder that does not exist from here — every project failed, and
    /// every tool answered as if nothing were wrong. <see cref="RestoreAsync"/> restores before every
    /// load specifically to self-heal the second case rather than relying on a human noticing.
    /// </summary>
    public bool IsDegraded
    {
        get { lock (_gate) return State == WorkspaceState.Loaded && _loadDiagnostics.Count > 0; }
    }

    public int ProjectCount
    {
        get { lock (_gate) return _solution?.ProjectIds.Count ?? 0; }
    }

    /// <summary>
    /// Names of the projects actually in the loaded solution. A count alone cannot be acted on: when
    /// one project of a solution fails to load, the caller needs to know *which*, because semantic
    /// results for that project are silently degraded while the rest are sound.
    /// </summary>
    public IReadOnlyList<string> ProjectNames
    {
        get
        {
            lock (_gate)
                return _solution is null ? [] : [.. _solution.Projects.Select(p => p.Name).Order(StringComparer.Ordinal)];
        }
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
            await RestoreAsync(entry);

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
            DrainPendingChanges(); // edits that landed while this load was in flight
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

    /// <summary>
    /// Restores before opening rather than trusting whatever <c>obj/</c> already holds.
    /// <see cref="MSBuildWorkspace"/>'s design-time build does not restore — it reads the existing NuGet
    /// assets cache — so a cache written by a different environment (observed here: a Windows-side
    /// Visual Studio restore of this repo's `/mnt/c` checkout, which bakes in a VS-only fallback package
    /// folder) fails under this environment even though the packages themselves are fine. A failed
    /// restore is not fatal here: it is logged and the open is attempted anyway, since an existing cache
    /// that is merely stale rather than foreign can still open successfully.
    /// </summary>
    private async Task RestoreAsync(string entry)
    {
        var dotnet = Environment.ProcessPath is { } p
            && string.Equals(Path.GetFileNameWithoutExtension(p), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? p
            : "dotnet";
        try
        {
            var psi = new ProcessStartInfo(dotnet, ["restore", entry])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // `dotnet restore` spawns PERSISTENT MSBuild worker nodes that inherit these redirected pipes
            // and outlive the parent restore process. Draining both streams concurrently (below) is not
            // enough on its own: WaitForExitAsync returns once the direct child exits, but a lingering
            // node still holds the write end open, so ReadToEndAsync on that pipe never sees EOF and hangs
            // past the drain. Disabling node reuse and the build server keeps every child short-lived so
            // the pipes actually close. (Observed here as WorkspaceHost.LoadAsync hanging for the full
            // GetSolutionAsync timeout inside SampleSolutionFixture, which runs this same restore step
            // through a real WorkspaceHost.)
            psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

            using var proc = Process.Start(psi)!;
            // Both streams must still be drained concurrently, not just read after the fact: `dotnet
            // restore` writes enough to stdout to fill the pipe buffer, and an unread redirected stream
            // blocks the child from writing to it — so reading only stderr would deadlock the restore
            // (and, upstream, the whole load) even with node reuse disabled.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdout, stderr);
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0)
                _log.LogWarning("dotnet restore exited {Code} before workspace load: {Error}", proc.ExitCode, stderr.Result.Trim());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "dotnet restore could not be run before workspace load");
        }
    }

    /// <summary>
    /// Takes the text of documents just written to disk by an applied patch into the live solution.
    ///
    /// Without this the workspace still holds the pre-patch text of a file it just changed, so the very
    /// next patch to that file reads as drifted against disk and is refused. Waiting for the mtime poll
    /// to notice would leave that window open for however long the sweep interval is, and make the
    /// outcome depend on timing.
    /// </summary>
    public void AdoptAppliedText(Solution applied, IReadOnlyList<DocumentId> documents)
    {
        lock (_gate)
        {
            if (_solution is null)
                return;
            foreach (var id in documents)
            {
                // TryGetText is sound here, unlike in the drift check: these documents were just built
                // by WithDocumentText on the fork, so their text is materialised by construction.
                if (applied.GetDocument(id) is { } document && document.TryGetText(out var text))
                    _solution = _solution.WithDocumentText(id, text);
            }
        }
    }

    /// <summary>
    /// Whether the live solution's copy of any of these files is behind disk — the backstop that keeps a
    /// read from presenting drifted content as current. Files the solution does not contain are ignored:
    /// nothing was served from them.
    /// </summary>
    public async Task<bool> IsBehindDiskAsync(IEnumerable<string> absPaths)
    {
        foreach (var abs in absPaths.Distinct(StringComparer.Ordinal))
        {
            Document? document;
            lock (_gate)
            {
                var ids = _solution?.GetDocumentIdsWithFilePath(abs) ?? [];
                document = ids.IsEmpty ? null : _solution!.GetDocument(ids[0]);
            }
            if (document is null)
                continue;

            // GetTextAsync rather than TryGetText. TryGetText returns false when the text is not already
            // materialised, and this is a safety check: a false there is indistinguishable from "matches
            // disk", so the failure mode is silently reporting a drifted file as healthy. MSBuildWorkspace
            // does appear to materialise document text on load, so TryGetText works in practice today —
            // this does not depend on that continuing to hold.
            // A Document is an immutable snapshot, so awaiting outside the lock is safe.
            if (await DiskDrift.DriftedAsync(abs, await document.GetTextAsync()))
                return true;
        }
        return false;
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
        // A change arriving mid-load cannot be applied yet, but dropping it loses it for good: the index
        // has already recorded the file's new mtime, so it will never report that path as changed again
        // and the workspace stays behind disk until something forces a full reload. Hold it instead.
        //
        // This is how a whole commit went missing here: the workspace was reloading after an SDK fix
        // when the edits landed, the events were discarded, and every later read served pre-commit text
        // while reporting itself healthy.
        if (State != WorkspaceState.Loaded)
        {
            lock (_gate)
                foreach (var rel in changedRelPaths)
                    _pendingChanges.Add(rel);
            return;
        }

        if (structural)
        {
            TriggerReload();
            return;
        }

        lock (_gate)
            ApplyChangedPaths(changedRelPaths);
    }

    /// <summary>Re-reads the given paths into the live solution. Caller holds <c>_gate</c>.</summary>
    private void ApplyChangedPaths(IReadOnlyCollection<string> relPaths)
    {
        if (_solution is null)
            return;
        foreach (var rel in relPaths)
        {
            var abs = _locator.AbsPath(rel);
            if (!File.Exists(abs))
                continue;
            foreach (var id in _solution.GetDocumentIdsWithFilePath(abs))
                _solution = _solution.WithDocumentText(id, SourceText.From(File.ReadAllText(abs)));
        }
    }

    /// <summary>
    /// Applies changes that arrived while the workspace was not in a state to take them. A fresh load
    /// already reads current disk content, so this only matters for paths that changed between that read
    /// and the load completing — a window a reload after an out-of-band edit lands in easily.
    /// </summary>
    private void DrainPendingChanges()
    {
        lock (_gate)
        {
            if (_pendingChanges.Count == 0)
                return;
            ApplyChangedPaths(_pendingChanges);
            _pendingChanges.Clear();
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
