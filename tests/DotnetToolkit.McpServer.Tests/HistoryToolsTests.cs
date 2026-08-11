using System.Diagnostics;
using System.Text.Json;
using DotnetToolkit.McpServer.Git;
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
/// HistoryTools.GetSemanticDiff over a real (tiny) git repo in /tmp, mirroring SemanticDiffTests'
/// fixture but exercising the MCP tool wrapper itself (ref resolution, error shapes, JSON rendering).
/// </summary>
public sealed class HistoryToolsGetSemanticDiffTests : IAsyncLifetime
{
    private string _root = "";
    private GitAnalyzer _git = null!;
    private SemanticDiff _diff = null!;
    private TelemetryRecorder _telemetry = null!;

    private const string Original = """
        namespace Demo;

        public class Calc
        {
            public int Add(int a, int b) => a + b;
        }
        """;

    public async ValueTask InitializeAsync()
    {
        // Parsed as plain JSON below; pin regardless of another test's process-wide Formats.Current.
        Formats.Current = OutputFormat.Compact;

        _root = Path.Combine(Path.GetTempPath(), "dt-history-git-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        var store = new KnowledgeStore(
            new SolutionLocator(NullLogger<SolutionLocator>.Instance, _root),
            NullLogger<KnowledgeStore>.Instance);
        _telemetry = new TelemetryRecorder(store, NullLogger<TelemetryRecorder>.Instance);

        await Git("init", "-q");
        await Git("config", "user.email", "test@example.com");
        await Git("config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(_root, "Calc.cs"), Original);
        await Git("add", ".");
        await Git("commit", "-q", "-m", "initial");

        var locator = new SolutionLocator(NullLogger<SolutionLocator>.Instance, _root);
        _git = new GitAnalyzer(locator, NullLogger<GitAnalyzer>.Instance);
        _diff = new SemanticDiff(_git);
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task BodyChange_ReportsNonBreakingChangeAndImpactSummary()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Calc.cs"), Original.Replace("=> a + b;", "=> b + a;"));
        await Git("add", ".");
        await Git("commit", "-q", "-m", "reorder addition");

        var root = Root(await HistoryTools.GetSemanticDiff(_git, _diff, _telemetry, "HEAD~1", "HEAD"));

        var changed = Assert.Single(root.GetProperty("symbolsChanged").EnumerateArray());
        Assert.Equal("non-breaking", changed.GetProperty("apiImpact").GetString());
        Assert.Equal(0, root.GetProperty("apiImpactSummary").GetProperty("breaking").GetInt32());
        Assert.Equal(1, root.GetProperty("apiImpactSummary").GetProperty("nonBreaking").GetInt32());
    }

    [Fact]
    public async Task UnresolvableRef_ReportsUnresolvedRefError()
    {
        var root = Root(await HistoryTools.GetSemanticDiff(_git, _diff, _telemetry, "does-not-exist", "HEAD"));

        Assert.Equal("unresolved_ref", root.GetProperty("error").GetString());
        }

        [Fact]
        public async Task RepoTypo_SurfacesDidYouMean()
        {
            var repoName = Path.GetFileName(_root);
            var typo = repoName[..^1];
            var root = Root(await HistoryTools.GetSemanticDiff(_git, _diff, _telemetry, repo: typo));

            Assert.Equal("unknown_repository", root.GetProperty("error").GetString());
            Assert.Equal(repoName, root.GetProperty("didYouMean").GetString());
        }

    private async Task Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdout, stderr);
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {await stderr}");
    }
}

/// <summary>HistoryTools.SearchLog over a real SQLite-backed FeatureLogStore, no MSBuild needed.</summary>
public sealed class HistoryToolsSearchLogTests : IDisposable
{
    private readonly string _root;
    private readonly FeatureLogStore _featureLog;
    private readonly TelemetryRecorder _telemetry;

    public HistoryToolsSearchLogTests()
    {
        // Parsed as plain JSON below; pin regardless of another test's process-wide Formats.Current.
        Formats.Current = OutputFormat.Compact;

        _root = Directory.CreateTempSubdirectory("history-search-log-tests-").FullName;
        var locator = new SolutionLocator(NullLogger<SolutionLocator>.Instance, _root);
        var store = new KnowledgeStore(locator, NullLogger<KnowledgeStore>.Instance);
        _featureLog = new FeatureLogStore(store);
        _telemetry = new TelemetryRecorder(store, NullLogger<TelemetryRecorder>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void QueryMatchingIntent_ReturnsTheEntry()
    {
        _featureLog.Append(new FeatureLogStore.LogEntry(
            "tsk_a", null, null, "fixed decimal rounding in price calculation", [], null, []));
        _featureLog.Append(new FeatureLogStore.LogEntry(
            "tsk_b", null, null, "renamed OrderService to OrderProcessor", [], null, []));

        var root = Root(HistoryTools.SearchLog(_featureLog, _telemetry, "decimal rounding"));

        var item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Contains("decimal rounding", item.GetProperty("intent").GetString());
    }

    [Fact]
    public void NoQuery_ReturnsMostRecentEntriesUpToLimit()
    {
        for (var i = 0; i < 3; i++)
            _featureLog.Append(new FeatureLogStore.LogEntry($"tsk_{i}", null, null, $"change {i}", [], null, []));

        var root = Root(HistoryTools.SearchLog(_featureLog, _telemetry, query: null, limit: 2));

        Assert.Equal(2, root.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData("rounding decimal")]      // reversed order
    [InlineData("decimal price")]         // non-adjacent words
    [InlineData("price rounding decimal")] // three terms, none adjacent, none in order
    public void EveryTermMatchesInAnyOrder_NotOnlyAsOneAdjacentPhrase(string query)
    {
        _featureLog.Append(new FeatureLogStore.LogEntry(
            "tsk_a", null, null, "fixed decimal rounding in price calculation", [], null, []));

        var root = Root(HistoryTools.SearchLog(_featureLog, _telemetry, query));

        var item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Contains("decimal rounding", item.GetProperty("intent").GetString());
    }

    [Fact]
    public void EveryTermMustMatch_SoAnExtraUnrelatedTermNarrowsToNothing()
    {
        _featureLog.Append(new FeatureLogStore.LogEntry(
            "tsk_a", null, null, "fixed decimal rounding in price calculation", [], null, []));

        var root = Root(HistoryTools.SearchLog(_featureLog, _telemetry, "decimal telemetry"));

        Assert.Empty(root.GetProperty("items").EnumerateArray());
    }
}
