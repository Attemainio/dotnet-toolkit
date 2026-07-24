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
        string SymbolId, string FqName, string Kind, string Project,
        string DeclHash, string? BodyHash, string DisplayString,
        string? RefsHash = null, string? ApiHash = null, bool IsTest = false, string Modifiers = "",
        string Origin = "source", string? DocumentationId = null);

    /// <summary>Body-derived facts for one symbol, tied to the body hash they were computed from.</summary>
    public sealed record FactsRow(string SymbolId, string FactsJson, string BodyHash);

    public sealed record EdgeRow(string From, string To, string EdgeKind, string? File, int? Line);

    /// <summary>
    /// callers / tests reference counts for a symbol, derived from cached edges. Tests are the subset
    /// of callers whose own declaration carries a test attribute, so the count is real rather than
    /// assumed and cannot drift away from the caller count.
    /// </summary>
    public (int Callers, int Tests)? ReferenceCounts(string symbolId) => ReferenceCounts([symbolId]);

    /// <summary>
    /// Whether the edge cache actually covers the project a symbol lives in. Edges come from the
    /// semantic model, so a project that failed to load in MSBuild contributes none — and a caller
    /// count read from the cache would then report 0 for every symbol in it. That zero is
    /// indistinguishable from "genuinely uncalled" without this check, and reads as a fact the
    /// store does not possess: observed on a repo where a NuGet advisory blocked one project's
    /// load, reporting 0 callers for a method that had 5.
    /// </summary>
    public bool HasEdgeCoverageFor(string symbolId)
    {
        if (!_store.Available)
            return false;
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM reference_edges e
                JOIN symbols f ON f.symbol_id = e.from_symbol
                WHERE f.project = (SELECT project FROM symbols WHERE symbol_id = $id)
                LIMIT 1);
            """;
        cmd.Parameters.AddWithValue("$id", symbolId);
        return cmd.ExecuteScalar() is long and not 0;
    }

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
              -- tests is a subset of callers, derived from the caller's own is_test flag rather than
              -- from a parallel edge set. The two can no longer disagree, and tests <= callers holds
              -- by construction instead of by both being written correctly on the same pass.
              (SELECT COUNT(DISTINCT e.from_symbol) FROM reference_edges e
                 JOIN symbols f ON f.symbol_id = e.from_symbol
                 WHERE e.to_symbol IN ({list}) AND e.edge_kind = 'call' AND f.is_test = 1);
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

    /// <summary>
    /// Batch fq_name/kind/display_string lookup for many symbol ids at once — e.g. projecting every node
    /// of a get_call_hierarchy tree without one query per node. Missing ids are simply absent from the
    /// result rather than erroring, since a hierarchy walk over the edge cache can reference an id whose
    /// row was since removed by a reindex.
    /// </summary>
    public IReadOnlyDictionary<string, (string? FqName, string? Kind, string? DisplayString)> RowsFor(IReadOnlyCollection<string> symbolIds)
    {
        var result = new Dictionary<string, (string?, string?, string?)>(StringComparer.Ordinal);
        if (!_store.Available || symbolIds.Count == 0)
            return result;
        using var connection = _store.Connect();
        // Chunked below SQLite's default 999-host-parameter limit (SQLITE_LIMIT_VARIABLE_NUMBER) — a
        // hierarchy walk can pass up to CallHierarchy.HardNodeCap (3000) ids in one call.
        foreach (var chunk in symbolIds.Chunk(900))
        {
            using var cmd = connection.CreateCommand();
            var names = chunk.Select((_, i) => "$s" + i).ToList();
            cmd.CommandText = $"SELECT symbol_id, fq_name, kind, display_string FROM symbols WHERE symbol_id IN ({string.Join(',', names)});";
            var i = 0;
            foreach (var id in chunk)
                cmd.Parameters.AddWithValue("$s" + i++, id);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2), reader.GetString(3));
        }
        return result;
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
    /// The stored documentation-comment id for an external symbol — BCL/NuGet code this repo's own
    /// source calls, implements, or extends, discovered only as an edge target and never declared here.
    /// Accepts either a <c>sym_…</c> id or a fully-qualified name; null when the handle does not resolve
    /// to a row, or resolves to a source-origin row (get_symbol's live workspace path already covers that
    /// case, so this is only ever the external fallback).
    /// </summary>
    public string? ExternalDocumentationId(string handle)
    {
        if (!_store.Available || string.IsNullOrWhiteSpace(handle))
            return null;
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT documentation_id FROM symbols
            WHERE (symbol_id = $handle OR fq_name = $handle) AND origin = 'external'
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$handle", handle);
        return cmd.ExecuteScalar() as string;
    }

    public IReadOnlyList<SearchHit> Search(
        string query, IReadOnlyCollection<string>? includeKinds, IReadOnlyCollection<string>? excludeKinds, int limit,
        IReadOnlyCollection<string>? includeModifiers = null, IReadOnlyCollection<string>? excludeModifiers = null,
        string origin = "source")
    {
        if (!_store.Available || string.IsNullOrWhiteSpace(query))
            return [];

        var fts = SearchFts(query, includeKinds, excludeKinds, limit, includeModifiers, excludeModifiers, origin);
        if (fts.Count >= limit)
            return fts;

        // A short FTS result is topped up from the substring matcher rather than replaced by it. The
        // two answer different questions: FTS matches whole tokens, so "ormat" cannot reach OutputFormat,
        // while LIKE has no notion of a multi-word query. Gating the fallback on "FTS returned nothing"
        // meant a single weak token match suppressed the substring index entirely.
        var seen = fts.Select(h => h.SymbolId).ToHashSet(StringComparer.Ordinal);
        var topUp = SearchLike(query, includeKinds, excludeKinds, limit, includeModifiers, excludeModifiers, origin).Where(h => seen.Add(h.SymbolId));
        return [.. fts, .. topUp.Take(limit - fts.Count)];
    }

    private IReadOnlyList<SearchHit> SearchFts(
        string query, IReadOnlyCollection<string>? includeKinds, IReadOnlyCollection<string>? excludeKinds, int limit,
        IReadOnlyCollection<string>? includeModifiers = null, IReadOnlyCollection<string>? excludeModifiers = null,
        string origin = "source")
    {
        var match = SearchText.ForQuery(query);
        if (match is null)
            return [];

        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        var kindFilter = AppendKindFilter(cmd, "s.kind", includeKinds, excludeKinds);
        var modifierFilter = AppendModifierFilter(cmd, "s.modifiers", includeModifiers, excludeModifiers);
        var originFilter = AppendOriginFilter(cmd, "s.origin", origin);
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
            WHERE symbols_fts MATCH $match{kindFilter}{modifierFilter}{originFilter}
            ORDER BY rank, bm25(symbols_fts), length(s.fq_name), s.fq_name
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$match", match);
        cmd.Parameters.AddWithValue("$q", query);
        cmd.Parameters.AddWithValue("$prefix", query + "%");
        cmd.Parameters.AddWithValue("$limit", limit);

        // No catch here on purpose. ForQuery quotes and escapes every term it emits, so a malformed
        // MATCH expression is unreachable — the only SqliteException this can raise is a bug in the
        // statement above, and swallowing it returns an empty result that reads as "nothing matched".
        // That masked a real one once: bm25() under a GROUP BY throws, and the empty list looked like
        // a miss rather than a hard failure.
        return ReadHits(cmd);
    }

    private IReadOnlyList<SearchHit> SearchLike(
        string query, IReadOnlyCollection<string>? includeKinds, IReadOnlyCollection<string>? excludeKinds, int limit,
        IReadOnlyCollection<string>? includeModifiers = null, IReadOnlyCollection<string>? excludeModifiers = null,
        string origin = "source")
    {
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        // COLLATE NOCASE so callers are not silently punished for "method" vs "Method".
        var kindFilter = AppendKindFilter(cmd, "kind", includeKinds, excludeKinds);
        var modifierFilter = AppendModifierFilter(cmd, "modifiers", includeModifiers, excludeModifiers);
        var originFilter = AppendOriginFilter(cmd, "origin", origin);
        cmd.CommandText = $"""
            SELECT symbol_id, display_string, kind, fq_name, decl_hash,
                   CASE
                     WHEN fq_name = $q THEN 0
                     WHEN fq_name LIKE $prefix THEN 1
                     WHEN fq_name LIKE $contains THEN 2
                     ELSE 3
                   END AS rank
            FROM symbols
            WHERE fq_name LIKE $contains{kindFilter}{modifierFilter}{originFilter}
            ORDER BY rank, length(fq_name), fq_name
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$q", query);
        cmd.Parameters.AddWithValue("$prefix", query + "%");
        cmd.Parameters.AddWithValue("$contains", "%" + query + "%");
        cmd.Parameters.AddWithValue("$limit", limit);

        return ReadHits(cmd);
    }

    /// <summary>
    /// origin: "source" (default — existing behavior, external rows never surface unasked), "external",
    /// or "all". Unlike kind/modifier filtering this is a single value, not a set, so it needs no
    /// per-token parameter loop.
    /// </summary>
    private static string AppendOriginFilter(SqliteCommand cmd, string columnExpr, string origin)
    {
        if (origin is not ("source" or "external"))
            return "";
        cmd.Parameters.AddWithValue("$origin", origin);
        return $" AND {columnExpr} = $origin";
    }

