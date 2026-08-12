using Microsoft.Data.Sqlite;

namespace DotnetToolkit.McpServer.Store;

/// <summary>
/// Read/write access to the symbol index and reference-edge cache (spec §18). Writes come from the
/// background <c>SymbolIndexBuilder</c>; reads serve <c>referenceCounts</c> and the search fallback.
/// </summary>
public sealed partial class SymbolStore
{
    private readonly IKnowledgeStore _store;

    public SymbolStore(IKnowledgeStore store) => _store = store;

    public bool Available => _store.Available;


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

    /// <summary>The five reference counts a hit's refs column renders, for one symbol.</summary>
    /// <param name="Callers">Members whose body calls this one.</param>
    /// <param name="Callees">Distinct symbols this member's own body calls.</param>
    /// <param name="Implementations">Types implementing this interface or deriving from this class, and members implementing this interface member.</param>
    /// <param name="Overrides">Members overriding this virtual or abstract one.</param>
    /// <param name="Tests">The subset of <paramref name="Callers"/> whose declaration carries a test attribute.</param>
    public readonly record struct RefCounts(int Callers, int Callees, int Implementations, int Overrides, int Tests);

    /// <summary>Reference counts for many symbols at once, keyed by symbol id.</summary>
    /// <param name="symbolIds">The symbols to count references for.</param>
    /// <returns>
    /// One entry per id whose project the edge cache actually covers, carrying 0 where that symbol genuinely
    /// has none; or <c>null</c> when no knowledge store is available or the cache covers none of them.
    /// An id ABSENT from a non-null result was not measured, which is a different answer from zero and must
    /// never be reported as one -- present-means-measured is what keeps this in step with
    /// <see cref="HasEdgeCoverageFor"/>, which the single-symbol path applies for the same reason.
    /// </returns>
    /// <remarks>
    /// A hit's refs column needs one count per hit, and asking <see cref="ReferenceCounts(string)"/> per row
    /// opened a connection per row -- a 200-hit page would become 200 round trips, the N+1 shape
    /// standards/antipatterns.md names. Three grouped queries answer the whole page instead, and that stays
    /// three however many hits it holds. Counts LEFT JOIN symbols so an edge whose other end has no row of its
    /// own still counts, which is what keeps this from quietly under-reporting.
    /// </remarks>
    public IReadOnlyDictionary<string, RefCounts>? ReferenceCountsFor(IReadOnlyCollection<string> symbolIds)
    {
        if (!_store.Available || symbolIds.Count == 0)
            return null;
        using var connection = _store.Connect();
        var list = string.Join(',', symbolIds.Select((_, i) => "$s" + i));

        void Bind(SqliteCommand command)
        {
            var n = 0;
            foreach (var id in symbolIds)
                command.Parameters.AddWithValue("$s" + n++, id);
        }

        // Which of these ids sit in a project the edge cache covers at all -- HasEdgeCoverageFor's check,
        // computed once for the whole page instead of once per row. A project that failed to load in MSBuild
        // contributes no edges, and without this every symbol in it reads as a confident "0 callers".
        var covered = new HashSet<string>(StringComparer.Ordinal);
        using (var coverage = connection.CreateCommand())
        {
            coverage.CommandText = $"""
                SELECT s.symbol_id
                  FROM symbols s
                 WHERE s.symbol_id IN ({list})
                   AND s.project IN (SELECT DISTINCT f.project
                                       FROM reference_edges e
                                       JOIN symbols f ON f.symbol_id = e.from_symbol);
                """;
            Bind(coverage);
            using var reader = coverage.ExecuteReader();
            while (reader.Read())
                covered.Add(reader.GetString(0));
        }
        if (covered.Count == 0)
            return null;

        // Everything pointing AT these symbols, in ONE grouped pass: the four counts read the same rows and
        // differ only by edge_kind, so a query each would re-scan the same index four times. Implementations
        // folds 'inherits' in with 'implements' so a class's derived types are counted alongside an
        // interface's implementers, matching what get_symbol's implementations component already reports.
        var incoming = new Dictionary<string, (int Callers, int Tests, int Implementations, int Overrides)>(StringComparer.Ordinal);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT e.to_symbol,
                       COUNT(DISTINCT CASE WHEN e.edge_kind = 'call' THEN e.from_symbol END),
                       COUNT(DISTINCT CASE WHEN e.edge_kind = 'call' AND f.is_test = 1 THEN e.from_symbol END),
                       COUNT(DISTINCT CASE WHEN e.edge_kind IN ('implements', 'inherits') THEN e.from_symbol END),
                       COUNT(DISTINCT CASE WHEN e.edge_kind = 'overrides' THEN e.from_symbol END)
                  FROM reference_edges e
                  LEFT JOIN symbols f ON f.symbol_id = e.from_symbol
                 WHERE e.to_symbol IN ({list})
                 GROUP BY e.to_symbol;
                """;
            Bind(cmd);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                incoming[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
        }

        // Outgoing calls are keyed the other way round, so they cannot ride along with the pass above.
        var callees = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT e.from_symbol, COUNT(DISTINCT e.to_symbol)
                  FROM reference_edges e
                 WHERE e.from_symbol IN ({list}) AND e.edge_kind = 'call'
                 GROUP BY e.from_symbol;
                """;
            Bind(cmd);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                callees[reader.GetString(0)] = reader.GetInt32(1);
        }

