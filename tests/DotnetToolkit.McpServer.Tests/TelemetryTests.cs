using DotnetToolkit.McpServer.Identity;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Telemetry;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

public sealed class TelemetryTests : IDisposable
{
    private readonly string _root;
    private readonly KnowledgeStore _store;
    private readonly TelemetryRecorder _recorder;
    private readonly MetricsReader _metrics;

    public TelemetryTests()
    {
        _root = Directory.CreateTempSubdirectory("telemetry-tests-").FullName;
        var locator = new SolutionLocator(NullLogger<SolutionLocator>.Instance, _root);
        _store = new KnowledgeStore(locator, NullLogger<KnowledgeStore>.Instance);
        _recorder = new TelemetryRecorder(_store, NullLogger<TelemetryRecorder>.Instance);
        _metrics = new MetricsReader(_store);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void StoreOpensAndRunsMigrations()
    {
        Assert.True(_store.Available);
        Assert.True(File.Exists(_store.DatabasePath));
    }

    [Fact]
    public void RecordedEventAggregatesIntoMetrics()
    {
        _recorder.RecordRetrieval(Sample("ses_a", "tsk_1", "get_symbol", tokens: 120));
        _recorder.RecordRetrieval(Sample("ses_a", "tsk_1", "get_references", tokens: 80));
        _recorder.RecordRetrieval(Sample("ses_b", "tsk_2", "get_symbol", tokens: 200));

        var global = _metrics.Read("global", null, null, null, "tool");
        Assert.Equal(3, global.Totals.ToolCalls);
        Assert.Equal(400, global.Totals.TokensReturned);
        Assert.Equal("get_symbol", global.Groups[0].Key); // highest token total first
        Assert.Equal(320, global.Groups[0].TokensReturned);

        var session = _metrics.Read("session", ["ses_a"], null, null, "none");
        Assert.Equal(2, session.Totals.ToolCalls);
        Assert.Equal(200, session.Totals.TokensReturned);
        Assert.Empty(session.Groups);

        // Merging several session ids together is the point of sessionIds being an array: a caller
        // discovers past ids via groupBy:"session" and feeds them back in here to combine them.
        var merged = _metrics.Read("session", ["ses_a", "ses_b"], null, null, "none");
        Assert.Equal(3, merged.Totals.ToolCalls);
        Assert.Equal(400, merged.Totals.TokensReturned);
    }

    [Fact]
    public void GroupBySessionReportsFirstAndLastSeen()
    {
        _recorder.RecordRetrieval(Sample("ses_x", "tsk_1", "get_symbol", tokens: 50));
        _recorder.RecordPatch(SamplePatch("ses_x", "tsk_1", tokens: 60));

        var bySession = _metrics.Read("global", null, null, null, "session");
        var group = Assert.Single(bySession.Groups, g => g.Key == "ses_x");
        Assert.Equal(2, group.Calls);
        Assert.Equal(110, group.TokensReturned);
        Assert.NotNull(group.FirstSeen);
        Assert.NotNull(group.LastSeen);
    }

    [Fact]
    public void SinceUntilFiltersByCreatedAtDate()
    {
        _recorder.RecordRetrieval(Sample("ses_y", "tsk_1", "get_symbol", tokens: 30));

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var tomorrow = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");

        var inRange = _metrics.Read("global", null, today, tomorrow, "none");
        Assert.True(inRange.Totals.ToolCalls >= 1);

        var outOfRange = _metrics.Read("global", null, null, yesterday, "none");
        Assert.Equal(0, outOfRange.Totals.ToolCalls);
    }

    [Fact]
    public void RepeatFetchWithoutLeaseIsFlagged()
    {
        _recorder.RecordRetrieval(Sample("ses_a", "tsk_1", "get_symbol", tokens: 100, symbolId: "sym_abc"));
        _recorder.RecordRetrieval(Sample("ses_a", "tsk_1", "get_symbol", tokens: 100, symbolId: "sym_abc"));

        var flags = _metrics.Read("global", null, null, null, "none").Flags;
        var flag = Assert.Single(flags);
        Assert.Equal("repeat_fetch_without_lease", flag.Kind);
        Assert.Equal("sym_abc", flag.SymbolId);
        Assert.Equal(2, flag.Count);
    }

    // Conformance C6: UPDATE on a raw telemetry table raises; append succeeds.
    [Fact]
    public void RawTelemetryIsImmutable_C6()
    {
        _recorder.RecordRetrieval(Sample("ses_a", "tsk_1", "get_symbol", tokens: 10));

        using var connection = _store.Connect();
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE retrieval_events SET returned_tokens = 0;";
        var ex = Assert.Throws<SqliteException>(() => update.ExecuteNonQuery());
        Assert.Contains("immutable", ex.Message);

        // Append still succeeds after the rejected update.
        _recorder.RecordRetrieval(Sample("ses_a", "tsk_1", "get_symbol", tokens: 20));
        Assert.Equal(2, _metrics.Read("global", null, null, null, "none").Totals.ToolCalls);
    }

    private static RetrievalEvent Sample(string session, string task, string tool, int tokens, string? symbolId = null) =>
        new()
        {
            ToolCallId = Ids.ToolCall(),
            SessionId = session,
            TaskId = task,
            ToolName = tool,
            SymbolId = symbolId,
            ReturnedTokens = tokens,
        };

    // Regression for the bug the ultrareview surfaced: validate_patch has no retrieval_events row
    // (it writes patch_events via RecordPatch instead), so its calls/tokens were silently absent
    // from every total and never appeared in any groupBy:tool bucket.
    [Fact]
    public void PatchEventsFoldIntoTotalsAndToolGroup()
    {
        _recorder.RecordRetrieval(Sample("ses_a", "tsk_1", "get_symbol", tokens: 100));
        _recorder.RecordPatch(SamplePatch("ses_a", "tsk_1", tokens: 150));

        var global = _metrics.Read("global", null, null, null, "tool");
        Assert.Equal(2, global.Totals.ToolCalls);
        Assert.Equal(250, global.Totals.TokensReturned);
        Assert.Equal(1, global.Totals.ValidationAttempts);

        var patchGroup = Assert.Single(global.Groups, g => g.Key == "validate_patch");
        Assert.Equal(1, patchGroup.Calls);
        Assert.Equal(150, patchGroup.TokensReturned);
    }

    private static TelemetryRecorder.PatchEvent SamplePatch(string session, string task, int tokens) =>
        new()
        {
            ToolCallId = Ids.ToolCall(),
            PatchId = Ids.ToolCall(),
            ValidationAttemptId = Ids.ToolCall(),
            SessionId = session,
            TaskId = task,
            ChangedSymbolIdsJson = "[]",
            ChangeKindsJson = "[]",
            BaseVersionsJson = "{}",
            CompletedLevel = "parse",
            RequiredLevel = "parse",
            IsSufficient = true,
            Succeeded = true,
            Applied = false,
            ReturnedTokens = tokens,
        };
}
