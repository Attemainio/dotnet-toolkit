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
    private readonly KnowledgeStore _store;
    private readonly ILogger<TelemetryRecorder> _log;

    public TelemetryRecorder(KnowledgeStore store, ILogger<TelemetryRecorder> log)
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
                     symbol_id, resolution, direction, known_version, refetch, lease_hit,
                     content_version, returned_symbols, returned_tokens, staleness, error_kind, created_at)
                VALUES
                    ($event_id, $tool_call_id, $session_id, $task_id, $tool_name, $requested_symbol,
                     $symbol_id, $resolution, $direction, $known_version, $refetch, $lease_hit,
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
            cmd.Parameters.AddWithValue("$known_version", (object?)e.KnownVersion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$refetch", e.Refetch ? 1 : 0);
            cmd.Parameters.AddWithValue("$lease_hit", e.LeaseHit ? 1 : 0);
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

    public sealed record PatchEvent
    {
        public required string ToolCallId { get; init; }
        public required string PatchId { get; init; }
        public required string ValidationAttemptId { get; init; }
        public required string SessionId { get; init; }
        public required string TaskId { get; init; }
        public int AttemptOrdinal { get; init; } = 1;
        public required string ChangedSymbolIdsJson { get; init; }
        public required string ChangeKindsJson { get; init; }
        public required string BaseVersionsJson { get; init; }
        public required string CompletedLevel { get; init; }
        public required string RequiredLevel { get; init; }
        public required bool IsSufficient { get; init; }
        public required bool Succeeded { get; init; }
        public required bool Applied { get; init; }
        public string? Intent { get; init; }
        public int RawDiagnostics { get; init; }
        public int DistilledDiagnostics { get; init; }
        public int ReturnedTokens { get; init; }
        public long DurationMs { get; init; }
    }

    public void RecordPatch(PatchEvent e)
    {
        if (!_store.Available)
            return;
        try
        {
            using var connection = _store.Connect();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO patch_events
                    (event_id, tool_call_id, patch_id, validation_attempt_id, session_id, task_id,
                     attempt_ordinal, changed_symbol_ids, change_kinds, base_versions, completed_level,
                     required_level, is_sufficient, succeeded, applied, intent, raw_diagnostics,
                     distilled_diagnostics, returned_tokens, duration_ms, created_at)
                VALUES
                    ($event_id, $tool_call_id, $patch_id, $val_id, $session_id, $task_id,
                     $ordinal, $changed, $kinds, $base, $completed,
                     $required, $sufficient, $succeeded, $applied, $intent, $raw,
                     $distilled, $tokens, $duration, $created_at);
                """;
            cmd.Parameters.AddWithValue("$event_id", Identity.Ids.Event());
            cmd.Parameters.AddWithValue("$tool_call_id", e.ToolCallId);
            cmd.Parameters.AddWithValue("$patch_id", e.PatchId);
            cmd.Parameters.AddWithValue("$val_id", e.ValidationAttemptId);
            cmd.Parameters.AddWithValue("$session_id", e.SessionId);
            cmd.Parameters.AddWithValue("$task_id", e.TaskId);
            cmd.Parameters.AddWithValue("$ordinal", e.AttemptOrdinal);
            cmd.Parameters.AddWithValue("$changed", e.ChangedSymbolIdsJson);
            cmd.Parameters.AddWithValue("$kinds", e.ChangeKindsJson);
            cmd.Parameters.AddWithValue("$base", e.BaseVersionsJson);
            cmd.Parameters.AddWithValue("$completed", e.CompletedLevel);
            cmd.Parameters.AddWithValue("$required", e.RequiredLevel);
            cmd.Parameters.AddWithValue("$sufficient", e.IsSufficient ? 1 : 0);
            cmd.Parameters.AddWithValue("$succeeded", e.Succeeded ? 1 : 0);
            cmd.Parameters.AddWithValue("$applied", e.Applied ? 1 : 0);
            cmd.Parameters.AddWithValue("$intent", (object?)e.Intent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$raw", e.RawDiagnostics);
            cmd.Parameters.AddWithValue("$distilled", e.DistilledDiagnostics);
            cmd.Parameters.AddWithValue("$tokens", e.ReturnedTokens);
            cmd.Parameters.AddWithValue("$duration", e.DurationMs);
            cmd.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to record patch event for {Patch}", e.PatchId);
        }
    }

    /// <summary>Appends a lifecycle marker (task/session start, compaction signal) — spec §19.1.</summary>
    public void RecordSession(string sessionId, string? taskId, string kind, string? detail = null)
    {
        if (!_store.Available)
            return;
        try
        {
            using var connection = _store.Connect();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO session_events (event_id, session_id, task_id, kind, detail, created_at)
                VALUES ($event_id, $session_id, $task_id, $kind, $detail, $created_at);
                """;
            cmd.Parameters.AddWithValue("$event_id", Identity.Ids.Event());
            cmd.Parameters.AddWithValue("$session_id", sessionId);
            cmd.Parameters.AddWithValue("$task_id", (object?)taskId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to record session event {Kind}", kind);
        }
    }
}