        // A covered id with no edge row genuinely has zero of everything; an uncovered one is omitted.
        return covered.ToDictionary(
            id => id,
            id =>
            {
                var (callers, tests, implementations, overrides) = incoming.GetValueOrDefault(id);
                return new RefCounts(callers, callees.GetValueOrDefault(id), implementations, overrides, tests);
            },
            StringComparer.Ordinal);
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

    /// <summary>One ranked result row from a full-text or LIKE search over the symbol index.</summary>
    /// <remarks>
    /// <c>Generated</c> is what lets search_index say why a hit has no file location: the row exists
    /// because the compilation declares it, while ProjectIndex — which supplies the locations — prunes the
    /// obj/ tree a generator writes to. Without it that row reads as an indexing failure.
    /// </remarks>
    public sealed record SearchHit(
        string SymbolId, string DisplayString, string Kind, string FqName, string DeclHash, int Rank,
        string? Namespace = null, DeclarationPlacement Placement = DeclarationPlacement.InTree,
        string? Modifiers = null);

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

    /// <summary>Full-text symbol search, topped up with a substring fallback when FTS returns too few hits.</summary>
    /// <param name="query">Free-text query over symbol names.</param>
    /// <param name="includeKinds">Symbol kinds to include; null/empty means no kind filtering.</param>
    /// <param name="excludeKinds">Symbol kinds to exclude.</param>
    /// <param name="limit">Maximum hits to return.</param>
    /// <param name="includeModifiers">Modifiers a hit must have.</param>
    /// <param name="excludeModifiers">Modifiers a hit must not have.</param>
    /// <param name="origin">"source" (this repo's own symbols) or "external" (BCL/NuGet symbols referenced from it).</param>
    /// <returns>Up to <paramref name="limit"/> hits, deduplicated by symbol id. Empty when the store is unavailable or the query is blank.</returns>
    /// <remarks>
    /// A MULTI-TERM query gives each term a floor share of the budget before the globally ranked union
    /// spends what is left. The union alone is spent in rank order across every term at once, so a term
    /// with far fewer name-matches than its neighbours could take no slots at all: four exact type names
    /// at the default limit answered for two of them, and the other two lost every slot to partial
    /// matches thrown up by the same query. search_index's own advice is to put every term in one call,
    /// so that one call has to answer for each of them.
    /// </remarks>
    public IReadOnlyList<SearchHit> Search(
        string query, IReadOnlyCollection<string>? includeKinds, IReadOnlyCollection<string>? excludeKinds, int limit,
        IReadOnlyCollection<string>? includeModifiers = null, IReadOnlyCollection<string>? excludeModifiers = null,
        string origin = "source")
    {
        if (!_store.Available || string.IsNullOrWhiteSpace(query))
            return [];

        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length < 2)
            return Ranked(query, limit);

        var floor = Math.Max(1, limit / terms.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<SearchHit>(limit);

        // Floors first, in the order the caller wrote the terms.
        foreach (var term in terms)
            Absorb(Ranked(term, floor));

        // Then the globally ranked union spends the remainder, which is what keeps a multi-term query
        // ranked ACROSS its terms rather than being a bare concatenation of per-term lists.
        if (results.Count < limit)
            Absorb(Ranked(query, limit));

        return results;

        void Absorb(IReadOnlyList<SearchHit> hits)
        {
            foreach (var hit in hits)
            {
                if (results.Count >= limit)
                    return;
                if (seen.Add(hit.SymbolId))
                    results.Add(hit);
            }
        }

        IReadOnlyList<SearchHit> Ranked(string text, int take)
        {
            var fts = SearchFts(text, includeKinds, excludeKinds, take, includeModifiers, excludeModifiers, origin);
            if (fts.Count >= take)
                return fts;

            // A short FTS result is topped up from the substring matcher rather than replaced by it. The
            // two answer different questions: FTS matches whole tokens, so "ormat" cannot reach OutputFormat,
            // while LIKE has no notion of a multi-word query. Gating the fallback on "FTS returned nothing"
            // meant a single weak token match suppressed the substring index entirely.
            var ftsSeen = fts.Select(h => h.SymbolId).ToHashSet(StringComparer.Ordinal);
            var topUp = SearchLike(text, includeKinds, excludeKinds, take, includeModifiers, excludeModifiers, origin)
                .Where(h => ftsSeen.Add(h.SymbolId));
            return [.. fts, .. topUp.Take(take - fts.Count)];
        }
    }

