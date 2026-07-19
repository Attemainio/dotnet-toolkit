using DotnetToolkit.McpServer.Store;
using Microsoft.Data.Sqlite;

namespace DotnetToolkit.McpServer.Telemetry;

/// <summary>
/// Post-processes raw telemetry into per-event verdicts and per-task summaries (spec §19.2).
///
/// This stratum is explicitly heuristic and explicitly rebuildable: it is dropped and recomputed from
/// immutable raw events, and every row carries <see cref="Version"/> so the rules can evolve without
/// rewriting history. Rebuilding for a fixed version is idempotent (Conformance C9).
///
/// Ruleset attr-v1:
///   1. used_for_edit        — the event's symbol was later changed by an applied patch in the task.
///   2. used_for_navigation  — a symbol this event surfaced was the target of a later retrieval.
///   3. reread               — a later same-task retrieval of the same symbol at the same version that
///                             was neither a lease hit nor an explicit refetch. Lease hits are correct
///                             behaviour and post-compaction refetches are justified; only a plain
///                             re-fetch of content you already had is waste.
///   4. verdict precedence   — contributing &gt; navigational &gt; wasted_reread &gt; unused.
/// </summary>
public sealed class AttributionJob
{
    public const string Version = "attr-v1";

    private readonly KnowledgeStore _store;

    public AttributionJob(KnowledgeStore store) => _store = store;

    private sealed record Event(string EventId, string TaskId, string? SymbolId, string? ContentVersion,
        bool LeaseHit, bool Refetch, long Tokens, string CreatedAt);

    /// <summary>Recomputes attribution for one task. Returns the number of events attributed.</summary>
    public int RebuildForTask(string taskId)
    {
        if (!_store.Available)
            return 0;

        using var connection = _store.Connect();
        var events = LoadEvents(connection, taskId);
        if (events.Count == 0)
            return 0;

        var changedByPatches = ChangedSymbolIds(connection, taskId);
        var laterTargets = events.Select(e => e.SymbolId).Where(s => s is not null).ToHashSet(StringComparer.Ordinal)!;

        using var tx = connection.BeginTransaction();

        // Drop and rebuild — never update in place — so a re-run for the same version is idempotent.
        Delete(connection, tx, "DELETE FROM derived_retrieval_attribution WHERE event_id IN "
            + "(SELECT event_id FROM retrieval_events WHERE task_id = $t);", taskId);
        Delete(connection, tx, "DELETE FROM derived_task_summary WHERE task_id = $t;", taskId);

        long contributing = 0, navigational = 0, unused = 0, wasted = 0, total = 0;
        var now = DateTimeOffset.UtcNow.ToString("O");

        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            total += e.Tokens;

            var usedForEdit = e.SymbolId is not null && changedByPatches.Contains(e.SymbolId);
            var reread = !usedForEdit && IsReread(events, i);
            var usedForNavigation = !usedForEdit && !reread && e.SymbolId is not null && laterTargets.Contains(e.SymbolId);

            var verdict = usedForEdit ? "contributing"
                : usedForNavigation ? "navigational"
                : reread ? "wasted_reread"
                : "unused";

            switch (verdict)
            {
                case "contributing": contributing += e.Tokens; break;
                case "navigational": navigational += e.Tokens; break;
                case "wasted_reread": wasted += e.Tokens; break;
                default: unused += e.Tokens; break;
            }

            WriteAttribution(connection, tx, e, usedForEdit, usedForNavigation, reread, verdict, now);
        }

        var (attempts, insufficient, firstSuccess) = PatchStats(connection, taskId);
        var savedByLeases = events.Where(e => e.LeaseHit).Sum(e => e.Tokens);

        WriteSummary(connection, tx, taskId, now, total, contributing, navigational, unused, wasted,
            savedByLeases, attempts, firstSuccess, insufficient);

