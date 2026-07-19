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
        string DeclHash, string? BodyHash, string DisplayString, string? XmlDoc,
        string? RefsHash = null, string? ApiHash = null);

    /// <summary>Body-derived facts for one symbol, tied to the body hash they were computed from.</summary>
    public sealed record FactsRow(string SymbolId, string FactsJson, string BodyHash);

    public sealed record EdgeRow(string From, string To, string EdgeKind, string? DispatchKind, string? File, int? Line);

    /// <summary>
    /// callers / tests reference counts for a symbol, derived from cached edges. Tests come from
    /// <c>test_reference</c> edges, so the count is real rather than assumed.
    /// </summary>
    public (int Callers, int Tests)? ReferenceCounts(string symbolId) => ReferenceCounts([symbolId]);

    /// <summary>
    /// Counts across a set of equivalent ids. A call made through an interface is recorded against the
    /// INTERFACE member, but Roslyn's caller search cascades to implementations — so counting only the
    /// implementation's own id under-reports exactly the callers get_references would show. Passing the
    /// symbol plus the interface members it implements keeps the two in agreement.
    /// </summary>
    public (int Callers, int Tests)? ReferenceCounts(IReadOnlyCollection<string> symbolIds)
    {
        if (!_store.Available || symbolIds.Count == 0)
            return null;
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        var names = symbolIds.Select((_, i) => "$s" + i).ToList();
        var list = string.Join(',', names);
        cmd.CommandText = $"""
            SELECT
              (SELECT COUNT(DISTINCT from_symbol) FROM reference_edges
                 WHERE to_symbol IN ({list}) AND edge_kind = 'call'),
              (SELECT COUNT(DISTINCT from_symbol) FROM reference_edges
                 WHERE to_symbol IN ({list}) AND edge_kind = 'test_reference');
            """;
        var i = 0;
        foreach (var id in symbolIds)
            cmd.Parameters.AddWithValue("$s" + i++, id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetInt32(0), reader.GetInt32(1)) : (0, 0);
    }

    /// <summary>
    /// Call targets reachable in one hop. Interface members are followed through to their registered
    /// implementations, so a slice does not dead-end at an interface boundary the way a literal call
    /// graph would.
    /// </summary>
    public IReadOnlyList<string> CallTargets(string symbolId) => Neighbors(symbolId, outgoing: true);

    /// <summary>Callers one hop away — the reverse direction for the meet-in-the-middle search.</summary>
    public IReadOnlyList<string> Callers(string symbolId) => Neighbors(symbolId, outgoing: false);

    private IReadOnlyList<string> Neighbors(string symbolId, bool outgoing)
    {
        if (!_store.Available)
            return [];
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        // 'call' plus 'implementation' so interface dispatch is traversable in both directions.
        cmd.CommandText = outgoing
            ? "SELECT DISTINCT to_symbol FROM reference_edges WHERE from_symbol = $id AND edge_kind IN ('call','implementation');"
            : "SELECT DISTINCT from_symbol FROM reference_edges WHERE to_symbol = $id AND edge_kind IN ('call','implementation');";
        cmd.Parameters.AddWithValue("$id", symbolId);

        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Display string for a symbol id, for rendering slice nodes without a semantic lookup.</summary>
    public string? DisplayFor(string symbolId)
    {
        if (!_store.Available)
            return null;
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT display_string FROM symbols WHERE symbol_id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", symbolId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>The semantic version layers (refs/api) recorded for a symbol, if the index has them.</summary>
    public (string? Refs, string? Api) LayersFor(string symbolId)
    {
        if (!_store.Available)
            return (null, null);
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT refs_hash, api_hash FROM symbols WHERE symbol_id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", symbolId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return (null, null);
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <summary>
    /// Body-derived facts, returned only when they were computed from the body hash still in effect.
    /// A moved body yields null rather than stale facts.
    /// </summary>
    public string? FactsFor(string symbolId, string? currentBodyHash)
    {
        if (!_store.Available || currentBodyHash is null)
            return null;
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT facts_json FROM mechanical_facts WHERE symbol_id = $id AND body_hash = $body LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", symbolId);
        cmd.Parameters.AddWithValue("$body", currentBodyHash);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Symbols in test projects that reference any of the given symbols (ladder level 5 input).</summary>
    public IReadOnlyList<string> TestsReferencing(IReadOnlyCollection<string> symbolIds)
    {
        if (!_store.Available || symbolIds.Count == 0)
            return [];
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        var names = symbolIds.Select((_, i) => "$s" + i).ToList();
        cmd.CommandText = $"""
            SELECT DISTINCT s.fq_name
            FROM reference_edges e
            JOIN symbols s ON s.symbol_id = e.from_symbol
            WHERE e.edge_kind = 'test_reference' AND e.to_symbol IN ({string.Join(',', names)});
            """;
        var i = 0;
        foreach (var id in symbolIds)
            cmd.Parameters.AddWithValue("$s" + i++, id);

        var tests = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tests.Add(reader.GetString(0));
        return tests;
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
    /// <summary>
    /// Ranked symbol lookup. Tries FTS first so multi-word and camel-case-interior queries work
    /// ("Ledger TryBuy TrySell" finds both methods on FIFOLedger); falls back to the substring
    /// matcher when FTS finds nothing, which keeps single-token and partial-identifier queries
    /// working on an index written before the FTS migration ran.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string query, IReadOnlyCollection<string>? kinds, int limit)
    {
        if (!_store.Available || string.IsNullOrWhiteSpace(query))
            return [];

        var fts = SearchFts(query, kinds, limit);
        return fts.Count > 0 ? fts : SearchLike(query, kinds, limit);
    }

    private IReadOnlyList<SearchHit> SearchFts(string query, IReadOnlyCollection<string>? kinds, int limit)
    {
        var match = SearchText.ForQuery(query);
        if (match is null)
            return [];

        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        var kindFilter = kinds is { Count: > 0 }
            ? " AND s.kind COLLATE NOCASE IN (" + string.Join(',', kinds.Select((_, i) => "$k" + i)) + ")"
            : "";
        // bm25 is negated by convention (more negative = better), so ordering ascending puts the
        // rows matching more of the query's terms first. The exact/prefix cases are still promoted
        // ahead of it so an exact name never loses to a better-scoring partial.
        cmd.CommandText = $"""
            SELECT s.symbol_id, s.display_string, s.kind, s.fq_name, s.decl_hash,
                   CASE
                     WHEN s.fq_name = $q THEN 0
                     WHEN s.fq_name LIKE $prefix THEN 1
                     ELSE 2
                   END AS rank
            FROM symbols_fts f
            JOIN symbols s ON s.symbol_id = f.symbol_id
            WHERE symbols_fts MATCH $match{kindFilter}
            ORDER BY rank, bm25(symbols_fts), length(s.fq_name), s.fq_name
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$match", match);
        cmd.Parameters.AddWithValue("$q", query);
        cmd.Parameters.AddWithValue("$prefix", query + "%");
        cmd.Parameters.AddWithValue("$limit", limit);
        if (kinds is { Count: > 0 })
        {
            var i = 0;
            foreach (var k in kinds)
                cmd.Parameters.AddWithValue("$k" + i++, k);
        }

        try
        {
            return ReadHits(cmd);
        }
        catch (SqliteException)
        {
            // A malformed MATCH expression must degrade to the substring matcher, never fail the call.
            return [];
        }
    }

    private IReadOnlyList<SearchHit> SearchLike(string query, IReadOnlyCollection<string>? kinds, int limit)
    {
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

        return ReadHits(cmd);
    }

    private static IReadOnlyList<SearchHit> ReadHits(SqliteCommand cmd)
    {
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

    /// <summary>The version layers already recorded for a symbol — the gate for incremental updates.</summary>
    public sealed record ExistingSymbol(string DeclHash, string? BodyHash, string? RefsHash, string? ApiHash);

    /// <summary>Outcome of an incremental pass, so the caller can report how much work was skipped.</summary>
    public sealed record UpdateStats(int Updated, int Removed, int Unchanged);

    public IReadOnlyDictionary<string, ExistingSymbol> ExistingSymbols()
    {
        var existing = new Dictionary<string, ExistingSymbol>(StringComparer.Ordinal);
        if (!_store.Available)
            return existing;
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT symbol_id, decl_hash, body_hash, refs_hash, api_hash FROM symbols;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            existing[reader.GetString(0)] = new ExistingSymbol(
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4));
        }
        return existing;
    }

    /// <summary>
    /// Fingerprint-gated update (spec §Maintenance): rows are rewritten only where a version layer
    /// actually moved, edges only for owners whose content moved, and facts only where the body moved.
    /// A formatting sweep across many files therefore updates zero semantic rows, because the trivia-blind
    /// hashes do not change.
    /// </summary>
    public UpdateStats ApplyIncremental(
        IReadOnlyList<SymbolRow> symbols,
        IReadOnlyList<(string SymbolId, string File, int StartLine, int EndLine, string? Region)> sites,
        IReadOnlyList<EdgeRow> edges,
        IReadOnlyList<FactsRow> facts)
    {
        if (!_store.Available)
            return new UpdateStats(0, 0, 0);

        var existing = ExistingSymbols();
        var incoming = symbols.ToDictionary(s => s.SymbolId, StringComparer.Ordinal);

        var changed = symbols
            .Where(s => !existing.TryGetValue(s.SymbolId, out var prior) || Moved(prior, s))
            .Select(s => s.SymbolId)
            .ToHashSet(StringComparer.Ordinal);
        var removed = existing.Keys.Where(id => !incoming.ContainsKey(id)).ToList();

        // Edge owners that are not symbols in their own right (e.g. a synthesized entry point) have no
        // hash to compare, so their edges are always refreshed — they are few.
        var edgeOwners = edges.Select(e => e.From).Distinct(StringComparer.Ordinal)
            .Where(from => changed.Contains(from) || !incoming.ContainsKey(from))
            .ToHashSet(StringComparer.Ordinal);

        if (changed.Count == 0 && removed.Count == 0 && edgeOwners.Count == 0)
            return new UpdateStats(0, 0, existing.Count);

        using var connection = _store.Connect();
        using var tx = connection.BeginTransaction();

        foreach (var id in removed)
            DeleteSymbol(connection, tx, id);

        foreach (var id in changed)
        {
            ExecParam(connection, tx, "DELETE FROM declaration_sites WHERE symbol_id = $id;", id);
            ExecParam(connection, tx, "DELETE FROM mechanical_facts WHERE symbol_id = $id;", id);
        }
        foreach (var owner in edgeOwners)
            ExecParam(connection, tx, "DELETE FROM reference_edges WHERE from_symbol = $id;", owner);

        WriteSymbols(connection, tx, symbols.Where(s => changed.Contains(s.SymbolId)).ToList());
        WriteSites(connection, tx, sites.Where(s => changed.Contains(s.SymbolId)).ToList());
        WriteEdges(connection, tx, edges.Where(e => edgeOwners.Contains(e.From)).ToList());
        WriteFacts(connection, tx, facts.Where(f => changed.Contains(f.SymbolId)).ToList());

        tx.Commit();
        return new UpdateStats(changed.Count, removed.Count, existing.Count - changed.Count - removed.Count);
    }

    private static bool Moved(ExistingSymbol prior, SymbolRow next) =>
        prior.DeclHash != next.DeclHash
        || prior.BodyHash != next.BodyHash
        || prior.RefsHash != next.RefsHash
        || prior.ApiHash != next.ApiHash;

    private static void DeleteSymbol(SqliteConnection connection, SqliteTransaction tx, string id)
    {
        ExecParam(connection, tx, "DELETE FROM mechanical_facts WHERE symbol_id = $id;", id);
        ExecParam(connection, tx, "DELETE FROM declaration_sites WHERE symbol_id = $id;", id);
        ExecParam(connection, tx, "DELETE FROM reference_edges WHERE from_symbol = $id OR to_symbol = $id;", id);
        ExecParam(connection, tx, "DELETE FROM symbols WHERE symbol_id = $id;", id);
    }

    private static void ExecParam(SqliteConnection connection, SqliteTransaction tx, string sql, string id)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Replaces the entire index in one transaction (full rebuild).</summary>
    public void ReplaceAll(IReadOnlyList<SymbolRow> symbols,
        IReadOnlyList<(string SymbolId, string File, int StartLine, int EndLine, string? Region)> sites,
        IReadOnlyList<EdgeRow> edges,
        IReadOnlyList<FactsRow>? facts = null)
    {
        if (!_store.Available)
            return;
        using var connection = _store.Connect();
        using var tx = connection.BeginTransaction();

        Exec(connection, tx,
            "DELETE FROM mechanical_facts; DELETE FROM reference_edges; DELETE FROM declaration_sites; DELETE FROM symbols;");

        WriteSymbols(connection, tx, symbols);
        WriteSites(connection, tx, sites);
        WriteEdges(connection, tx, edges);
        WriteFacts(connection, tx, facts ?? []);

        tx.Commit();
    }

    // ---- shared writers (used by both the full rebuild and the incremental pass) ----------------

    private static void WriteSymbols(SqliteConnection connection, SqliteTransaction tx, IReadOnlyList<SymbolRow> symbols)
    {
        if (symbols.Count == 0)
            return;
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO symbols
                (symbol_id, fq_name, kind, accessibility, project, decl_hash, body_hash,
                 refs_hash, api_hash, display_string, xml_doc, embedding, updated_at, search_text)
            VALUES ($id, $fq, $kind, $acc, $proj, $decl, $body, $refs, $api, $disp, $doc, NULL, $ts, $search);
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
            cmd.Parameters.AddWithValue("$refs", (object?)s.RefsHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$api", (object?)s.ApiHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$disp", s.DisplayString);
            cmd.Parameters.AddWithValue("$doc", (object?)s.XmlDoc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ts", now);
            cmd.Parameters.AddWithValue("$search", SearchText.ForIndex(s.FqName));
            cmd.ExecuteNonQuery();
        }
    }

    private static void WriteSites(SqliteConnection connection, SqliteTransaction tx,
        IReadOnlyList<(string SymbolId, string File, int StartLine, int EndLine, string? Region)> sites)
    {
        if (sites.Count == 0)
            return;
        using var cmd = connection.CreateCommand();
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

    private static void WriteEdges(SqliteConnection connection, SqliteTransaction tx, IReadOnlyList<EdgeRow> edges)
    {
        if (edges.Count == 0)
            return;
        using var cmd = connection.CreateCommand();
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

    private static void WriteFacts(SqliteConnection connection, SqliteTransaction tx, IReadOnlyList<FactsRow> facts)
    {
        if (facts.Count == 0)
            return;
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO mechanical_facts (symbol_id, facts_json, body_hash)
            VALUES ($id, $facts, $body);
            """;
        foreach (var f in facts)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", f.SymbolId);
            cmd.Parameters.AddWithValue("$facts", f.FactsJson);
            cmd.Parameters.AddWithValue("$body", f.BodyHash);
            cmd.ExecuteNonQuery();
        }
    }

    private static void Exec(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
