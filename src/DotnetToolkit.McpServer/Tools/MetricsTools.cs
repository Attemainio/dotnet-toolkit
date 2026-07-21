using System.ComponentModel;
using DotnetToolkit.McpServer.Contracts;
using DotnetToolkit.McpServer.Identity;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Telemetry;
using DotnetToolkit.McpServer.Workspace;
using ModelContextProtocol.Server;

namespace DotnetToolkit.McpServer.Tools;

[McpServerToolType]
public static class MetricsTools
{
    [McpServerTool(Name = "get_retrieval_metrics")]
    [Description("Self-observation over this server's own telemetry (spec §17): token totals, lease savings, "
        + "validation attempts, and advisory flags such as repeated fetches without a lease. Computed from raw "
        + "events only. scope: session|global; groupBy: tool|symbol|level|session|none. "
        + "Session ids are no longer caller-supplied on any tool - every call in this server process shares "
        + "one ambient id automatically, and that id is stable for the process's whole lifetime, so scope: "
        + "\"session\" only matters when merging sessions from OTHER (past) server processes. Use "
        + "groupBy:\"session\" with since/until first to discover which session ids exist in a date range - "
        + "there is no other directory of past sessions - then pass those ids to sessionIds to merge their "
        + "totals together.")]
    public static string GetRetrievalMetrics(
        MetricsReader metrics,
        [Description("session | global (default global).")] string scope = "global",
        [Description("One or more session ids to merge together. Required for scope=session.")] string[]? sessionIds = null,
        [Description("Inclusive ISO date lower bound, e.g. \"2026-07-07\" (yyyy-MM-dd only).")] string? since = null,
        [Description("Inclusive ISO date upper bound, e.g. \"2026-07-21\" (yyyy-MM-dd only).")] string? until = null,
        [Description("tool | symbol | level | session | none (default tool). \"session\" groups by session_id "
            + "with firstSeen/lastSeen - the way to discover past session ids for a date range.")] string groupBy = "tool")
    {
        if (since is not null && !DateOnly.TryParseExact(since, "yyyy-MM-dd", out _))
            return Formats.Render(new { error = "invalid_date", detail = $"since must be yyyy-MM-dd, got '{since}'." });
        if (until is not null && !DateOnly.TryParseExact(until, "yyyy-MM-dd", out var untilDay))
            return Formats.Render(new { error = "invalid_date", detail = $"until must be yyyy-MM-dd, got '{until}'." });

        // until is inclusive of that whole day, but created_at carries a full timestamp, so the SQL
        // bound has to be the exclusive start of the NEXT day rather than the bare date string.
        var untilExclusive = until is null ? null : DateOnly.ParseExact(until, "yyyy-MM-dd").AddDays(1).ToString("yyyy-MM-dd");

        var result = metrics.Read(scope, sessionIds, since, untilExclusive, groupBy);
        return Formats.Render(new
        {
            totals = new
            {
                toolCalls = result.Totals.ToolCalls,
                tokensReturned = result.Totals.TokensReturned,
                leaseHits = result.Totals.LeaseHits,
                tokensSavedByLeases = result.Totals.TokensSavedByLeases,
                refetches = result.Totals.Refetches,
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
            flags = result.Flags.Select(f => new { kind = f.Kind, symbolId = f.SymbolId, count = f.Count, hint = f.Hint }),
        });
    }
}
