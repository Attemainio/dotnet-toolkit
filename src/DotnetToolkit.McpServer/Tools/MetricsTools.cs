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
    [Description("Self-observation over this server's own telemetry (spec §17) — how many tokens this session has used: token totals and "
        + "validation attempts. Computed from raw "
        + "events only. scope: session|global; groupBy: tool|symbol|level|session|task|none. "
        + "Session ids are not caller-supplied - every call in this server process shares one ambient id "
        + "automatically, and that id is stable for the process's whole lifetime, so scope: \"session\" only "
        + "matters when merging sessions from OTHER (past) server processes. Use groupBy:\"session\" with "
        + "since/until first to discover which session ids exist in a date range - there is no other directory "
        + "of past sessions - then pass those ids to sessionIds to merge their totals together. "
        + "TASK ids, unlike session ids, ARE caller-supplied: pass taskId on a tool call, then read just that "
        + "caller's calls back with taskIds or groupBy:\"task\". That is the only way to tell concurrent "
        + "callers apart, since they all share the one ambient session id. To measure a single call's exact "
        + "token cost, snapshot with groupBy:\"tool\" before and after it and subtract that tool's row.")]
    public static string GetRetrievalMetrics(
        MetricsReader metrics,
        [Description("session | global (default global).")] string scope = "global",
        [Description("One or more session ids to merge together. Required for scope=session.")] string[]? sessionIds = null,
        [Description("One or more caller-supplied task ids to narrow to - the ids passed as taskId on the tool "
            + "calls themselves. Independent of scope, since a task id names one caller inside a session.")] string[]? taskIds = null,
        [Description("Inclusive ISO date lower bound, e.g. \"2026-07-07\" (yyyy-MM-dd only).")] string? since = null,
        [Description("Inclusive ISO date upper bound, e.g. \"2026-07-21\" (yyyy-MM-dd only).")] string? until = null,
        [Description("tool | symbol | level | session | task | none (default tool). \"session\" groups by session_id "
            + "with firstSeen/lastSeen - the way to discover past session ids for a date range; \"task\" does the "
            + "same for caller-supplied task ids.")] string groupBy = "tool")
    {
        if (since is not null && !DateOnly.TryParseExact(since, "yyyy-MM-dd", out _))
            return Formats.Render(new { error = "invalid_date", detail = $"since must be yyyy-MM-dd, got '{since}'." });
        if (until is not null && !DateOnly.TryParseExact(until, "yyyy-MM-dd", out var untilDay))
            return Formats.Render(new { error = "invalid_date", detail = $"until must be yyyy-MM-dd, got '{until}'." });

        // until is inclusive of that whole day, but created_at carries a full timestamp, so the SQL
        // bound has to be the exclusive start of the NEXT day rather than the bare date string.
        var untilExclusive = until is null ? null : DateOnly.ParseExact(until, "yyyy-MM-dd").AddDays(1).ToString("yyyy-MM-dd");

        var result = metrics.Read(scope, sessionIds, since, untilExclusive, groupBy, taskIds);
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

        });
    }
}
