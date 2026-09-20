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
        _recorder.RecordRetrieval(Sample(Ids.AmbientSession, "tsk_1", "get_symbol", tokens: 120));
        _recorder.RecordRetrieval(Sample(Ids.AmbientSession, "tsk_1", "get_references", tokens: 80));
        _recorder.RecordRetrieval(Sample(Ids.AmbientSession, "tsk_2", "get_symbol", tokens: 200));

        var byTool = _metrics.Read(null, null, "tool");
        Assert.Equal(3, byTool.Totals.ToolCalls);
        Assert.Equal(400, byTool.Totals.TokensReturned);
        Assert.Equal("get_symbol", byTool.Groups[0].Key); // highest token total first
        Assert.Equal(320, byTool.Groups[0].TokensReturned);

        Assert.Empty(_metrics.Read(null, null, "none").Groups);
    }

    // firstSeen/lastSeen let a caller bound its own probe in time.
    [Fact]
    public void GroupByTaskReportsFirstAndLastSeen()
    {
        _recorder.RecordRetrieval(Sample(Ids.AmbientSession, "tsk_1", "get_symbol", tokens: 50));
        _recorder.RecordRetrieval(Sample(Ids.AmbientSession, "tsk_1", "get_references", tokens: 60));

        var group = Assert.Single(_metrics.Read(null, null, "task").Groups, g => g.Key == "tsk_1");
        Assert.Equal(2, group.Calls);
        Assert.Equal(110, group.TokensReturned);
        Assert.NotNull(group.FirstSeen);
        Assert.NotNull(group.LastSeen);
    }

    [Fact]
    public void SinceUntilFiltersByCreatedAtDate()
    {
        _recorder.RecordRetrieval(Sample(Ids.AmbientSession, "tsk_1", "get_symbol", tokens: 30));

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var tomorrow = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");

        var inRange = _metrics.Read(today, tomorrow, "none");
        Assert.True(inRange.Totals.ToolCalls >= 1);

        var outOfRange = _metrics.Read(null, yesterday, "none");
        Assert.Equal(0, outOfRange.Totals.ToolCalls);
    }

    // Conformance C6: UPDATE on a raw telemetry table raises; append succeeds.
    [Fact]
    public void RawTelemetryIsImmutable_C6()
    {
        _recorder.RecordRetrieval(Sample(Ids.AmbientSession, "tsk_1", "get_symbol", tokens: 10));

        using var connection = _store.Connect();
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE retrieval_events SET returned_tokens = 0;";
        var ex = Assert.Throws<SqliteException>(() => update.ExecuteNonQuery());
        Assert.Contains("immutable", ex.Message);

        // Append still succeeds after the rejected update.
        _recorder.RecordRetrieval(Sample(Ids.AmbientSession, "tsk_1", "get_symbol", tokens: 20));
        Assert.Equal(2, _metrics.Read(null, null, "none").Totals.ToolCalls);
    }

    [Fact]
    public void TaskIdsFilterNarrowsToOneCallerWithinASession()
    {
        _recorder.RecordRetrieval(Sample(Ids.AmbientSession, "tsk_probe", "get_symbol", tokens: 100));
        _recorder.RecordRetrieval(Sample(Ids.AmbientSession, "tsk_other", "get_symbol", tokens: 400));

        var result = _metrics.Read(null, null, "tool", ["tsk_probe"]);

        Assert.Equal(1, result.Totals.ToolCalls);
        Assert.Equal(100, result.Totals.TokensReturned);
    }

    [Fact]
    public void GroupByTaskSeparatesCallersSharingOneSessionId()
    {
        _recorder.RecordRetrieval(Sample(Ids.AmbientSession, "tsk_a", "get_symbol", tokens: 100));
        _recorder.RecordRetrieval(Sample(Ids.AmbientSession, "tsk_b", "search_index", tokens: 50));
        _recorder.RecordRetrieval(Sample(Ids.AmbientSession, "tsk_b", "get_scope", tokens: 25));

        var groups = _metrics.Read(null, null, "task").Groups;

        Assert.Equal(2, groups.Count);
        Assert.Equal(100, groups.Single(g => g.Key == "tsk_a").TokensReturned);
        Assert.Equal(75, groups.Single(g => g.Key == "tsk_b").TokensReturned);
        Assert.Equal(2, groups.Single(g => g.Key == "tsk_b").Calls);
    }

    // The reading is this process's calls and nothing else, and no argument widens it. A row left by
    // another session - one the startup purge could not remove because the store failed to open, say -
    // must still stay out of the numbers rather than quietly inflating them.
    [Fact]
    public void ReadingCoversTheAmbientSessionAloneWithNoWayToWidenIt()
    {
        _recorder.RecordRetrieval(Sample(Ids.AmbientSession, "tsk_1", "get_symbol", tokens: 100));
        _recorder.RecordRetrieval(Sample("ses_another_process", "tsk_1", "get_symbol", tokens: 400));

        var current = _metrics.Read(null, null, "none");

        Assert.Equal(1, current.Totals.ToolCalls);
        Assert.Equal(100, current.Totals.TokensReturned);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnattributedCallFallsBackToTheAmbientSession(string? supplied)
    {
        Assert.Equal(Ids.AmbientSession, Ids.TaskId(supplied));
    }

    [Fact]
    public void SuppliedTaskIdIsTrimmedRatherThanUsedVerbatim()
    {
        Assert.Equal("tsk_probe", Ids.TaskId("  tsk_probe  "));
    }

    private static RetrievalEvent Sample(string session, string task, string tool, int tokens,
        string? symbolId = null, string? toolCallId = null) =>
        new()
        {
            ToolCallId = toolCallId ?? Ids.ToolCall(),
            SessionId = session,
            TaskId = task,
            ToolName = tool,
            SymbolId = symbolId,
            ReturnedTokens = tokens,
        };

    /// <summary>Both directions of a metered call survive the round trip, and stay apart.</summary>
    /// <remarks>
    /// Apart because they are priced differently: the request is output tokens (the model had to generate
    /// it), the response is input tokens (it was loaded into context). One summed number would average
    /// the dear half into the cheap one and understate what a chatty tool really costs.
    /// </remarks>
    [Fact]
    public void MeteredToolCallsReportBothDirectionsSeparately()
    {
        _recorder.RecordToolCall(SampleToolCall("toolu_1", "Grep", request: 40, response: 900));
        _recorder.RecordToolCall(SampleToolCall("toolu_2", "Read", request: 30, response: 4000));

        var harness = _metrics.Read(null, null, "tool").Harness;

        Assert.NotNull(harness);
        Assert.Equal(2, harness.Totals.ToolCalls);
        Assert.Equal(70, harness.Totals.RequestTokens);
        Assert.Equal(4900, harness.Totals.ResponseTokens);
        Assert.Equal("chars4", harness.Estimator);
        Assert.Equal(4000, Assert.Single(harness.ByTool, g => g.Key == "Read").ResponseTokens);
    }

    // The meter is the only instrument that sees both routes, so the axis that matters is which agent a
    // call ran in - that is what separates the MCP probe from the raw one. A call from the main thread
    // carries no agent_type at all, and must not fall out of the breakdown for it.
    [Fact]
    public void MeteredCallsGroupByAgentWithTheMainThreadNamed()
    {
        _recorder.RecordToolCall(SampleToolCall("toolu_1", "Grep", 40, 900, "dotnet-perf-raw-probe"));
        _recorder.RecordToolCall(SampleToolCall("toolu_2", "get_symbol", 50, 300, "dotnet-perf-mcp-probe"));
        _recorder.RecordToolCall(SampleToolCall("toolu_3", "Bash", 20, 10));

        var byAgent = _metrics.Read(null, null, "tool").Harness!.ByAgent;

        Assert.Equal(3, byAgent.Count);
        Assert.Equal(900, byAgent.Single(g => g.Key == "dotnet-perf-raw-probe").ResponseTokens);
        Assert.Equal(300, byAgent.Single(g => g.Key == "dotnet-perf-mcp-probe").ResponseTokens);
        Assert.Equal(10, byAgent.Single(g => g.Key == "(main thread)").ResponseTokens);
    }

    [Fact]
    public void MeteredCallsGroupByAgentAndTool()
    {
        _recorder.RecordToolCall(SampleToolCall("toolu_1", "Grep", 40, 900, "dotnet-perf-raw-probe"));
        _recorder.RecordToolCall(SampleToolCall("toolu_2", "Read", 30, 400, "dotnet-perf-raw-probe"));
        _recorder.RecordToolCall(SampleToolCall("toolu_3", "get_symbol", 50, 300, "dotnet-perf-mcp-probe"));
        _recorder.RecordToolCall(SampleToolCall("toolu_4", "Bash", 20, 10));

        var byAgentTool = _metrics.Read(null, null, "tool").Harness!.ByAgentTool;

        Assert.Equal(3, byAgentTool.Count);
        var rawProbe = byAgentTool.Single(a => a.Agent == "dotnet-perf-raw-probe");
        Assert.Equal(2, rawProbe.Calls);
        Assert.Equal(1300, rawProbe.ResponseTokens);
        Assert.Equal(2, rawProbe.ByTool.Count);
        Assert.Equal(900, rawProbe.ByTool.Single(g => g.Key == "Grep").ResponseTokens);
        Assert.Equal(400, rawProbe.ByTool.Single(g => g.Key == "Read").ResponseTokens);

        var mainThread = byAgentTool.Single(a => a.Agent == "(main thread)");
        Assert.Equal(1, mainThread.Calls);
        Assert.Equal(10, mainThread.ResponseTokens);
    }

    /// <summary>
    /// The meter counts whole-file .cs reads per agent and per tool, so a benchmark can say from the
    /// instrument that a probe stayed off raw reads, instead of proving it by forcing the guards to deny.
    /// </summary>
    [Fact]
    public void MeteredWholeFileReadsAreCountedPerAgentAndTool()
    {
        _recorder.RecordToolCall(SampleToolCall("toolu_1", "Read", 30, 4000, "dotnet-perf-raw-probe") with { WholeFileCsRead = true });
        _recorder.RecordToolCall(SampleToolCall("toolu_2", "Read", 30, 400, "dotnet-perf-raw-probe"));
        _recorder.RecordToolCall(SampleToolCall("toolu_3", "Bash", 20, 3000, "dotnet-perf-raw-probe") with { WholeFileCsRead = true });
        _recorder.RecordToolCall(SampleToolCall("toolu_4", "get_symbol", 50, 300, "dotnet-perf-mcp-probe"));

        var byAgentTool = _metrics.Read(null, null, "tool").Harness!.ByAgentTool;

        var rawProbe = byAgentTool.Single(a => a.Agent == "dotnet-perf-raw-probe");
        Assert.Equal(2, rawProbe.WholeFileCsReads);
        Assert.Equal(1, rawProbe.ByTool.Single(g => g.Key == "Read").WholeFileCsReads);
        Assert.Equal(1, rawProbe.ByTool.Single(g => g.Key == "Bash").WholeFileCsReads);
        Assert.Equal(0, byAgentTool.Single(a => a.Agent == "dotnet-perf-mcp-probe").WholeFileCsReads);
    }

    [Fact]
    public void MeteredCallsGroupByHarnessAndModelIncludingLocalModels()
    {
        _recorder.RecordToolCall(SampleToolCall(
            "toolu_1", "apply_patch", 40, 90, harness: "codex", model: "local/qwen3-coder"));
        _recorder.RecordToolCall(SampleToolCall(
            "toolu_2", "Read", 30, 400, harness: "claude-code", model: "claude-sonnet"));

        var harness = _metrics.Read(null, null, "tool").Harness!;

        Assert.Equal(90, harness.ByHarness.Single(group => group.Key == "codex").ResponseTokens);
        Assert.Equal(400, harness.ByHarness.Single(group => group.Key == "claude-code").ResponseTokens);
        Assert.Equal(90, harness.ByModel.Single(group => group.Key == "local/qwen3-coder").ResponseTokens);
    }

    // PostToolUse can be delivered more than once for one call. tool_use_id is UNIQUE and the insert
    // ignores a conflict, so a redelivery is a no-op rather than a doubled cost.
    [Fact]
    public void ReMeteringTheSameToolUseIdIsIgnored()
    {
        _recorder.RecordToolCall(SampleToolCall("toolu_1", "Grep", request: 40, response: 900));
        _recorder.RecordToolCall(SampleToolCall("toolu_1", "Grep", request: 40, response: 900));

        var harness = _metrics.Read(null, null, "tool").Harness;

        Assert.NotNull(harness);
        Assert.Equal(1, harness.Totals.ToolCalls);
        Assert.Equal(900, harness.Totals.ResponseTokens);
    }

    // Absent, not zeroed: "the meter recorded nothing" and "the tools cost nothing" are different
    // claims, and a zeroed block would assert the second while meaning the first.
    [Fact]
    public void HarnessBlockIsAbsentWhenNothingWasMetered()
    {
        _recorder.RecordRetrieval(Sample(Ids.AmbientSession, "tsk_1", "get_symbol", tokens: 100));

        Assert.Null(_metrics.Read(null, null, "tool").Harness);
    }

    // A hook cannot know a caller-supplied task id, so metered rows carry none. Returning them
    // unfiltered beside task-filtered retrieval numbers would invite a comparison between two
    // differently scoped figures, so the block is withheld instead.
    [Fact]
    public void HarnessBlockIsWithheldWhenNarrowedToATask()
    {
        _recorder.RecordToolCall(SampleToolCall("toolu_1", "Grep", request: 40, response: 900));

        Assert.Null(_metrics.Read(null, null, "tool", ["tsk_1"]).Harness);
        Assert.NotNull(_metrics.Read(null, null, "tool").Harness);
    }

    private static TelemetryRecorder.ToolCallEvent SampleToolCall(
        string toolUseId,
        string tool,
        int request,
        int response,
        string? agentType = null,
        string? harness = null,
        string? model = null) =>
        new()
        {
            ToolName = tool,
            ToolUseId = toolUseId,
            RequestTokens = request,
            ResponseTokens = response,
            TokenEstimator = "chars4",
            AgentType = agentType,
            Harness = harness,
            Model = model,
        };
}
