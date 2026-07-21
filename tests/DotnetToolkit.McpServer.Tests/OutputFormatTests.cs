using System.Text.Json;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Telemetry;
using DotnetToolkit.McpServer.Tools;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Proves Formats.Current actually reaches the wire, not just that Formats.Render compiles: toon text is
/// structurally NOT JSON (no leading '{', JsonDocument.Parse rejects it outright), and set_output_format
/// changes what subsequent calls return without a restart. Formats.Current is a single process-wide
/// static, so every fixture in this suite (including this one) pins it explicitly in its own
/// constructor — nothing here can assume what a previous test class left it as, since
/// DisableTestParallelization only guarantees sequential execution, not a fixed class order.
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
        Formats.Current = OutputFormat.Toon;
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
    public async Task GetSymbol_IsToon_WhenFormatsCurrentIsToon()
    {
        var result = await ContextTools.GetSymbol(
            _workspace, _locator, _index, _symbols, _featureLog, _builder, _telemetry,
            "Demo.Foo", sessionId: "ses_a", taskId: "tsk_a");

        Assert.False(result.TrimStart().StartsWith('{'));
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(result));
        Assert.Contains("symbolId:", result);
    }

    [Fact]
    public async Task SetOutputFormat_SwitchesToJson_AndSubsequentCallsReflectIt()
    {
        var confirmation = ServerTools.SetOutputFormat("json");
        Assert.Equal("output format set to json", confirmation);

        var result = await ContextTools.GetSymbol(
            _workspace, _locator, _index, _symbols, _featureLog, _builder, _telemetry,
            "Demo.Foo", sessionId: "ses_a", taskId: "tsk_a");

        // "json" is pretty-printed (WriteIndented), unlike "compact" — the two are otherwise the same data.
        Assert.Contains('\n', result);
        var root = JsonDocument.Parse(result).RootElement;
        Assert.True(root.TryGetProperty("contentVersion", out _));
    }

    [Fact]
    public void SetOutputFormat_RejectsAnUnknownFormat_WithoutChangingCurrent()
    {
        Formats.Current = OutputFormat.Compact;
        var confirmation = ServerTools.SetOutputFormat("yaml");

        Assert.Equal("unknown format: yaml (use json|compact|toon)", confirmation);
        Assert.Equal(OutputFormat.Compact, Formats.Current);
    }
}
