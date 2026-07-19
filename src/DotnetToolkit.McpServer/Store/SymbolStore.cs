using Microsoft.Data.Sqlite;

namespace DotnetToolkit.McpServer.Store;

/// <summary>
/// Read/write access to the symbol index and reference-edge cache (spec §18). Writes come from the
/// background <c>SymbolIndexBuilder</c>; reads serve <c>referenceCounts</c> and the search fallback.
/// </summary>
public sealed class SymbolStore
{
    private readonly KnowledgeStore _store;

    public SymbolStore(KnowledgeStore store) => _store = store;

    public bool Available => _store.Available;

    public sealed record SymbolRow(
        string SymbolId, string FqName, string Kind, string Accessibility, string Project,
        string DeclHash, string? BodyHash, string DisplayString, string? XmlDoc);

    public sealed record EdgeRow(string From, string To, string EdgeKind, string? DispatchKind, string? File, int? Line);

    /// <summary>callers / tests reference counts for a symbol, derived from cached call edges.</summary>
    public (int Callers, int Tests)? ReferenceCounts(string symbolId, ISet<string> testProjectSymbols)
    {
        if (!_store.Available)
            return null;
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(DISTINCT from_symbol) FROM reference_edges WHERE to_symbol = $id AND edge_kind = 'call';";
        cmd.Parameters.AddWithValue("$id", symbolId);
        var callers = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        // Test attribution is refined in later phases; for MVP tests==0 unless test edges are recorded.
        return (callers, 0);
    }

    public sealed record SearchHit(string SymbolId, string DisplayString, string Kind, string FqName, string DeclHash, int Rank);

    /// <summary>
    /// Resolves a <c>sym_…</c> identifier back to its fully-qualified name. symbolId is a one-way hash,
    /// so this lookup is what makes every symbolId the server hands out (search hits, reference items,
    /// suggestedInspection entries) directly usable as a retrieval target.
    /// </summary>
    public string? FqNameFor(string symbolId)
    {
        if (!_store.Available || string.IsNullOrWhiteSpace(symbolId))
            return null;
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT fq_name FROM symbols WHERE symbol_id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", symbolId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// Ranked symbol lookup for <c>search_index</c> (spec §16). MVP backend: substring match over
    /// fully-qualified name, ranked exact &gt; prefix &gt; contains. The FTS5 upgrade is Phase 7;
    /// the response schema is already final so callers do not change when the backend does.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string query, IReadOnlyCollection<string>? kinds, int limit)
    {
        if (!_store.Available || string.IsNullOrWhiteSpace(query))
            return [];
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        // COLLATE NOCASE so callers are not silently punished for "method" vs "Method".
        var kindFilter = kinds is { Count: > 0 }
            ? " AND kind COLLATE NOCASE IN (" + string.Join(',', kinds.Select((_, i) => "$k" + i)) + ")"
            : "";
        cmd.CommandText = $"""
            SELECT symbol_id, display_string, kind, fq_name, decl_hash,
                   CASE
                     WHEN fq_name = $q THEN 0
                     WHEN fq_name LIKE $prefix THEN 1
                     WHEN fq_name LIKE $contains THEN 2
                     ELSE 3
                   END AS rank
            FROM symbols
            WHERE fq_name LIKE $contains{kindFilter}
            ORDER BY rank, length(fq_name), fq_name
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$q", query);
        cmd.Parameters.AddWithValue("$prefix", query + "%");
        cmd.Parameters.AddWithValue("$contains", "%" + query + "%");
        cmd.Parameters.AddWithValue("$limit", limit);
        if (kinds is { Count: > 0 })
        {
            var i = 0;
            foreach (var k in kinds)
                cmd.Parameters.AddWithValue("$k" + i++, k);
        }

        var hits = new List<SearchHit>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            hits.Add(new SearchHit(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt32(5)));
        }
        return hits;
    }

    /// <summary>Total symbol rows — used to report index readiness / staleness.</summary>
    public int SymbolCount()
    {
        if (!_store.Available)
            return 0;
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM symbols;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>Replaces the entire index in one transaction (full rebuild). Incremental, hash-gated
    /// invalidation is a Phase 3 refinement.</summary>
    public void ReplaceAll(IReadOnlyList<SymbolRow> symbols,
        IReadOnlyList<(string SymbolId, string File, int StartLine, int EndLine, string? Region)> sites,
        IReadOnlyList<EdgeRow> edges)
    {
        if (!_store.Available)
            return;
        using var connection = _store.Connect();
        using var tx = connection.BeginTransaction();

        Exec(connection, tx, "DELETE FROM reference_edges; DELETE FROM declaration_sites; DELETE FROM symbols;");

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO symbols
                    (symbol_id, fq_name, kind, accessibility, project, decl_hash, body_hash,
                     refs_hash, api_hash, display_string, xml_doc, embedding, updated_at)
                VALUES ($id, $fq, $kind, $acc, $proj, $decl, $body, NULL, NULL, $disp, $doc, NULL, $ts);
                """;
            var now = DateTimeOffset.UtcNow.ToString("O");
            foreach (var s in symbols)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$id", s.SymbolId);
                cmd.Parameters.AddWithValue("$fq", s.FqName);
                cmd.Parameters.AddWithValue("$kind", s.Kind);
                cmd.Parameters.AddWithValue("$acc", s.Accessibility);
                cmd.Parameters.AddWithValue("$proj", s.Project);
                cmd.Parameters.AddWithValue("$decl", s.DeclHash);
                cmd.Parameters.AddWithValue("$body", (object?)s.BodyHash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$disp", s.DisplayString);
                cmd.Parameters.AddWithValue("$doc", (object?)s.XmlDoc ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ts", now);
                cmd.ExecuteNonQuery();
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO declaration_sites (symbol_id, file, start_line, end_line, region)
                VALUES ($id, $file, $start, $end, $region);
                """;
            foreach (var site in sites)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$id", site.SymbolId);
                cmd.Parameters.AddWithValue("$file", site.File);
                cmd.Parameters.AddWithValue("$start", site.StartLine);
                cmd.Parameters.AddWithValue("$end", site.EndLine);
                cmd.Parameters.AddWithValue("$region", (object?)site.Region ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO reference_edges (from_symbol, to_symbol, edge_kind, dispatch_kind, file, line)
                VALUES ($from, $to, $kind, $dispatch, $file, $line);
                """;
            foreach (var e in edges)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$from", e.From);
                cmd.Parameters.AddWithValue("$to", e.To);
                cmd.Parameters.AddWithValue("$kind", e.EdgeKind);
                cmd.Parameters.AddWithValue("$dispatch", (object?)e.DispatchKind ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$file", (object?)e.File ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$line", (object?)e.Line ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    private static void Exec(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
