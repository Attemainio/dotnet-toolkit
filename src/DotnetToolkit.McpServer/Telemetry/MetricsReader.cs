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
    private readonly IKnowledgeStore _store;

    public MetricsReader(IKnowledgeStore store) => _store = store;

    /// <summary>Process-wide retrieval-cost totals: tool calls, tokens returned, and validation counters.</summary>
    public sealed record Totals(
        int ToolCalls, long TokensReturned,
        int ValidationAttempts, int InsufficientValidations, int FailedValidations);

    /// <summary>One row of a metrics breakdown grouped by tool/session/day (per the request's groupBy), with its own call/token totals and first/last-seen timestamps.</summary>
    public sealed record Group(string Key, int Calls, long TokensReturned, string? FirstSeen = null, string? LastSeen = null);

    /// <summary>The full response shape for <c>get_retrieval_metrics</c>: totals and requested groupings.</summary>
    public sealed record Metrics(Totals Totals, IReadOnlyList<Group> Groups);

    /// <param name="scope">session | global</param>
    /// <param name="sessionIds">One or more session ids to merge together; required for scope=session.</param>
    /// <param name="since">Inclusive ISO date (yyyy-MM-dd) lower bound on created_at.</param>
    /// <param name="until">Exclusive bound on created_at, already resolved by the caller to the day AFTER
    /// the last day wanted (an ISO date string compares correctly against created_at's full timestamp only
    /// as a lower bound, so an inclusive "last day" filter needs its upper edge pushed one day out).</param>
    /// <param name="groupBy">tool | symbol | level | session | task | none</param>
    /// <param name="taskIds">One or more caller-supplied task ids to narrow to. Applied independently of
    /// <paramref name="scope"/>, since a task id identifies one caller inside a session rather than a
    /// different slice of history. Optional and last so the existing positional callers of this internal
    /// API keep compiling unchanged.</param>
    public Metrics Read(string scope, string[]? sessionIds, string? since, string? until, string groupBy,
        string[]? taskIds = null)
    {
        if (!_store.Available)
            return new Metrics(new Totals(0, 0, 0, 0, 0), []);

        using var connection = _store.Connect();
        var (where, parameters) = ScopeFilter(scope, sessionIds, taskIds, since, until);

        var totals = ReadTotals(connection, where, parameters);
        var groups = ReadGroups(connection, where, parameters, groupBy);
        return new Metrics(totals, groups);
    }

    private static (string Where, List<(string, object)> Params) ScopeFilter(
        string scope, string[]? sessionIds, string[]? taskIds, string? since, string? until)
    {
        var parameters = new List<(string, object)>();
        var clauses = new List<string> { "1=1" };

        if (string.Equals(scope.Trim(), "session", StringComparison.OrdinalIgnoreCase) && sessionIds is { Length: > 0 })
            clauses.Add(InClause("session_id", "$sid", sessionIds, parameters));

        // Not gated on scope, unlike sessionIds above: a task id names one caller *within* a session, so
        // narrowing to it is meaningful whether or not specific sessions were also named.
        if (taskIds is { Length: > 0 })
            clauses.Add(InClause("task_id", "$tid", taskIds, parameters));

        if (since is not null)
        {
            parameters.Add(("$since", since));
            clauses.Add("created_at >= $since");
        }
        if (until is not null)
        {
            parameters.Add(("$until", until));
            clauses.Add("created_at < $until");
        }

        return (string.Join(" AND ", clauses), parameters);
    }

    private static string InClause(string column, string prefix, string[] values, List<(string, object)> parameters)
    {
        var placeholders = new string[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            placeholders[i] = prefix + i;
            parameters.Add((prefix + i, values[i]));
        }
        return $"{column} IN ({string.Join(',', placeholders)})";
    }

    /// <summary>
    /// Restricts a <c>patch_events</c> aggregate to the rows whose cost is not already counted under
    /// <c>retrieval_events</c>. Interpolated into a query that aliases <c>patch_events</c> as <c>p</c>.
    /// </summary>
    /// <remarks>
    /// validate_patch writes only <c>patch_events</c>, so its rows have to be folded into any call or
    /// token total or they vanish entirely. rename_symbol, though, writes to BOTH tables for a single
    /// invocation under one <c>tool_call_id</c> - folding those in unconditionally reported every rename
    /// as two calls at twice the tokens it actually returned.
    /// </remarks>
    private const string NotAlreadyCounted =
        "NOT EXISTS (SELECT 1 FROM retrieval_events r WHERE r.tool_call_id = p.tool_call_id)";

    private Totals ReadTotals(SqliteConnection connection, string where, List<(string, object)> parameters)
    {
        int toolCalls = 0;
        long tokensReturned = 0;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT COUNT(*), COALESCE(SUM(returned_tokens),0)
                FROM retrieval_events WHERE {where};
                """;
            Bind(cmd, parameters);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                toolCalls = reader.GetInt32(0);
                tokensReturned = reader.GetInt64(1);
            }
        }

        int attempts = 0, insufficient = 0, failed = 0, patchOnlyCalls = 0;
        long patchOnlyTokens = 0;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT COUNT(*),
                       COALESCE(SUM(CASE WHEN is_sufficient = 0 THEN 1 ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN succeeded = 0 THEN 1 ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN {NotAlreadyCounted} THEN 1 ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN {NotAlreadyCounted} THEN returned_tokens ELSE 0 END),0)
                FROM patch_events p WHERE {where};
                """;
            Bind(cmd, parameters);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                attempts = reader.GetInt32(0);
                insufficient = reader.GetInt32(1);
                failed = reader.GetInt32(2);
                patchOnlyCalls = reader.GetInt32(3);
                patchOnlyTokens = reader.GetInt64(4);
            }
        }

        // validate_patch has no retrieval_events row at all - it writes patch_events instead (see
        // TelemetryRecorder.RecordPatch) - so its calls and tokens must be folded in here or they
        // silently vanish from every total this method reports, even though returned_tokens was
        // recorded for every one of them. Only the rows NotAlreadyCounted admits are folded in:
        // rename_symbol writes to both tables per call, and counting its patch row too reported one
        // rename as two calls at double its tokens. attempts/insufficient/failed still count EVERY
        // patch row - a rename really is a validation attempt, it is only its cost that is already held.
        toolCalls += patchOnlyCalls;
        tokensReturned += patchOnlyTokens;

        return new Totals(toolCalls, tokensReturned, attempts, insufficient, failed);
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
            // Same two-table union as "session" below, for the same reason. This is the axis a caller
            // measuring its own cost uses: it supplies its own task id per call, then groups by it to get
            // that probe's totals apart from every other caller sharing the process-wide session id.
            "task" => $"""
                SELECT task_id, COUNT(*), COALESCE(SUM(returned_tokens),0), MIN(created_at), MAX(created_at)
                FROM (
                    SELECT task_id, returned_tokens, created_at FROM retrieval_events WHERE {where}
                    UNION ALL
                    SELECT task_id, returned_tokens, created_at FROM patch_events p
                    WHERE {where} AND {NotAlreadyCounted}
                )
                GROUP BY task_id ORDER BY MAX(created_at) DESC;
                """,
            // A session can write to either table (or both), so merge the two under session_id, same
            // reasoning as the tool-grouped view below. min/max created_at give the session's observed
            // span without a separate query - this is how a caller discovers past session ids at all
            // (there is no session directory; created_at IS the only way to answer "sessions from two
            // weeks ago").
            "session" => $"""
                SELECT session_id, COUNT(*), COALESCE(SUM(returned_tokens),0), MIN(created_at), MAX(created_at)
                FROM (
                    SELECT session_id, returned_tokens, created_at FROM retrieval_events WHERE {where}
                    UNION ALL
                    SELECT session_id, returned_tokens, created_at FROM patch_events p
                    WHERE {where} AND {NotAlreadyCounted}
                )
                GROUP BY session_id ORDER BY MAX(created_at) DESC;
                """,
            // validate_patch never appears in retrieval_events (it has its own patch_events table -
            // see the comment in ReadTotals), so the tool-grouped view needs a second branch unioned
            // in under a literal label; patch_events has no tool_name column to group by since it is
            // the only tool writing there, and HAVING with no GROUP BY filters the single whole-table
            // aggregate row down to nothing when this scope has no patch_events at all. rename_symbol
            // does carry its own tool_name in retrieval_events, so NotAlreadyCounted is what stops its
            // patch row being relabelled 'validate_patch' and counted a second time.
            _ => $"""
                SELECT tool_name, COUNT(*), COALESCE(SUM(returned_tokens),0)
                FROM retrieval_events WHERE {where} GROUP BY tool_name
                UNION ALL
                SELECT 'validate_patch', COUNT(*), COALESCE(SUM(returned_tokens),0)
                FROM patch_events p WHERE {where} AND {NotAlreadyCounted}
                HAVING COUNT(*) > 0
                ORDER BY 3 DESC;
                """,
        };

        var groups = new List<Group>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, parameters);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            groups.Add(reader.FieldCount >= 5
                ? new Group(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4))
                : new Group(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2)));
        }
        return groups;
    }


    private static void Bind(SqliteCommand cmd, List<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
    }
}
