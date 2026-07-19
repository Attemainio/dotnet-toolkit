using DotnetToolkit.McpServer.Store;
using Microsoft.Data.Sqlite;

namespace DotnetToolkit.McpServer.Telemetry;

/// <summary>
/// Read-side aggregations over raw telemetry for <c>get_retrieval_metrics</c> (spec §17).
/// Everything here is computed from immutable raw events only; no derived attribution
/// (that stratum arrives in Phase 5).
/// </summary>
public sealed class MetricsReader
{
    private readonly KnowledgeStore _store;

    public MetricsReader(KnowledgeStore store) => _store = store;

    public sealed record Totals(
        int ToolCalls, long TokensReturned, int LeaseHits, long TokensSavedByLeases, int Refetches,
        int ValidationAttempts, int InsufficientValidations, int FailedValidations);

    public sealed record Group(string Key, int Calls, long TokensReturned);

    public sealed record Flag(string Kind, string? SymbolId, int Count, string Hint);

    public sealed record Metrics(Totals Totals, IReadOnlyList<Group> Groups, IReadOnlyList<Flag> Flags);

    /// <param name="scope">session | task | global</param>
    /// <param name="groupBy">tool | symbol | level | none</param>
    public Metrics Read(string scope, string? sessionId, string? taskId, string groupBy)
    {
        if (!_store.Available)
            return new Metrics(new Totals(0, 0, 0, 0, 0, 0, 0, 0), [], []);

        using var connection = _store.Connect();
        var (where, parameters) = ScopeFilter(scope, sessionId, taskId);

        var totals = ReadTotals(connection, where, parameters);
        var groups = ReadGroups(connection, where, parameters, groupBy);
        var flags = ReadFlags(connection, where, parameters);
        return new Metrics(totals, groups, flags);
    }

    private static (string Where, List<(string, object)> Params) ScopeFilter(string scope, string? sessionId, string? taskId)
    {
        var parameters = new List<(string, object)>();
        switch (scope.Trim().ToLowerInvariant())
        {
            case "task" when !string.IsNullOrEmpty(taskId):
                parameters.Add(("$task", taskId));
                return ("task_id = $task", parameters);
            case "session" when !string.IsNullOrEmpty(sessionId):
                parameters.Add(("$session", sessionId));
                return ("session_id = $session", parameters);
            default:
                return ("1=1", parameters);
        }
    }

    private Totals ReadTotals(SqliteConnection connection, string where, List<(string, object)> parameters)
    {
        int toolCalls = 0, leaseHits = 0, refetches = 0;
        long tokensReturned = 0;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT COUNT(*), COALESCE(SUM(returned_tokens),0),
                       COALESCE(SUM(lease_hit),0), COALESCE(SUM(refetch),0)
                FROM retrieval_events WHERE {where};
                """;
            Bind(cmd, parameters);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                toolCalls = reader.GetInt32(0);
                tokensReturned = reader.GetInt64(1);
                leaseHits = reader.GetInt32(2);
                refetches = reader.GetInt32(3);
            }
        }

        // Tokens saved by leases: each lease hit avoided re-sending content the size of that
        // symbol's largest prior full fetch in scope. Approximation, documented in §17 spirit.
        long tokensSaved = 0;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT COALESCE(SUM(best.max_tokens), 0)
                FROM retrieval_events le
                JOIN (
                    SELECT symbol_id, MAX(returned_tokens) AS max_tokens
                    FROM retrieval_events
                    WHERE {where} AND lease_hit = 0 AND symbol_id IS NOT NULL
                    GROUP BY symbol_id
                ) best ON best.symbol_id = le.symbol_id
                WHERE {where} AND le.lease_hit = 1 AND le.symbol_id IS NOT NULL;
                """;
            Bind(cmd, parameters);
            tokensSaved = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }

        int attempts = 0, insufficient = 0, failed = 0;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT COUNT(*),
                       COALESCE(SUM(CASE WHEN is_sufficient = 0 THEN 1 ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN succeeded = 0 THEN 1 ELSE 0 END),0)
                FROM patch_events WHERE {where};
                """;
            Bind(cmd, parameters);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                attempts = reader.GetInt32(0);
                insufficient = reader.GetInt32(1);
                failed = reader.GetInt32(2);
            }
        }

        return new Totals(toolCalls, tokensReturned, leaseHits, tokensSaved, refetches, attempts, insufficient, failed);
    }

    private List<Group> ReadGroups(SqliteConnection connection, string where, List<(string, object)> parameters, string groupBy)
    {
        var normalized = groupBy.Trim().ToLowerInvariant();
        if (normalized is "none" or "")
            return [];

        string sql = normalized switch
        {
            "symbol" => $"""
                SELECT COALESCE(symbol_id, '(unresolved)'), COUNT(*), COALESCE(SUM(returned_tokens),0)
                FROM retrieval_events WHERE {where} GROUP BY symbol_id ORDER BY 3 DESC;
                """,
            "level" => $"""
                SELECT completed_level, COUNT(*), COALESCE(SUM(returned_tokens),0)
                FROM patch_events WHERE {where} GROUP BY completed_level ORDER BY 2 DESC;
                """,
            _ => $"""
                SELECT tool_name, COUNT(*), COALESCE(SUM(returned_tokens),0)
                FROM retrieval_events WHERE {where} GROUP BY tool_name ORDER BY 3 DESC;
                """,
        };

        var groups = new List<Group>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, parameters);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            groups.Add(new Group(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2)));
        return groups;
    }

    private List<Flag> ReadFlags(SqliteConnection connection, string where, List<(string, object)> parameters)
    {
        var flags = new List<Flag>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT symbol_id, COUNT(*) AS c
            FROM retrieval_events
            WHERE {where} AND symbol_id IS NOT NULL AND lease_hit = 0 AND refetch = 0
            GROUP BY symbol_id HAVING c > 1 ORDER BY c DESC LIMIT 20;
            """;
        Bind(cmd, parameters);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            flags.Add(new Flag(
                "repeat_fetch_without_lease",
                reader.GetString(0),
                reader.GetInt32(1),
                "Supply knownVersion for this symbol."));
        }
        return flags;
    }

    private static void Bind(SqliteCommand cmd, List<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
    }
}
