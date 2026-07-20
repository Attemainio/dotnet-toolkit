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
        string SymbolId, string? OldSymbolId, IReadOnlyList<string> ChangeKinds, string? Detail,
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
                    (log_id, symbol_id, old_symbol_id, change_kinds, detail, old_version, new_version, api_impact)
                VALUES ($log, $symbol, $oldsymbol, $kinds, $detail, $old, $new, $impact);
                """;
            foreach (var symbol in entry.Symbols)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$log", logId);
                cmd.Parameters.AddWithValue("$symbol", symbol.SymbolId);
                cmd.Parameters.AddWithValue("$oldsymbol", (object?)symbol.OldSymbolId ?? DBNull.Value);
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

    /// <summary>
    /// Every prior symbolId a rename/arity change chain has passed through, including <paramref
    /// name="symbolId"/> itself. A rename gives the same logical member a new symbolId (its content-
    /// derived hash is over the fully-qualified name), so log entries recorded before the rename stay
    /// filed under the old id -- this walks feature_log_symbols.old_symbol_id backward to recover them.
    /// </summary>
    public IReadOnlyList<string> ResolveIdChain(string symbolId)
    {
        if (!_store.Available)
            return [symbolId];

        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            WITH RECURSIVE chain(id) AS (
                SELECT $start
                UNION
                SELECT s.old_symbol_id
                FROM feature_log_symbols s
                JOIN chain c ON s.symbol_id = c.id
                WHERE s.old_symbol_id IS NOT NULL AND s.old_symbol_id != s.symbol_id
            )
            SELECT id FROM chain;
            """;
        cmd.Parameters.AddWithValue("$start", symbolId);

        var ids = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>
    /// RecentForSymbol for the common case: the chain walk and the log lookup folded into one query on
    /// one connection, instead of a caller doing <see cref="ResolveIdChain"/> then <see
    /// cref="RecentForSymbol"/> as two separate round trips. Was exactly that two-step call in
    /// get_symbol's recentLog path -- on the default include set, a batched get_symbol turned every
    /// symbol into 2 connection-open+query round trips instead of 1.
    /// </summary>
    public IReadOnlyList<SymbolLogEntry> RecentForSymbolWithChain(string symbolId, int limit = 3)
    {
        if (!_store.Available)
            return [];

        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            WITH RECURSIVE chain(id) AS (
                SELECT $start
                UNION
                SELECT s.old_symbol_id
                FROM feature_log_symbols s
                JOIN chain c ON s.symbol_id = c.id
                WHERE s.old_symbol_id IS NOT NULL AND s.old_symbol_id != s.symbol_id
            )
            SELECT l.log_id, l.created_at, l.intent, s.detail, s.new_version, s.api_impact
            FROM feature_log_symbols s
            JOIN feature_log l ON l.log_id = s.log_id
            WHERE s.symbol_id IN (SELECT id FROM chain)
            ORDER BY l.created_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$start", symbolId);
        cmd.Parameters.AddWithValue("$limit", limit);

        var entries = new List<SymbolLogEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new SymbolLogEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return entries;
    }

    /// <summary>
    /// One development-log entry as attached to a symbol (spec §9 <c>recentLog</c>). <paramref name="NewVersion"/>
    /// is carried so the caller can mark an entry stale rather than presenting superseded history as truth.
    /// </summary>
    public sealed record SymbolLogEntry(
        string LogId, string CreatedAt, string Intent, string? Detail, string? NewVersion, string? ApiImpact);

    /// <summary>
    /// The most recent log entries touching a symbol, newest first. Accepts several ids because a
    /// rename/arity change gives the same logical member a new symbolId, and its history lives under
    /// the old one.
    /// </summary>
    public IReadOnlyList<SymbolLogEntry> RecentForSymbol(IReadOnlyCollection<string> symbolIds, int limit = 3)
    {
        if (!_store.Available || symbolIds.Count == 0)
            return [];

        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        var names = symbolIds.Select((_, i) => "$s" + i).ToList();
        cmd.CommandText = $"""
            SELECT l.log_id, l.created_at, l.intent, s.detail, s.new_version, s.api_impact
            FROM feature_log_symbols s
            JOIN feature_log l ON l.log_id = s.log_id
            WHERE s.symbol_id IN ({string.Join(',', names)})
            ORDER BY l.created_at DESC
            LIMIT $limit;
            """;
        var i = 0;
        foreach (var id in symbolIds)
            cmd.Parameters.AddWithValue("$s" + i++, id);
        cmd.Parameters.AddWithValue("$limit", limit);

        var entries = new List<SymbolLogEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new SymbolLogEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return entries;
    }

    /// <summary>Free-text search over recorded intents — the read path for "why is this like this".</summary>
public IReadOnlyList<(string LogId, string CreatedAt, string Intent, IReadOnlyList<string> Tags)> SearchIntents(string? query, int limit = 10)
    {
        if (!_store.Available)
            return [];
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        var filter = string.IsNullOrWhiteSpace(query) ? "" : " WHERE intent LIKE $q";
        cmd.CommandText = $"SELECT log_id, created_at, intent, tags FROM feature_log{filter} ORDER BY created_at DESC LIMIT $limit;";
        if (!string.IsNullOrWhiteSpace(query))
            cmd.Parameters.AddWithValue("$q", "%" + query + "%");
        cmd.Parameters.AddWithValue("$limit", limit);

        var rows = new List<(string, string, string, IReadOnlyList<string>)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                System.Text.Json.JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? []));
        return rows;
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
