using DotnetToolkit.McpServer.Store;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DotnetToolkit.McpServer.Telemetry;

/// <summary>
/// Writes raw telemetry rows (spec §19.1). Every content-bearing tool call appends exactly
/// one <c>retrieval_events</c> row; writes are best-effort — a telemetry failure must never
/// fail the tool call it is measuring.
/// </summary>
public sealed class TelemetryRecorder
{
    private readonly IKnowledgeStore _store;
    private readonly ILogger<TelemetryRecorder> _log;

    public TelemetryRecorder(IKnowledgeStore store, ILogger<TelemetryRecorder> log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>
    /// Approximate token count for a serialized response. A precise BPE count is out of scope
    /// for MVP; ~4 chars/token is stable enough to drive relative waste comparisons (spec §19.1
    /// "measured on serialized response").
    /// </summary>
    public static int EstimateTokens(string? serialized) =>
        string.IsNullOrEmpty(serialized) ? 0 : (serialized.Length + 3) / 4;

    public void RecordRetrieval(RetrievalEvent e)
    {
        if (!_store.Available)
            return;
        try
        {
            using var connection = _store.Connect();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO retrieval_events
                    (event_id, tool_call_id, session_id, task_id, tool_name, requested_symbol,
                     symbol_id, resolution, direction,
                     content_version, returned_symbols, returned_tokens, staleness, error_kind, created_at)
                VALUES
                    ($event_id, $tool_call_id, $session_id, $task_id, $tool_name, $requested_symbol,
                     $symbol_id, $resolution, $direction,
                     $content_version, $returned_symbols, $returned_tokens, $staleness, $error_kind, $created_at);
                """;
            cmd.Parameters.AddWithValue("$event_id", Identity.Ids.Event());
            cmd.Parameters.AddWithValue("$tool_call_id", e.ToolCallId);
            cmd.Parameters.AddWithValue("$session_id", e.SessionId);
            cmd.Parameters.AddWithValue("$task_id", e.TaskId);
            cmd.Parameters.AddWithValue("$tool_name", e.ToolName);
            cmd.Parameters.AddWithValue("$requested_symbol", (object?)e.RequestedSymbol ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$symbol_id", (object?)e.SymbolId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$resolution", (object?)e.Resolution ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$direction", (object?)e.Direction ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$content_version", (object?)e.ContentVersion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$returned_symbols", e.ReturnedSymbols);
            cmd.Parameters.AddWithValue("$returned_tokens", e.ReturnedTokens);
            cmd.Parameters.AddWithValue("$staleness", e.Staleness);
            cmd.Parameters.AddWithValue("$error_kind", (object?)e.ErrorKind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to record retrieval event for {Tool}", e.ToolName);
        }
    }

    /// <summary>One tool call as the harness dispatched it, measured by the <c>meter-tool-call</c> hook.</summary>
    /// <remarks>
    /// Deliberately distinct from <see cref="RetrievalEvent"/>. A retrieval event is written from inside
    /// an MCP tool method, so it exists only for this server's own tools; this one is written for every
    /// tool the harness runs, which is the only way a Grep-and-Read route can be measured against an MCP
    /// route on one instrument. It also splits the two directions, which a retrieval event cannot: from
    /// inside a tool method only the response side is visible.
    /// </remarks>
    public sealed record ToolCallEvent
    {
        public required string ToolName { get; init; }
        public required string ToolUseId { get; init; }

        /// <summary>Tokens the model had to generate to make the call — the output side.</summary>
        public int RequestTokens { get; init; }

        /// <summary>Tokens the call loaded into the model's context — the input side.</summary>
        public int ResponseTokens { get; init; }

        /// <summary>What produced the counts, so a recalibration is not silently mixed with older rows.</summary>
        public required string TokenEstimator { get; init; }

        public string? HarnessSessionId { get; init; }
        public string? Harness { get; init; }
        public string? Model { get; init; }
        public string? TurnId { get; init; }
        public string? TranscriptPath { get; init; }

        /// <summary>Legacy mirror retained for databases and older hook payloads.</summary>
        public string? ClaudeSessionId { get; init; }
        public string? AgentId { get; init; }
        public string? AgentType { get; init; }

        /// <summary>Whether the call put a compiled <c>.cs</c> file into context whole (an unranged Read, or a dumping utility).</summary>
        public bool WholeFileCsRead { get; init; }
    }

    /// <summary>Appends one metered tool call, ignoring one already recorded under the same id.</summary>
    /// <param name="e">The measurement, as reported by the hook over the control channel.</param>
    /// <remarks>
    /// The session id is stamped here rather than sent by the hook, and that is the whole reason this goes
    /// through the server: a hook is a separate process with its own ambient id, so a row it wrote itself
    /// would carry an id no read ever matches. <c>INSERT OR IGNORE</c> makes a duplicated hook delivery a
    /// no-op rather than a double count, resting on <c>tool_use_id</c>'s UNIQUE constraint.
    /// </remarks>
    public void RecordToolCall(ToolCallEvent e)
    {
        if (!_store.Available)
            return;
        try
        {
            using var connection = _store.Connect();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO tool_call_events
                    (event_id, session_id, claude_session_id, harness_session_id, harness, model,
                     turn_id, transcript_path, agent_id, agent_type, tool_use_id, tool_name,
                     request_tokens, response_tokens, token_estimator, whole_file_cs_read, created_at)
                VALUES
                    ($event_id, $session_id, $claude_session_id, $harness_session_id, $harness, $model,
                     $turn_id, $transcript_path, $agent_id, $agent_type, $tool_use_id, $tool_name,
                     $request_tokens, $response_tokens, $token_estimator, $whole_file_cs_read, $created_at);
                """;
            cmd.Parameters.AddWithValue("$event_id", Identity.Ids.Event());
            cmd.Parameters.AddWithValue("$session_id", Identity.Ids.AmbientSession);
            cmd.Parameters.AddWithValue("$claude_session_id", (object?)e.ClaudeSessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$harness_session_id", (object?)e.HarnessSessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$harness", (object?)e.Harness ?? "unknown");
            cmd.Parameters.AddWithValue("$model", (object?)e.Model ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$turn_id", (object?)e.TurnId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$transcript_path", (object?)e.TranscriptPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$agent_id", (object?)e.AgentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$agent_type", (object?)e.AgentType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tool_use_id", e.ToolUseId);
            cmd.Parameters.AddWithValue("$tool_name", e.ToolName);
            cmd.Parameters.AddWithValue("$request_tokens", e.RequestTokens);
            cmd.Parameters.AddWithValue("$response_tokens", e.ResponseTokens);
            cmd.Parameters.AddWithValue("$token_estimator", e.TokenEstimator);
            cmd.Parameters.AddWithValue("$whole_file_cs_read", e.WholeFileCsRead ? 1 : 0);
            cmd.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to record tool call event for {Tool}", e.ToolName);
        }
    }
}
