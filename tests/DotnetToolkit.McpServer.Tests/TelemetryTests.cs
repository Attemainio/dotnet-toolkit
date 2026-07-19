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

        var global = _metrics.Read("global", null, null, "tool");
        Assert.Equal(3, global.Totals.ToolCalls);
        Assert.Equal(400, global.Totals.TokensReturned);
        Assert.Equal("get_symbol", global.Groups[0].Key); // highest token total first
        Assert.Equal(320, global.Groups[0].TokensReturned);

        var session = _metrics.Read("session", "ses_a", null, "none");
        Assert.Equal(2, session.Totals.ToolCalls);
        Assert.Equal(200, session.Totals.TokensReturned);
        Assert.Empty(session.Groups);
    }

    [Fact]
    public void RepeatFetchWithoutLeaseIsFlagged()
    {
        _recorder.RecordRetrieval(Sample("ses_a", "tsk_1", "get_symbol", tokens: 100, symbolId: "sym_abc"));
        _recorder.RecordRetrieval(Sample("ses_a", "tsk_1", "get_symbol", tokens: 100, symbolId: "sym_abc"));

        var flags = _metrics.Read("global", null, null, "none").Flags;
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
        Assert.Equal(2, _metrics.Read("global", null, null, "none").Totals.ToolCalls);
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
}
