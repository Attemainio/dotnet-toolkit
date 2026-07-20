using System.Text.Json;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Telemetry;
using DotnetToolkit.McpServer.Tools;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Proves defaultFormat actually reaches the wire, not just that Formats.Render compiles: a repo with
/// no config.json gets TOON (the new default), and TOON text is structurally NOT JSON — no leading
/// '{', and JsonDocument.Parse rejects it outright. Every other test in this suite pins defaultFormat
/// to "compact" via its fixture's config.json specifically so its own JsonDocument.Parse assertions
/// keep working; this is the one test that deliberately leaves it unset.
/// </summary>
public sealed class OutputFormatTests : IDisposable
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

    public OutputFormatTests()
    {
        _root = Directory.CreateTempSubdirectory("output-format-tests-").FullName;
        File.WriteAllText(Path.Combine(_root, "Foo.cs"),
            "namespace Demo;\n\n/// <summary>A demo type.</summary>\npublic class Foo\n{\n    public int Bar() => 1;\n}\n");

        _locator = new SolutionLocator(NullLogger<SolutionLocator>.Instance, _root);
        _index = new ProjectIndex(_locator, NullLogger<ProjectIndex>.Instance);
        _index.StartInitialization();
        _workspace = new WorkspaceHost(_locator, _index, NullLogger<WorkspaceHost>.Instance);
        _workspace.StartLoading();
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

    [Fact]
    public async Task GetSymbol_DefaultsToToon_WhenNoConfigOverridesIt()
    {
        var result = await ContextTools.GetSymbol(
            _workspace, _locator, _index, _symbols, _featureLog, _builder, _telemetry,
            "Demo.Foo", sessionId: "ses_a", taskId: "tsk_a");

        Assert.False(result.TrimStart().StartsWith('{'));
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(result));
        Assert.Contains("symbolId:", result);
    }
}
