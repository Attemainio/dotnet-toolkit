using DotnetToolkit.McpServer.Identity;

namespace DotnetToolkit.McpServer.Store;

/// <summary>
/// Append-only development log (spec §18). This stratum is a source of truth — it is never rebuilt
/// from source. Every applied <c>validate_patch</c> appends exactly one <see cref="LogEntry"/> with
/// one <see cref="LogSymbol"/> per changed symbol (Conformance C12).
/// </summary>
public sealed class FeatureLogStore
{
    private readonly KnowledgeStore _store;

    public FeatureLogStore(KnowledgeStore store) => _store = store;

    public bool Available => _store.Available;

    public sealed record LogSymbol(
        string SymbolId, IReadOnlyList<string> ChangeKinds, string? Detail,
        string? OldVersion, string? NewVersion, string? ApiImpact);

    public sealed record LogEntry(
        string TaskId, string? PatchId, string? CommitSha, string Intent,
        IReadOnlyList<string> Tags, string? ValidationJson, IReadOnlyList<LogSymbol> Symbols);

    /// <summary>Appends one log record and its per-symbol rows in a single transaction. Returns the log id.</summary>
    public string Append(LogEntry entry)
    {
        if (!_store.Available)
            return "";

        var logId = Ids.Log();
        using var connection = _store.Connect();
        using var tx = connection.BeginTransaction();

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO feature_log (log_id, task_id, patch_id, commit_sha, intent, tags, validation_json, created_at)
                VALUES ($log, $task, $patch, $commit, $intent, $tags, $validation, $ts);
                """;
            cmd.Parameters.AddWithValue("$log", logId);
            cmd.Parameters.AddWithValue("$task", entry.TaskId);
            cmd.Parameters.AddWithValue("$patch", (object?)entry.PatchId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$commit", (object?)entry.CommitSha ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$intent", entry.Intent);
            cmd.Parameters.AddWithValue("$tags", System.Text.Json.JsonSerializer.Serialize(entry.Tags));
            cmd.Parameters.AddWithValue("$validation", (object?)entry.ValidationJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO feature_log_symbols
                    (log_id, symbol_id, change_kinds, detail, old_version, new_version, api_impact)
                VALUES ($log, $symbol, $kinds, $detail, $old, $new, $impact);
                """;
            foreach (var symbol in entry.Symbols)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$log", logId);
                cmd.Parameters.AddWithValue("$symbol", symbol.SymbolId);
                cmd.Parameters.AddWithValue("$kinds", System.Text.Json.JsonSerializer.Serialize(symbol.ChangeKinds));
                cmd.Parameters.AddWithValue("$detail", (object?)symbol.Detail ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$old", (object?)symbol.OldVersion ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$new", (object?)symbol.NewVersion ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$impact", (object?)symbol.ApiImpact ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return logId;
    }

    public int EntryCount()
    {
        if (!_store.Available)
            return 0;
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM feature_log;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public (int Entries, int Symbols) CountsForTask(string taskId)
    {
        if (!_store.Available)
            return (0, 0);
        using var connection = _store.Connect();
        using var entries = connection.CreateCommand();
        entries.CommandText = "SELECT COUNT(*) FROM feature_log WHERE task_id = $t;";
        entries.Parameters.AddWithValue("$t", taskId);
        var entryCount = Convert.ToInt32(entries.ExecuteScalar() ?? 0);

        using var symbols = connection.CreateCommand();
        symbols.CommandText = """
            SELECT COUNT(*) FROM feature_log_symbols s
            JOIN feature_log l ON l.log_id = s.log_id WHERE l.task_id = $t;
            """;
        symbols.Parameters.AddWithValue("$t", taskId);
        var symbolCount = Convert.ToInt32(symbols.ExecuteScalar() ?? 0);
        return (entryCount, symbolCount);
    }
}