private static string AppendKindFilter(
        SqliteCommand cmd, string columnExpr,
        IReadOnlyCollection<string>? includeKinds, IReadOnlyCollection<string>? excludeKinds)
    {
        var clauses = new List<string>();
        if (includeKinds is { Count: > 0 })
        {
            clauses.Add($"{columnExpr} COLLATE NOCASE IN ("
                + string.Join(',', includeKinds.Select((_, i) => "$ki" + i)) + ")");
            var i = 0;
            foreach (var k in includeKinds)
                cmd.Parameters.AddWithValue("$ki" + i++, k);
        }
        if (excludeKinds is { Count: > 0 })
        {
            clauses.Add($"{columnExpr} COLLATE NOCASE NOT IN ("
                + string.Join(',', excludeKinds.Select((_, i) => "$ke" + i)) + ")");
            var i = 0;
            foreach (var k in excludeKinds)
                cmd.Parameters.AddWithValue("$ke" + i++, k);
        }
        return clauses.Count == 0 ? "" : " AND " + string.Join(" AND ", clauses);
    }

    /// <summary>
    /// Builds the modifier-filter fragment for SearchFts/SearchLike. Unlike <see cref="AppendKindFilter"/>,
    /// include and exclude are independent filters that combine (AND) rather than one replacing the
    /// other: kind is single-valued per symbol so "OR the includes, ignore excludes if any include was
    /// given" makes sense, but modifiers are multi-valued per symbol, so "has all of these AND none of
    /// those" is the combination callers actually want. Tokens are matched as whole words against a
    /// modifiers column stored with a leading/trailing space, so a plain LIKE with space-padded
    /// wildcards is a safe word-boundary match without a tokenizer.
    /// </summary>
    private static string AppendModifierFilter(
        SqliteCommand cmd, string columnExpr,
        IReadOnlyCollection<string>? includeTokens, IReadOnlyCollection<string>? excludeTokens)
    {
        var clauses = new List<string>();
        if (includeTokens is { Count: > 0 })
        {
            var i = 0;
            foreach (var t in includeTokens)
            {
                var p = "$mi" + i++;
                clauses.Add($"{columnExpr} LIKE {p}");
                cmd.Parameters.AddWithValue(p, $"% {t} %");
            }
        }
        if (excludeTokens is { Count: > 0 })
        {
            var i = 0;
            foreach (var t in excludeTokens)
            {
                var p = "$me" + i++;
                clauses.Add($"{columnExpr} NOT LIKE {p}");
                cmd.Parameters.AddWithValue(p, $"% {t} %");
            }
        }
        return clauses.Count == 0 ? "" : " AND " + string.Join(" AND ", clauses);
    }

    /// <summary>
    /// symbolIds of every type recorded as directly implementing <paramref name="interfaceSymbolId"/>
    /// (search_index's implements filter). Direct only — mirrors get_symbol's interfaces component,
    /// not a transitive closure.
    /// </summary>
    public IReadOnlyCollection<string> ImplementorsOf(string interfaceSymbolId)
    {
        if (!_store.Available)
            return [];
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT from_symbol FROM reference_edges WHERE to_symbol = $id AND edge_kind = 'implements';";
        cmd.Parameters.AddWithValue("$id", interfaceSymbolId);
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>
    /// Reads hits in rank order, keeping the first row per symbol. The dedupe lives here rather than as
    /// a GROUP BY because FTS5 refuses bm25() in an aggregate context ("unable to use function bm25 in
    /// the requested context") — and that error is swallowed by the degradation catch below, so the
    /// query would have failed to nothing instead of loudly. Writes are the real guarantee of one row
    /// per symbol; this is the cheap backstop that keeps a duplicate from ever reaching a caller.
    /// </summary>
    private static IReadOnlyList<SearchHit> ReadHits(SqliteCommand cmd)
    {
        var hits = new List<SearchHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var symbolId = reader.GetString(0);
            if (!seen.Add(symbolId))
                continue;
            hits.Add(new SearchHit(
                symbolId, reader.GetString(1), reader.GetString(2),
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

    /// <summary>
    /// Rebuilds the FTS mirror from <c>symbols</c> when it has drifted — a row per symbol, no duplicates.
    /// The mirror is pure derived data, so a rebuild is always safe; this is what lets a cache written by
    /// an older build recover on the next start instead of needing the cache directory deleted.
    /// Returns the number of rows written, or 0 when the mirror was already correct.
    /// </summary>
    public int RepairSearchIndex()
    {
        if (!_store.Available)
            return 0;

        using var connection = _store.Connect();
        using (var check = connection.CreateCommand())
        {
            // Drift is either a count mismatch (missing or duplicated rows) — one query covers both,
            // since a correct mirror has exactly as many rows as there are symbols and no repeats.
            check.CommandText = """
                SELECT (SELECT COUNT(*) FROM symbols),
                       (SELECT COUNT(*) FROM symbols_fts),
                       (SELECT COUNT(*) FROM (SELECT symbol_id FROM symbols_fts GROUP BY symbol_id));
                """;
            using var reader = check.ExecuteReader();
            if (!reader.Read())
                return 0;
            var (symbols, ftsRows, ftsDistinct) = (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
            if (symbols == ftsRows && ftsRows == ftsDistinct)
                return 0;
        }

        var rows = new List<(string Id, string Fq)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT symbol_id, fq_name FROM symbols;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        using var tx = connection.BeginTransaction();
        Exec(connection, tx, "DELETE FROM symbols_fts;");
        using (var ins = connection.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO symbols_fts(symbol_id, search_text) VALUES ($id, $search);
                """;
            foreach (var (id, fq) in rows)
            {
                ins.Parameters.Clear();
                ins.Parameters.AddWithValue("$id", id);
                ins.Parameters.AddWithValue("$search", SearchText.ForIndex(fq));
                ins.ExecuteNonQuery();
            }
        }
        tx.Commit();
        return rows.Count;
    }

    /// <summary>The version layers already recorded for a symbol — the gate for incremental updates.</summary>
    public sealed record ExistingSymbol(string DeclHash, string? BodyHash, string? RefsHash, string? ApiHash, bool IsTest);

    /// <summary>Outcome of an incremental pass, so the caller can report how much work was skipped.</summary>
    public sealed record UpdateStats(int Updated, int Removed, int Unchanged);

    public IReadOnlyDictionary<string, ExistingSymbol> ExistingSymbols()
    {
        var existing = new Dictionary<string, ExistingSymbol>(StringComparer.Ordinal);
        if (!_store.Available)
            return existing;
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT symbol_id, decl_hash, body_hash, refs_hash, api_hash, is_test FROM symbols;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            existing[reader.GetString(0)] = new ExistingSymbol(
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                !reader.IsDBNull(5) && reader.GetInt32(5) == 1);
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
            ExecParam(connection, tx, "DELETE FROM mechanical_facts WHERE symbol_id = $id;", id);
        }
        foreach (var owner in edgeOwners)
            ExecParam(connection, tx, "DELETE FROM reference_edges WHERE from_symbol = $id;", owner);

        WriteSymbols(connection, tx, symbols.Where(s => changed.Contains(s.SymbolId)).ToList());
        WriteEdges(connection, tx, edges.Where(e => edgeOwners.Contains(e.From)).ToList());
        WriteFacts(connection, tx, facts.Where(f => changed.Contains(f.SymbolId)).ToList());

        tx.Commit();
        return new UpdateStats(changed.Count, removed.Count, existing.Count - changed.Count - removed.Count);
    }

    /// <summary>
    /// Whether a stored row disagrees with what this pass computed. The version layers are the usual
    /// answer, but IsTest is compared directly, because it is the one stored value whose input is not
    /// the declaration text.
    ///
    /// It is read from the attributes on the declaration, and an attribute only binds when the
    /// compilation resolved the framework that defines it. A workspace that failed to load — a broken
    /// restore, an SDK mismatch, a blocked package — yields a compilation where [Fact] is an error
    /// symbol, so the pass computes false for every test in the repo. The declaration text is
    /// unchanged, so no layer moves, so without this comparison the wrong value is written once and
    /// never revisited. Observed exactly that way: a degraded load flagged 0 of 105 test methods, and
    /// a healthy reload afterwards did not correct a single one.
    ///
    /// Comparing the value itself is what makes the pass self-correcting rather than merely cheap.
    ///
    /// An external row has no decl/body hash and no attribute to re-derive, so once written it is never
    /// considered moved — this index tracks only that the symbol is referenced, never how it changed.
    /// </summary>
    private static bool Moved(ExistingSymbol prior, SymbolRow next) =>
        next.Origin != "external" &&
        (prior.DeclHash != next.DeclHash
        || prior.BodyHash != next.BodyHash
        || prior.RefsHash != next.RefsHash
        || prior.ApiHash != next.ApiHash
        || prior.IsTest != next.IsTest);

    private static void DeleteSymbol(SqliteConnection connection, SqliteTransaction tx, string id)
    {
        ExecParam(connection, tx, "DELETE FROM mechanical_facts WHERE symbol_id = $id;", id);
        ExecParam(connection, tx, "DELETE FROM reference_edges WHERE from_symbol = $id OR to_symbol = $id;", id);
        ExecParam(connection, tx, "DELETE FROM symbols_fts WHERE symbol_id = $id;", id);
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
        IReadOnlyList<EdgeRow> edges,
        IReadOnlyList<FactsRow>? facts = null)
    {
        if (!_store.Available)
            return;
        using var connection = _store.Connect();
        using var tx = connection.BeginTransaction();

        Exec(connection, tx,
            "DELETE FROM mechanical_facts; DELETE FROM reference_edges; "
            + "DELETE FROM symbols_fts; DELETE FROM symbols;");

        WriteSymbols(connection, tx, symbols);
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
                (symbol_id, fq_name, kind, project, decl_hash, body_hash,
                 refs_hash, api_hash, display_string, embedding, is_test, modifiers, origin, documentation_id)
            VALUES ($id, $fq, $kind, $proj, $decl, $body, $refs, $api, $disp, NULL, $isTest, $modifiers, $origin, $docId);
            """;
        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var s in symbols)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", s.SymbolId);
            cmd.Parameters.AddWithValue("$fq", s.FqName);
            cmd.Parameters.AddWithValue("$kind", s.Kind);
            cmd.Parameters.AddWithValue("$proj", s.Project);
            cmd.Parameters.AddWithValue("$decl", s.DeclHash);
            cmd.Parameters.AddWithValue("$body", (object?)s.BodyHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$refs", (object?)s.RefsHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$api", (object?)s.ApiHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$disp", s.DisplayString);
            cmd.Parameters.AddWithValue("$ts", now);
            cmd.Parameters.AddWithValue("$search", SearchText.ForIndex(s.FqName));
            cmd.Parameters.AddWithValue("$isTest", s.IsTest ? 1 : 0);
            cmd.Parameters.AddWithValue("$modifiers", " " + s.Modifiers + " ");
            cmd.Parameters.AddWithValue("$origin", s.Origin);
            cmd.Parameters.AddWithValue("$docId", (object?)s.DocumentationId ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        WriteFts(connection, tx, symbols);
    }

    /// <summary>
    /// Mirrors symbols into the FTS table. Delete-then-insert per symbol, because the caller's
    /// INSERT OR REPLACE cannot be relied on to clear the old row (see Schema migration 7) and a
    /// stale row surfaces as a duplicate search hit.
    /// </summary>
    private static void WriteFts(SqliteConnection connection, SqliteTransaction tx, IReadOnlyList<SymbolRow> symbols)
    {
        using var del = connection.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM symbols_fts WHERE symbol_id = $id;";
        using var ins = connection.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = "INSERT INTO symbols_fts(symbol_id, search_text) VALUES ($id, $search);";

        foreach (var s in symbols)
        {
            del.Parameters.Clear();
            del.Parameters.AddWithValue("$id", s.SymbolId);
            del.ExecuteNonQuery();

            ins.Parameters.Clear();
            ins.Parameters.AddWithValue("$id", s.SymbolId);
            ins.Parameters.AddWithValue("$search", SearchText.ForIndex(s.FqName));
            ins.ExecuteNonQuery();
        }
    }


    private static void WriteEdges(SqliteConnection connection, SqliteTransaction tx, IReadOnlyList<EdgeRow> edges)
    {
        if (edges.Count == 0)
            return;
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO reference_edges (from_symbol, to_symbol, edge_kind, file, line)
            VALUES ($from, $to, $kind, $file, $line);
            """;
        foreach (var e in edges)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$from", e.From);
            cmd.Parameters.AddWithValue("$to", e.To);
            cmd.Parameters.AddWithValue("$kind", e.EdgeKind);
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