        tx.Commit();
        return events.Count;
    }

    /// <summary>Rebuilds every task present in raw telemetry.</summary>
    public int RebuildAll()
    {
        if (!_store.Available)
            return 0;
        var tasks = new List<string>();
        using (var connection = _store.Connect())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT task_id FROM retrieval_events;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                tasks.Add(reader.GetString(0));
        }
        return tasks.Sum(RebuildForTask);
    }

    /// <summary>Rule 3: same symbol, same version, later in the task, neither leased nor refetched.</summary>
    private static bool IsReread(IReadOnlyList<Event> events, int index)
    {
        var e = events[index];
        if (e.SymbolId is null || e.LeaseHit || e.Refetch)
            return false;
        for (var j = 0; j < index; j++)
        {
            var prior = events[j];
            if (prior.SymbolId == e.SymbolId && prior.ContentVersion == e.ContentVersion)
                return true;
        }
        return false;
    }

    private static List<Event> LoadEvents(SqliteConnection connection, string taskId)
    {
        var events = new List<Event>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT event_id, task_id, symbol_id, content_version, lease_hit, refetch, returned_tokens, created_at
            FROM retrieval_events WHERE task_id = $t ORDER BY created_at, event_id;
            """;
        cmd.Parameters.AddWithValue("$t", taskId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            events.Add(new Event(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4) != 0, reader.GetInt32(5) != 0,
                reader.GetInt64(6), reader.GetString(7)));
        }
        return events;
    }

    private static HashSet<string> ChangedSymbolIds(SqliteConnection connection, string taskId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT changed_symbol_ids FROM patch_events WHERE task_id = $t AND applied = 1;";
        cmd.Parameters.AddWithValue("$t", taskId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<string[]>(reader.GetString(0));
                if (parsed is not null)
                    foreach (var id in parsed)
                        ids.Add(id);
            }
            catch (System.Text.Json.JsonException)
            {
                // A malformed row must not abort attribution for the whole task.
            }
        }
        return ids;
    }

    private static (int Attempts, int Insufficient, int? FirstSuccess) PatchStats(SqliteConnection connection, string taskId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN is_sufficient = 0 THEN 1 ELSE 0 END), 0),
                   MIN(CASE WHEN succeeded = 1 AND is_sufficient = 1 THEN attempt_ordinal END)
            FROM patch_events WHERE task_id = $t;
            """;
        cmd.Parameters.AddWithValue("$t", taskId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return (0, 0, null);
        return (reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt32(2));
    }

    private static void Delete(SqliteConnection connection, SqliteTransaction tx, string sql, string taskId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$t", taskId);
        cmd.ExecuteNonQuery();
    }

    private static void WriteAttribution(SqliteConnection connection, SqliteTransaction tx, Event e,
        bool usedForEdit, bool usedForNavigation, bool reread, string verdict, string now)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO derived_retrieval_attribution
                (event_id, computed_at, attribution_version, used_for_edit, used_for_navigation,
                 reread, reread_after_compaction, hops_to_edit, verdict)
            VALUES ($id, $now, $ver, $edit, $nav, $reread, $compaction, NULL, $verdict);
            """;
        cmd.Parameters.AddWithValue("$id", e.EventId);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$ver", Version);
        cmd.Parameters.AddWithValue("$edit", usedForEdit ? 1 : 0);
        cmd.Parameters.AddWithValue("$nav", usedForNavigation ? 1 : 0);
        cmd.Parameters.AddWithValue("$reread", reread ? 1 : 0);
        cmd.Parameters.AddWithValue("$compaction", e.Refetch ? 1 : 0);
        cmd.Parameters.AddWithValue("$verdict", verdict);
        cmd.ExecuteNonQuery();
    }

    private static void WriteSummary(SqliteConnection connection, SqliteTransaction tx, string taskId, string now,
        long total, long contributing, long navigational, long unused, long wasted, long savedByLeases,
        int attempts, int? firstSuccess, int insufficient)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO derived_task_summary
                (task_id, computed_at, attribution_version, total_tokens, tokens_contributing,
                 tokens_navigational, tokens_unused, tokens_wasted_rereads, tokens_saved_by_leases,
                 validation_attempts, attempts_to_first_success, insufficient_green_lights,
                 suggested_inspection_followed, outcome)
            VALUES ($t, $now, $ver, $total, $contrib, $nav, $unused, $wasted, $saved,
                    $attempts, $first, $insufficient, NULL, $outcome);
            """;
        cmd.Parameters.AddWithValue("$t", taskId);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$ver", Version);
        cmd.Parameters.AddWithValue("$total", total);
        cmd.Parameters.AddWithValue("$contrib", contributing);
        cmd.Parameters.AddWithValue("$nav", navigational);
        cmd.Parameters.AddWithValue("$unused", unused);
        cmd.Parameters.AddWithValue("$wasted", wasted);
        cmd.Parameters.AddWithValue("$saved", savedByLeases);
        cmd.Parameters.AddWithValue("$attempts", attempts);
        cmd.Parameters.AddWithValue("$first", (object?)firstSuccess ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$insufficient", insufficient);
        cmd.Parameters.AddWithValue("$outcome", firstSuccess is not null ? "succeeded" : attempts > 0 ? "unresolved" : "read_only");
        cmd.ExecuteNonQuery();
    }
}
