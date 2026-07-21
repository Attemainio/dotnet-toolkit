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
        + "events only. scope: session|task|global; groupBy: tool|symbol|level|none.")]
    public static string GetRetrievalMetrics(
        MetricsReader metrics,
        [Description("session | task | global (default global).")] string scope = "global",
        [Description("Required for scope=session.")] string? sessionId = null,
        [Description("Required for scope=task.")] string? taskId = null,
        [Description("tool | symbol | level | none (default tool).")] string groupBy = "tool")
    {
        var result = metrics.Read(scope, sessionId, taskId, groupBy);
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
            groups = result.Groups.Select(g => new { key = g.Key, calls = g.Calls, tokensReturned = g.TokensReturned }),
            flags = result.Flags.Select(f => new { kind = f.Kind, symbolId = f.SymbolId, count = f.Count, hint = f.Hint }),
        });
    }
}
