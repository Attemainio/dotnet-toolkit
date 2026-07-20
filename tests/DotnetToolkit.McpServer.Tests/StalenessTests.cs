using System.Text.Json;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Telemetry;
using DotnetToolkit.McpServer.Tools;
using DotnetToolkit.McpServer.Validation;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Conformance C11 without MSBuild: with no solution to load, the workspace never becomes ready, so
/// index-backed responses must carry <c>limitedBy: index_only</c> while live-only tools refuse with
/// <c>workspace_loading</c>.
/// </summary>
public sealed class StalenessTests : IDisposable
{
    private readonly string _root;
    private readonly SolutionLocator _locator;
    private readonly ProjectIndex _index;
    private readonly WorkspaceHost _workspace;
    private readonly KnowledgeStore _store;
    private readonly SymbolStore _symbols;
    private readonly FeatureLogStore _featureLog;
    private readonly SymbolIndexBuilder _builder;
    private readonly TelemetryRecorder _telemetry;

    public StalenessTests()
    {
        _root = Directory.CreateTempSubdirectory("staleness-tests-").FullName;
        File.WriteAllText(Path.Combine(_root, "Foo.cs"),
            "namespace Demo;\n\n/// <summary>A demo type.</summary>\npublic class Foo\n{\n    public int Bar() => 1;\n}\n");

        _locator = new SolutionLocator(NullLogger<SolutionLocator>.Instance, _root);
        _index = new ProjectIndex(_locator, NullLogger<ProjectIndex>.Instance);
        _index.StartInitialization();
        _workspace = new WorkspaceHost(_locator, _index, NullLogger<WorkspaceHost>.Instance);
        _workspace.StartLoading(); // no solution → resolves to NoSolution, GetSolutionAsync returns null
        _store = new KnowledgeStore(_locator, NullLogger<KnowledgeStore>.Instance);
        _symbols = new SymbolStore(_store);
        _featureLog = new FeatureLogStore(_store);
        _builder = new SymbolIndexBuilder(_workspace, _symbols, NullLogger<SymbolIndexBuilder>.Instance);
        _telemetry = new TelemetryRecorder(_store, NullLogger<TelemetryRecorder>.Instance);
    }

    public void Dispose()
    {
        _workspace.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task GetSymbol_IsIndexBackedWhenWorkspaceUnavailable_C11()
    {
        var root = Root(await ContextTools.GetSymbol(
            _workspace, _locator, _index, _symbols, _featureLog, _builder, _telemetry,
            "Demo.Foo", sessionId: "ses_a", taskId: "tsk_a"));

        Assert.Equal("index_only", root.GetProperty("limitedBy").GetString());
        Assert.True(root.TryGetProperty("content", out _));
        Assert.Equal("Type", root.GetProperty("content").GetProperty("kind").GetString());
        Assert.StartsWith("decl:", root.GetProperty("contentVersion").GetString());
    }

    [Fact]
    public async Task GetReferences_IsLiveOnlyAndRefuses_C11()
    {
        var root = Root(await ContextTools.GetReferences(
            _workspace, _locator, _symbols, _telemetry, "Demo.Foo", "callers", sessionId: "ses_a", taskId: "tsk_a"));

        Assert.Equal("workspace_loading", root.GetProperty("error").GetString());
    }

    // Conformance C8: applyOnSuccess without an intent is rejected before any validation runs.
    [Fact]
    public async Task ValidatePatch_ApplyWithoutIntent_Rejected_C8()
    {
        var edits = new[] { new PatchEditInput("Foo.cs", 4, 6, "public class Foo { }") };
        var root = Root(await PatchTools.ValidatePatch(
            _workspace, _locator, _symbols, _featureLog, _builder,
            new TargetedTests(_locator, NullLogger<TargetedTests>.Instance), _telemetry,
            new Dictionary<string, string>(), edits, sessionId: "ses_a", taskId: "tsk_a",
            requestedLevel: null, applyOnSuccess: true, intent: null, tags: null));

        Assert.Equal("intent_required", root.GetProperty("error").GetString());
    }
}
