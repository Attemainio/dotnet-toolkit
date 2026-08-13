using System.ComponentModel;
using DotnetToolkit.McpServer.Contracts;
using DotnetToolkit.McpServer.Identity;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Telemetry;
using DotnetToolkit.McpServer.Workspace;
using ModelContextProtocol.Server;

namespace DotnetToolkit.McpServer.Tools;

/// <summary>Retrieval and patch-validation telemetry aggregated for <c>get_retrieval_metrics</c>.</summary>
[McpServerToolType]
public static class MetricsTools
{
    [McpServerTool(Name = "get_retrieval_metrics")]
    [Description("How many tokens has this session used — token usage, cost, call counts and validation "
        + "attempts from this server's own telemetry (spec §17). Answers \"what did that call cost\", "
        + "\"which tool is spending the most tokens\", \"how expensive was this task\", \"how much of the "
        + "budget have I burned\". Computed from raw events only. THIS SERVER PROCESS ONLY, ALWAYS: the raw "
        + "telemetry tables are cleared when the server starts and again when it stops, and no argument "
        + "reads another session - a month of accumulated history distorts the very efficiency numbers this "
        + "exists to report. TASK ids narrow INSIDE that reading and are caller-supplied: pass taskId on a "
        + "tool call, then read just that caller's calls back with taskIds or groupBy:\"task\". That is the "
        + "only way to tell concurrent callers apart, since every agent talking to this server shares its "
        + "one ambient session id. To measure a single call's exact token cost, snapshot with "
        + "groupBy:\"tool\" before and after it and subtract that tool's row. groupBy: "
        + "tool|symbol|level|session|task|none.")]
    public static string GetRetrievalMetrics(
        MetricsReader metrics,
        [Description("One or more caller-supplied task ids to narrow to - the ids passed as taskId on the "
            + "tool calls themselves, each naming one caller inside this session.")] string[]? taskIds = null,
        [Description("Inclusive ISO date lower bound, e.g. \"2026-08-13\" (yyyy-MM-dd only). Rarely needed: "
            + "the reading already covers only this server process's own lifetime.")] string? since = null,
        [Description("Inclusive ISO date upper bound, e.g. \"2026-08-13\" (yyyy-MM-dd only).")] string? until = null,
        [Description("tool | symbol | level | session | task | none (default tool). \"session\" reports this "
            + "session's own id and span in one row; \"task\" does the same per caller-supplied task id. It "
            + "does not affect the harness block, which always carries both its own breakdowns.")] string groupBy = "tool")
    {
        if (since is not null && !DateOnly.TryParseExact(since, "yyyy-MM-dd", out _))
            return Formats.Render(new { error = "invalid_date", detail = $"since must be yyyy-MM-dd, got '{since}'." });
        if (until is not null && !DateOnly.TryParseExact(until, "yyyy-MM-dd", out var untilDay))
            return Formats.Render(new { error = "invalid_date", detail = $"until must be yyyy-MM-dd, got '{until}'." });

        // until is inclusive of that whole day, but created_at carries a full timestamp, so the SQL
        // bound has to be the exclusive start of the NEXT day rather than the bare date string.
        var untilExclusive = until is null ? null : DateOnly.ParseExact(until, "yyyy-MM-dd").AddDays(1).ToString("yyyy-MM-dd");

        var result = metrics.Read(since, untilExclusive, groupBy, taskIds);

        // Absent, not zeroed, when the meter recorded nothing - see MetricsReader.ReadHarness.
        object? harness = result.Harness is null ? null : new
        {
            toolCalls = result.Harness.Totals.ToolCalls,
            requestTokens = result.Harness.Totals.RequestTokens,
            responseTokens = result.Harness.Totals.ResponseTokens,
            tokenEstimator = result.Harness.Estimator,
            byTool = result.Harness.ByTool.Select(g => new
            {
                key = g.Key,
                calls = g.Calls,
                requestTokens = g.RequestTokens,
                responseTokens = g.ResponseTokens,
            }),
            byAgent = result.Harness.ByAgent.Select(g => new
            {
                key = g.Key,
                calls = g.Calls,
                requestTokens = g.RequestTokens,
                responseTokens = g.ResponseTokens,
            }),
        };

        return Formats.Render(new
        {
            totals = new
            {
                toolCalls = result.Totals.ToolCalls,
                tokensReturned = result.Totals.TokensReturned,
                validationAttempts = result.Totals.ValidationAttempts,
                insufficientValidations = result.Totals.InsufficientValidations,
                failedValidations = result.Totals.FailedValidations,
            },
            groups = result.Groups.Select(g => new
            {
                key = g.Key,
                calls = g.Calls,
                tokensReturned = g.TokensReturned,
                firstSeen = g.FirstSeen,
                lastSeen = g.LastSeen,
            }),
            harness,
        });
    }
}
