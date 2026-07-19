using DotnetToolkit.McpServer.Identity;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Telemetry;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

public sealed class AttributionTests : IDisposable
{
    private readonly string _root;
    private readonly KnowledgeStore _store;
    private readonly TelemetryRecorder _recorder;
    private readonly AttributionJob _job;

    public AttributionTests()
    {
        _root = Directory.CreateTempSubdirectory("attribution-tests-").FullName;
        var locator = new SolutionLocator(NullLogger<SolutionLocator>.Instance, _root);
        _store = new KnowledgeStore(locator, NullLogger<KnowledgeStore>.Instance);
        _recorder = new TelemetryRecorder(_store, NullLogger<TelemetryRecorder>.Instance);
        _job = new AttributionJob(_store);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private void Record(string task, string? symbolId, int tokens, bool leaseHit = false, bool refetch = false,
        string? version = "decl:aaaa") =>
        _recorder.RecordRetrieval(new RetrievalEvent
        {
            ToolCallId = Ids.ToolCall(),
            SessionId = "ses_a",
            TaskId = task,
            ToolName = "get_symbol",
            SymbolId = symbolId,
            ContentVersion = version,
            LeaseHit = leaseHit,
            Refetch = refetch,
            ReturnedTokens = tokens,
        });

    private (string Verdict, long Tokens)? VerdictFor(string symbolId)
    {
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT a.verdict, e.returned_tokens
            FROM derived_retrieval_attribution a
            JOIN retrieval_events e ON e.event_id = a.event_id
            WHERE e.symbol_id = $s LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$s", symbolId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetInt64(1)) : null;
    }

    private int RowCount(string table)
    {
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    // Conformance C9: rebuilding derived tables from raw events is idempotent for a fixed version.
    [Fact]
    public void RebuildIsIdempotentForFixedVersion_C9()
    {
        Record("tsk_1", "sym_a", 100);
        Record("tsk_1", "sym_a", 100);   // plain reread
        Record("tsk_1", "sym_b", 50);

        var first = _job.RebuildForTask("tsk_1");
        var attributionsAfterFirst = RowCount("derived_retrieval_attribution");
        var summariesAfterFirst = RowCount("derived_task_summary");

        var second = _job.RebuildForTask("tsk_1");

        Assert.Equal(first, second);
        Assert.Equal(attributionsAfterFirst, RowCount("derived_retrieval_attribution"));
        Assert.Equal(summariesAfterFirst, RowCount("derived_task_summary"));
        Assert.Equal(1, summariesAfterFirst); // one summary per task, not one per rebuild
    }

    [Fact]
    public void PlainRereadIsWaste_ButLeaseHitAndRefetchAreNot()
    {
        Record("tsk_r", "sym_plain", 80);
        Record("tsk_r", "sym_plain", 80);                    // same version, no lease → waste
        Record("tsk_r", "sym_leased", 40);
        Record("tsk_r", "sym_leased", 40, leaseHit: true);   // correct behaviour
        Record("tsk_r", "sym_compact", 60);
        Record("tsk_r", "sym_compact", 60, refetch: true);   // justified after compaction

        _job.RebuildForTask("tsk_r");

        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM derived_retrieval_attribution WHERE verdict = 'wasted_reread';
            """;
        // Only the plain repeat counts as waste; the leased and refetched repeats do not.
        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar() ?? 0));
    }

    [Fact]
    public void SummaryPartitionsTokensAcrossVerdicts()
    {
        Record("tsk_s", "sym_x", 100);
        Record("tsk_s", "sym_y", 25);

        _job.RebuildForTask("tsk_s");

        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT total_tokens, tokens_contributing + tokens_navigational + tokens_unused + tokens_wasted_rereads,
                   attribution_version
            FROM derived_task_summary WHERE task_id = 'tsk_s';
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        // Every token lands in exactly one verdict bucket.
        Assert.Equal(reader.GetInt64(0), reader.GetInt64(1));
        Assert.Equal(125, reader.GetInt64(0));
        Assert.Equal("attr-v1", reader.GetString(2));
    }

    [Fact]
    public void EmptyTaskProducesNothing() => Assert.Equal(0, _job.RebuildForTask("tsk_missing"));
}
