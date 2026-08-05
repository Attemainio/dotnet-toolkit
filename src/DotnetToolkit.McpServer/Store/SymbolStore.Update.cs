using Microsoft.Data.Sqlite;

namespace DotnetToolkit.McpServer.Store;

public sealed partial class SymbolStore
{
    /// <summary>
    /// A symbol's persisted index row: identity, fingerprint hashes, and origin. <paramref name="Origin"/>
    /// is "source" for a symbol this repo's solution declares, or "external" for a minimal row minted
    /// only because some declared symbol references it (BCL/NuGet/another assembly); external rows carry
    /// no decl/body hash and use <paramref name="DocumentationId"/> (a cref-style id) in place of one.
    /// <paramref name="Generated"/> marks a symbol every one of whose declarations is source-generator
    /// output — recorded here because the file locations search_index puts on its rows come from
    /// ProjectIndex, which prunes obj/ before scanning and so has nothing to say about such a symbol.
    /// </summary>
    public sealed record SymbolRow(
        string SymbolId, string FqName, string Kind, string Project,
        string DeclHash, string? BodyHash, string DisplayString,
        string? RefsHash = null, string? ApiHash = null, bool IsTest = false, string Modifiers = "",
        string Origin = "source", string? DocumentationId = null, string? Namespace = null,
        DeclarationPlacement Placement = DeclarationPlacement.InTree);

    /// <summary>Body-derived facts for one symbol, tied to the body hash they were computed from.</summary>
    public sealed record FactsRow(string SymbolId, string FactsJson, string BodyHash);

    /// <summary>A directed reference edge between two symbols (call, implements, etc.), with an optional call-site location.</summary>
    public sealed record EdgeRow(string From, string To, string EdgeKind, string? File, int? Line);

    /// <summary>The version layers already recorded for a symbol — the gate for incremental updates.</summary>
    public sealed record ExistingSymbol(
        string DeclHash, string? BodyHash, string? RefsHash, string? ApiHash, bool IsTest, string Modifiers,
        DeclarationPlacement Placement);

    /// <summary>Outcome of an incremental pass, so the caller can report how much work was skipped.</summary>
    public sealed record UpdateStats(int Updated, int Removed, int Unchanged);

    /// <summary>Current per-layer hashes, test and generated flags, and modifier tags for every stored symbol, used to diff against an incoming rebuild.</summary>
    public IReadOnlyDictionary<string, ExistingSymbol> ExistingSymbols()
    {
        var existing = new Dictionary<string, ExistingSymbol>(StringComparer.Ordinal);
        if (!_store.Available)
            return existing;
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT symbol_id, decl_hash, body_hash, refs_hash, api_hash, is_test, modifiers, generated FROM symbols;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            existing[reader.GetString(0)] = new ExistingSymbol(
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                !reader.IsDBNull(5) && reader.GetInt32(5) == 1,
                reader.IsDBNull(6) ? "" : reader.GetString(6).Trim(),
                reader.IsDBNull(7) ? DeclarationPlacement.InTree : (DeclarationPlacement)reader.GetInt32(7));
        }
        return existing;
    }

    /// <summary>
    /// Fingerprint-gated update (spec §Maintenance): rows are rewritten only where a version layer
    /// actually moved, and facts only where the body moved. Edge DELETION (pruning a stale edge left
    /// over from a genuine content change) stays gated the same way an owner's row is, but edge
    /// INSERTION does not: every edge recomputed this pass is (re-)written via INSERT OR IGNORE
    /// regardless of whether its owner's own content moved, because an edge can become newly
    /// collectible from an otherwise-unchanged owner when the extraction logic itself changes (a new
    /// edge kind, a fix in CollectCallEdges) — gating inserts on "owner changed" left such edges
    /// permanently missing for any owner whose fingerprint predated the logic change, immune even to a
    /// full reload_workspace. INSERT OR IGNORE keeps this cheap for the (common) case where the edge
    /// was already present.
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
        // hash to compare, so their stale edges are always pruned — they are few.
        var edgeOwners = edges.Select(e => e.From).Distinct(StringComparer.Ordinal)
            .Where(from => changed.Contains(from) || !incoming.ContainsKey(from))
            .ToHashSet(StringComparer.Ordinal);

        if (changed.Count == 0 && removed.Count == 0 && edgeOwners.Count == 0 && edges.Count == 0)
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
        WriteEdges(connection, tx, edges);
        WriteFacts(connection, tx, facts.Where(f => changed.Contains(f.SymbolId)).ToList());

        tx.Commit();
        return new UpdateStats(changed.Count, removed.Count, existing.Count - changed.Count - removed.Count);
    }

    /// <summary>
    /// Whether a stored row disagrees with what this pass computed. The version layers are the usual
    /// answer, but IsTest and Modifiers are compared directly, because their inputs are not purely the
    /// declaration text.
    ///
    /// IsTest is read from the attributes on the declaration, and an attribute only binds when the
    /// compilation resolved the framework that defines it. A workspace that failed to load — a broken
    /// restore, an SDK mismatch, a blocked package — yields a compilation where [Fact] is an error
    /// symbol, so the pass computes false for every test in the repo. The declaration text is
    /// unchanged, so no layer moves, so without this comparison the wrong value is written once and
    /// never revisited. Observed exactly that way: a degraded load flagged 0 of 105 test methods, and
    /// a healthy reload afterwards did not correct a single one.
    ///
    /// Modifiers has the same failure mode for a different reason: it is a derived field added to the
    /// symbol row after the index already existed on some repos, so every row indexed before that
    /// point has an empty/stale modifiers column and an unchanged fingerprint — nothing ever rewrites
    /// it short of a full rebuild. A dotnet-toolkit self-evaluation on an external repo (PandaAI, one
    /// with a long-lived cache) found search_index's modifiers filter matching nothing at all, on every
    /// kind and every modifier token, for exactly this reason.
    ///
    /// Comparing the value itself is what makes the pass self-correcting rather than merely cheap.
    /// Generated is compared for the same reason and a third cause: it is derived from the declaration's
    /// PATH, which can change without a single token of the declaration changing.
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
        || prior.IsTest != next.IsTest
        || prior.Modifiers != next.Modifiers
        || prior.Placement != next.Placement);

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
                 refs_hash, api_hash, display_string, embedding, is_test, modifiers, origin, documentation_id, namespace, generated)
            VALUES ($id, $fq, $kind, $proj, $decl, $body, $refs, $api, $disp, NULL, $isTest, $modifiers, $origin, $docId, $ns, $generated);
            """;

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

            cmd.Parameters.AddWithValue("$isTest", s.IsTest ? 1 : 0);
            cmd.Parameters.AddWithValue("$modifiers", " " + s.Modifiers + " ");
            cmd.Parameters.AddWithValue("$origin", s.Origin);
            cmd.Parameters.AddWithValue("$docId", (object?)s.DocumentationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ns", (object?)s.Namespace ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$generated", (int)s.Placement);
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