    /// <summary>Source symbols whose own unqualified name is within a small edit distance of <paramref name="name"/>.</summary>
    /// <param name="name">The unqualified name that resolved to nothing.</param>
    /// <param name="limit">Maximum candidates to return.</param>
    /// <returns>The closest candidates, nearest first; empty when nothing is close enough to be a likely typo.</returns>
    /// <remarks>
    /// <see cref="Search"/> matches whole FTS tokens or a literal substring, and a MISSPELLING reaches
    /// neither: "RegistryNormalzr" shares no token with "RegistryNormalizer" and is not a substring of it,
    /// so the near-miss suggestion this was meant to feed never fired on the one input that needs it. This
    /// is the fallback for that case alone. It scans the source rows once, which is affordable precisely
    /// because nothing reaches it until a symbol has already failed to resolve.
    /// </remarks>
    public IReadOnlyList<SearchHit> NearNames(string name, int limit)
    {
        // Beyond three edits a "correction" is a different word rather than a typo, and a caller is better
        // served by a bare miss than by confident nonsense.
        const int maxDistance = 3;
        const int minLength = 3;

        if (!_store.Available || name.Length < minLength)
            return [];

        var tolerance = Math.Clamp(name.Length / 4, 1, maxDistance);

        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        // rank is a constant here: these rows are ordered by edit distance below, not by the exact/prefix
        // ladder the two real search paths share this projection with.
        cmd.CommandText = """
            SELECT symbol_id, display_string, kind, fq_name, decl_hash, 3 AS rank, namespace, generated, modifiers
            FROM symbols
            WHERE origin = 'source' AND length(fq_name) >= $minLen;
            """;
        cmd.Parameters.AddWithValue("$minLen", Math.Max(0, name.Length - tolerance));

        var scored = new List<(int Distance, SearchHit Hit)>();
        foreach (var hit in ReadHits(cmd))
        {
            var candidate = UnqualifiedName(hit.FqName);
            if (Math.Abs(candidate.Length - name.Length) > tolerance)
                continue;
            var distance = EditDistance(candidate, name, tolerance);
            if (distance <= tolerance)
                scored.Add((distance, hit));
        }

        return [.. scored
            .OrderBy(s => s.Distance)
            .ThenBy(s => s.Hit.FqName.Length)
            .ThenBy(s => s.Hit.FqName, StringComparer.Ordinal)
            .Take(limit)
            .Select(s => s.Hit)];
    }

    /// <summary>The last dotted segment of a stored name, with any parameter list dropped first.</summary>
    /// <param name="fqName">A stored fully-qualified name, which for a method carries its parameter list.</param>
    /// <returns>The bare declared name.</returns>
    /// <remarks>
    /// The parameter list has to go first: the dots inside "Ns.Type.Method(System.String)" are later than
    /// the one separating the name, so splitting on the last dot alone would yield "String)".
    /// </remarks>
    private static string UnqualifiedName(string fqName)
    {
        var bare = fqName.IndexOf('(', StringComparison.Ordinal) is var open && open >= 0 ? fqName[..open] : fqName;
        return bare[(bare.LastIndexOf('.') + 1)..];
    }

    /// <summary>Levenshtein edit distance between two names, compared case-insensitively.</summary>
    /// <param name="candidate">The stored name being scored.</param>
    /// <param name="target">The name the caller asked for.</param>
    /// <param name="tolerance">The distance beyond which the exact value stops mattering.</param>
    /// <returns>The distance, or any value above <paramref name="tolerance"/> once the row cannot qualify.</returns>
    /// <remarks>
    /// Two rolling rows rather than a full matrix, and the row is abandoned as soon as every cell in it
    /// exceeds the tolerance — the common case for a name that is simply unrelated, which is nearly every
    /// row in the store.
    /// </remarks>
    internal static int EditDistance(string candidate, string target, int tolerance)
    {
        var previous = new int[target.Length + 1];
        var current = new int[target.Length + 1];
        for (var j = 0; j <= target.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= candidate.Length; i++)
        {
            current[0] = i;
            var best = current[0];
            for (var j = 1; j <= target.Length; j++)
            {
                var swap = char.ToLowerInvariant(candidate[i - 1]) == char.ToLowerInvariant(target[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + swap);
                best = Math.Min(best, current[j]);
            }

            if (best > tolerance)
                return tolerance + 1;
            (previous, current) = (current, previous);
        }

        return previous[target.Length];
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
                   END AS rank,
                   s.namespace,
                   s.generated,
                       s.modifiers
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
                   END AS rank,
                   namespace,
                   generated,
                       modifiers
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
                reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? DeclarationPlacement.InTree : (DeclarationPlacement)reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
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

}
