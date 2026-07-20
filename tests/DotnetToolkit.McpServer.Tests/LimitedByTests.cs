using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Tools;
using DotnetToolkit.McpServer.Telemetry;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// The per-response tier marker. It tracked "not ready yet" but not "ready and broken", which is the
/// state that actually misleads: a workspace that loaded with failed projects has a semantic model
/// missing whatever those projects contribute, so answers are quietly wrong rather than absent.
///
/// Observed on this repo — the launcher's SDK and the SDK that restored obj/ differed by a feature
/// band, every project failed ResolvePackageAssets, and every response still reported the healthy
/// case because the index builder had completed a pass over the broken model.
/// </summary>
public sealed class LimitedByTests : IDisposable
{
    private readonly string _root;
    private readonly SolutionLocator _locator;
    private readonly ProjectIndex _index;
    private readonly WorkspaceHost _workspace;
    private readonly KnowledgeStore _store;
    private readonly SymbolStore _symbols;
    private readonly TelemetryRecorder _telemetry;

    public LimitedByTests()
    {
        _root = Directory.CreateTempSubdirectory("limited-by-").FullName;
        _locator = new SolutionLocator(NullLogger<SolutionLocator>.Instance, _root);
        _index = new ProjectIndex(_locator, NullLogger<ProjectIndex>.Instance);
        _workspace = new WorkspaceHost(_locator, _index, NullLogger<WorkspaceHost>.Instance);
        _store = new KnowledgeStore(_locator, NullLogger<KnowledgeStore>.Instance);
        _symbols = new SymbolStore(_store);
        _telemetry = new TelemetryRecorder(_store, NullLogger<TelemetryRecorder>.Instance);
    }

    public void Dispose()
    {
        _workspace.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// A workspace that never loaded is not degraded — it is honestly unavailable, and the caller is
    /// already told that by workspace_loading. Degraded is reserved for a load that half-succeeded.
    /// </summary>
    [Fact]
    public void AWorkspaceThatNeverLoadedIsNotDegraded()
    {
        Assert.False(_workspace.IsDegraded);

        _workspace.StartLoading(); // no solution in the temp root → NoSolution, not Loaded
        Assert.False(_workspace.IsDegraded);
    }

    /// <summary>
    /// search_index answers from the store, which a degraded pass can fill with wrong rows — a
    /// populated store is necessary but not sufficient for the healthy marker.
    /// </summary>
    [Fact]
    public async Task SearchIndexReportsIndexOnlyWhileTheStoreIsEmpty()
    {
        var json = await ContextTools.SearchIndex(_symbols, _index, _workspace, _telemetry, "Anything");
        var root = System.Text.Json.JsonDocument.Parse(json).RootElement;

        Assert.Equal("index_only", root.GetProperty("limitedBy").GetString());
    }
}
