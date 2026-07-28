using DotnetToolkit.McpServer.Telemetry;

namespace DotnetToolkit.McpServer.Tools;

/// <summary>
/// The single place a tool's response is turned into a <see cref="RetrievalEvent"/>, shared by every
/// tool that reports into <c>get_retrieval_metrics</c>.
/// </summary>
/// <remarks>
/// Retrieval tools differ widely in how much of the event they can fill in: <c>get_symbol</c> carries a
/// resolution and a content version, while <c>search_log</c> has none of those. Every field beyond the
/// ones a tool cannot avoid knowing is therefore optional, so a tool records by naming only what it
/// actually has rather than padding a positional call with nulls.
/// </remarks>
internal static class ToolTelemetry
{
    /// <summary>
    /// The <c>[Description]</c> text every tool's optional <c>taskId</c> parameter carries, held once so
    /// twelve tools state the same contract in the same words.
    /// </summary>
    internal const string TaskIdParam =
        "Optional caller-chosen id to attribute this call to in telemetry, e.g. \"eval_flow_20260728\". "
        + "get_retrieval_metrics can then filter (taskIds) or group (groupBy:\"task\") by it, which is the "
        + "only way to separate concurrent callers from each other - the session id is one per server "
        + "process and therefore shared by every agent talking to it. Omit to attribute the call to that "
        + "ambient session instead.";

    /// <summary>
    /// Records one tool call and returns the rendered response unchanged, so a call site can
    /// <c>return ToolTelemetry.Record(...)</c> in place of returning the response directly.
    /// </summary>
    /// <param name="telemetry">The recorder owning the knowledge store this event is written to.</param>
    /// <param name="toolCallId">Identifier for this single tool call; shared by every row a batched call writes.</param>
    /// <param name="sessionId">The server process's ambient session id.</param>
    /// <param name="taskId">The caller-attributed id from <see cref="Identity.Ids.TaskId"/>, which falls back to <paramref name="sessionId"/>.</param>
    /// <param name="tool">The MCP tool name, as it appears in a tool-grouped metrics report.</param>
    /// <param name="requestedSymbol">What the caller asked for, verbatim — a symbol name, query, file path or commit range.</param>
    /// <param name="result">The rendered response, whose estimated token count is the row's cost.</param>
    /// <param name="symbolId">The resolved symbol, when the tool resolved one.</param>
    /// <param name="resolution">How the request was resolved to that symbol, when the tool distinguishes resolution paths.</param>
    /// <param name="contentVersion">The version token handed back to the caller, when the tool issues one.</param>
    /// <param name="returnedSymbols">How many symbols the response carried.</param>
    /// <param name="limitedBy">What degraded the answer (<c>index_only</c>, <c>degraded</c>), or null when it was fully live.</param>
    /// <param name="errorKind">The error identifier when the response is an error payload, else null.</param>
    /// <param name="direction">The traversal direction, for tools that walk a graph.</param>
    /// <returns><paramref name="result"/>, unchanged.</returns>
    internal static string Record(
        TelemetryRecorder telemetry,
        string toolCallId,
        string sessionId,
        string taskId,
        string tool,
        string requestedSymbol,
        string result,
        string? symbolId = null,
        string? resolution = null,
        string? contentVersion = null,
        int returnedSymbols = 0,
        string? limitedBy = null,
        string? errorKind = null,
        string? direction = null)
    {
        telemetry.RecordRetrieval(new RetrievalEvent
        {
            ToolCallId = toolCallId,
            SessionId = sessionId,
            TaskId = taskId,
            ToolName = tool,
            RequestedSymbol = requestedSymbol,
            SymbolId = symbolId,
            Resolution = resolution,
            Direction = direction,
            ContentVersion = contentVersion,
            ReturnedSymbols = returnedSymbols,
            ReturnedTokens = TelemetryRecorder.EstimateTokens(result),
            // Telemetry keeps the pre-3.0 column name: retrieval_events is immutable raw history and
            // its rows cannot be rewritten, so renaming the column would split one signal across two.
            Staleness = limitedBy ?? "live",
            ErrorKind = errorKind,
        });
        return result;
    }
}
