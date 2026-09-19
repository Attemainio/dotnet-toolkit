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

    /// <summary>Process-wide retrieval-cost totals: tool calls and tokens returned.</summary>
    public sealed record Totals(int ToolCalls, long TokensReturned);

    /// <summary>One row of a metrics breakdown grouped by tool/symbol/task (per the request's groupBy), with its own call/token totals and first/last-seen timestamps.</summary>
    public sealed record Group(string Key, int Calls, long TokensReturned, string? FirstSeen = null, string? LastSeen = null);

    /// <summary>Both directions of one harness-metered tool call, summed: requests are what the model had to generate (output tokens), responses what the call loaded into its context (input tokens).</summary>
    public sealed record HarnessTotals(int ToolCalls, long RequestTokens, long ResponseTokens);

    /// <summary>One harness-metered breakdown row, keyed by tool name or by agent type.</summary>
    public sealed record HarnessGroup(string Key, int Calls, long RequestTokens, long ResponseTokens);

        /// <summary>One agent's own harness-metered breakdown, further split out by tool.</summary>
        public sealed record HarnessAgentBreakdown(
            string Agent, int Calls, long RequestTokens, long ResponseTokens, IReadOnlyList<HarnessGroup> ByTool);

        /// <summary>What the PostToolUse meter saw: every tool the harness dispatched, MCP or not.</summary>
        /// <param name="Estimator">Names the approximation behind every count in this block.</param>
        public sealed record Harness(
            HarnessTotals Totals, IReadOnlyList<HarnessGroup> ByTool, IReadOnlyList<HarnessGroup> ByAgent,
            IReadOnlyList<HarnessAgentBreakdown> ByAgentTool, IReadOnlyList<HarnessGroup> ByHarness,
            IReadOnlyList<HarnessGroup> ByModel, string Estimator);

    /// <summary>The full response shape for <c>get_retrieval_metrics</c>: totals, requested groupings, and the harness-metered view when one exists.</summary>
    public sealed record Metrics(Totals Totals, IReadOnlyList<Group> Groups, Harness? Harness = null);

    /// <param name="since">Inclusive ISO date (yyyy-MM-dd) lower bound on created_at.</param>
    /// <param name="until">Exclusive bound on created_at, already resolved by the caller to the day AFTER
    /// the last day wanted (an ISO date string compares correctly against created_at's full timestamp only
    /// as a lower bound, so an inclusive "last day" filter needs its upper edge pushed one day out).</param>
    /// <param name="groupBy">tool | symbol | level | session | task | none</param>
    /// <param name="taskIds">One or more caller-supplied task ids to narrow to, each naming one caller
    /// inside this session rather than a different slice of history.</param>
    /// <remarks>
    /// There is no cross-session reading and no argument that asks for one: this reports the calls of the
    /// process it is running in, and a task id narrows further inside that. <see cref="KnowledgeStore"/>
    /// empties the raw tables at startup and on a graceful stop, so in practice they hold nothing else;
    /// the session filter below is the second guard, keeping the reading honest even when a purge was
    /// skipped because the store failed to open.
    /// </remarks>
    public Metrics Read(string? since, string? until, string groupBy, string[]? taskIds = null)
    {
        if (!_store.Available)
            return new Metrics(new Totals(0, 0), []);

        using var connection = _store.Connect();
        var (where, parameters) = EventFilter(taskIds, since, until);

        var totals = ReadTotals(connection, where, parameters);
        var groups = ReadGroups(connection, where, parameters, groupBy);

        // Omitted rather than left unfiltered when a task id is named: the meter records no task id (a
        // hook cannot know one), so a harness block beside task-filtered retrieval numbers would read as
        // a comparison between them, and would be a wrong one.
        var harness = taskIds is { Length: > 0 } ? null : ReadHarness(connection, since, until);
        return new Metrics(totals, groups, harness);
    }

    private static (string Where, List<(string, object)> Params) EventFilter(
        string[]? taskIds, string? since, string? until)
    {
        var parameters = new List<(string, object)>();

        // The ambient id is minted once per process, so this is what confines every reading to this
        // server's own calls. It is unconditional by design: a month of accumulated history distorts
        // exactly the efficiency numbers this telemetry exists to report, so there is no widening it.
        var clauses = new List<string> { "session_id = $sid" };
        parameters.Add(("$sid", Identity.Ids.AmbientSession));

        // A task id names one caller *within* the session - every agent talking to this process shares
        // its one ambient id - so this narrows inside the reading rather than reaching outside it.
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

        return new Totals(toolCalls, tokensReturned);
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
            // This is the axis a caller
            // measuring its own cost uses: it supplies its own task id per call, then groups by it to get
            // that probe's totals apart from every other caller sharing the process-wide session id.
            "task" => $"""
                SELECT task_id, COUNT(*), COALESCE(SUM(returned_tokens),0), MIN(created_at), MAX(created_at)
                FROM retrieval_events WHERE {where}
                GROUP BY task_id ORDER BY MAX(created_at) DESC;
                """,
                // Only ever one session now - the process's own - so this reports that session's id and
                // its observed span rather than discovering past ones.
            "session" => $"""
                SELECT session_id, COUNT(*), COALESCE(SUM(returned_tokens),0), MIN(created_at), MAX(created_at)
                FROM retrieval_events WHERE {where}
                GROUP BY session_id ORDER BY MAX(created_at) DESC;
                """,
            _ => $"""
                SELECT tool_name, COUNT(*), COALESCE(SUM(returned_tokens),0)
                FROM retrieval_events WHERE {where}
                GROUP BY tool_name ORDER BY 3 DESC;
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

    /// <summary>Reads the harness-metered side, or null when the meter recorded nothing.</summary>
    /// <remarks>
    /// Null rather than a zeroed block: "the meter recorded nothing" and "the tools cost nothing" are
    /// different claims, and a zero would state the second while meaning the first. The
    /// commonest cause of nothing is a server started before <c>hooks.json</c> registered the meter.
    /// </remarks>
    private static Harness? ReadHarness(SqliteConnection connection, string? since, string? until)
    {
        var clauses = new List<string> { "session_id = $sid" };
        var parameters = new List<(string, object)> { ("$sid", Identity.Ids.AmbientSession) };
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
        var where = string.Join(" AND ", clauses);

        HarnessTotals? totals = null;
        var estimator = "unknown";
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT COUNT(*), COALESCE(SUM(request_tokens),0), COALESCE(SUM(response_tokens),0),
                       GROUP_CONCAT(DISTINCT token_estimator)
                FROM tool_call_events WHERE {where};
                """;
            Bind(cmd, parameters);
            using var reader = cmd.ExecuteReader();
            if (reader.Read() && reader.GetInt32(0) > 0)
            {
                totals = new HarnessTotals(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2));
                if (!reader.IsDBNull(3))
                    estimator = reader.GetString(3);
            }
        }

            return totals is null
                ? null
                : new Harness(
                    totals,
                    ReadHarnessGroups(connection, where, parameters, byAgent: false),
                    ReadHarnessGroups(connection, where, parameters, byAgent: true),
                    ReadHarnessAgentTool(connection, where, parameters),
                    ReadHarnessDimension(connection, where, parameters, "harness", "(unknown harness)"),
                    ReadHarnessDimension(connection, where, parameters, "model", "(unknown model)"),
                    estimator);
        }

    private static List<HarnessGroup> ReadHarnessGroups(
        SqliteConnection connection, string where, List<(string, object)> parameters, bool byAgent)
    {
        // Chosen from two literals here rather than passed in, so nothing caller-supplied can reach the
        // SQL text; the where clause interpolated beside it is built the same way, from literals and
        // bound placeholders. A null agent_type means the main thread rather than a subagent.
        var key = byAgent ? "COALESCE(agent_type, '(main thread)')" : "tool_name";

        var groups = new List<HarnessGroup>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {key}, COUNT(*), COALESCE(SUM(request_tokens),0), COALESCE(SUM(response_tokens),0)
            FROM tool_call_events WHERE {where}
            GROUP BY {key} ORDER BY 4 DESC;
            """;
        Bind(cmd, parameters);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            groups.Add(new HarnessGroup(
                reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3)));
        }
        return groups;
        }

        private static List<HarnessGroup> ReadHarnessDimension(
            SqliteConnection connection,
            string where,
            List<(string, object)> parameters,
            string column,
            string missingLabel)
        {
            // Only internal literals call this method; the allowlist keeps an accidental future caller
            // from turning a reporting dimension into interpolated SQL.
            if (column is not ("harness" or "model"))
                throw new ArgumentOutOfRangeException(nameof(column));

            var groups = new List<HarnessGroup>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT COALESCE({column}, $missing), COUNT(*),
                       COALESCE(SUM(request_tokens),0), COALESCE(SUM(response_tokens),0)
                FROM tool_call_events WHERE {where}
                GROUP BY COALESCE({column}, $missing) ORDER BY 4 DESC;
                """;
            Bind(cmd, parameters);
            cmd.Parameters.AddWithValue("$missing", missingLabel);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                groups.Add(new HarnessGroup(
                    reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3)));
            }
            return groups;
        }

        /// <summary>Reads the harness-metered rows grouped by BOTH agent and tool, nested per agent.</summary>
        /// <remarks>
        /// <c>byTool</c> and <c>byAgent</c> each answer one axis; neither can say which tools one particular
        /// agent actually called. This groups by the pair directly, then folds the flat rows into one entry
        /// per agent carrying its own tool breakdown, so "how many Read calls did the raw probe make" is a
        /// single lookup rather than an inference from two separate aggregates.
        /// </remarks>
        private static List<HarnessAgentBreakdown> ReadHarnessAgentTool(
            SqliteConnection connection, string where, List<(string, object)> parameters)
        {
            var byAgent = new List<(string Agent, List<HarnessGroup> Tools)>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT COALESCE(agent_type, '(main thread)'), tool_name,
                           COUNT(*), COALESCE(SUM(request_tokens),0), COALESCE(SUM(response_tokens),0)
                    FROM tool_call_events WHERE {where}
                    GROUP BY 1, 2 ORDER BY 1, 5 DESC;
                    """;
                Bind(cmd, parameters);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var agent = reader.GetString(0);
                    var tool = new HarnessGroup(
                        reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4));

                    var bucket = byAgent.Count > 0 && byAgent[^1].Agent == agent ? byAgent[^1].Tools : null;
                    if (bucket is null)
                    {
                        bucket = new List<HarnessGroup>();
                        byAgent.Add((agent, bucket));
                    }
                    bucket.Add(tool);
                }
            }

            var breakdowns = new List<HarnessAgentBreakdown>();
            foreach (var (agent, tools) in byAgent)
            {
                var calls = 0;
                long requestTokens = 0, responseTokens = 0;
                foreach (var tool in tools)
                {
                    calls += tool.Calls;
                    requestTokens += tool.RequestTokens;
                    responseTokens += tool.ResponseTokens;
                }
                breakdowns.Add(new HarnessAgentBreakdown(agent, calls, requestTokens, responseTokens, tools));
            }
            return breakdowns;
        }
}
