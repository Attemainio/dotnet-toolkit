using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// The FTS mirror behind search_index. Migration 6 kept it in sync with triggers on the assumption that
/// INSERT OR REPLACE fires the DELETE trigger — it does not unless recursive_triggers is on — so every
/// re-index duplicated a row, and rows predating the migration were never backfilled at all. These tests
/// pin both halves: writes stay one-row-per-symbol, and a drifted mirror heals.
/// </summary>
public sealed class SymbolSearchTests : IDisposable
{
    private readonly string _root;
    private readonly KnowledgeStore _store;
    private readonly SymbolStore _symbols;

    public SymbolSearchTests()
    {
        _root = Directory.CreateTempSubdirectory("symbol-search-tests-").FullName;
        var locator = new SolutionLocator(NullLogger<SolutionLocator>.Instance, _root);
        _store = new KnowledgeStore(locator, NullLogger<KnowledgeStore>.Instance);
        _symbols = new SymbolStore(_store);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private static SymbolStore.SymbolRow Row(string id, string fqName, string declHash = "d1") =>
        new(id, fqName, "Method", "public", "Proj", declHash, null, fqName, null);

    private int FtsRowCount(string symbolId)
    {
        using var connection = _store.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM symbols_fts WHERE symbol_id = $id;";
        cmd.Parameters.AddWithValue("$id", symbolId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// The original bug, at its source: re-indexing a symbol whose content moved must leave one FTS row,
    /// not two. Before the fix this returned the symbol twice from a single query.
    /// </summary>
    [Fact]
    public void ReindexingAChangedSymbolDoesNotDuplicateItsSearchRow()
    {
        var id = "sym_reindex";
        _symbols.ReplaceAll([Row(id, "Demo.Store.OutputFormat", "d1")], [], []);
        _symbols.ApplyIncremental([Row(id, "Demo.Store.OutputFormat", "d2")], [], [], []);
        _symbols.ApplyIncremental([Row(id, "Demo.Store.OutputFormat", "d3")], [], [], []);

        Assert.Equal(1, FtsRowCount(id));

        var hits = _symbols.Search("OutputFormat", null, 10);
        Assert.Single(hits);
    }

    /// <summary>A removed symbol must leave the mirror with it — the DELETE trigger is gone.</summary>
    [Fact]
    public void RemovingASymbolClearsItsSearchRow()
    {
        _symbols.ReplaceAll([Row("sym_a", "Demo.Keep"), Row("sym_b", "Demo.Drop")], [], []);
        _symbols.ApplyIncremental([Row("sym_a", "Demo.Keep")], [], [], []);

        Assert.Equal(0, FtsRowCount("sym_b"));
        Assert.Empty(_symbols.Search("Drop", null, 10));
    }

    /// <summary>
    /// A cache written by the pre-fix build: rows missing from the mirror entirely, plus duplicates.
    /// RepairSearchIndex must restore one row per symbol without touching the symbols themselves.
    /// </summary>
    [Fact]
    public void RepairSearchIndexBackfillsMissingRowsAndDropsDuplicates()
    {
        _symbols.ReplaceAll([Row("sym_a", "Demo.Alpha"), Row("sym_b", "Demo.Bravo")], [], []);

        // Reproduce the drift the old triggers produced.
        using (var connection = _store.Connect())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM symbols_fts WHERE symbol_id = 'sym_a';
                INSERT INTO symbols_fts(symbol_id, search_text) VALUES ('sym_b', 'Demo Bravo');
                """;
            cmd.ExecuteNonQuery();
        }
        Assert.Equal(0, FtsRowCount("sym_a"));
        Assert.Equal(2, FtsRowCount("sym_b"));

        // Even before repair, a duplicated mirror row must not reach the caller twice.
        Assert.Single(_symbols.Search("Bravo", null, 10));

        var repaired = _symbols.RepairSearchIndex();

        Assert.Equal(2, repaired);
        Assert.Equal(1, FtsRowCount("sym_a"));
        Assert.Equal(1, FtsRowCount("sym_b"));
        Assert.Single(_symbols.Search("Alpha", null, 10));
        Assert.Single(_symbols.Search("Bravo", null, 10));
    }

    /// <summary>A mirror that is already correct is left alone, so startup pays nothing in the normal case.</summary>
    [Fact]
    public void RepairSearchIndexIsANoOpWhenTheMirrorIsConsistent()
    {
        _symbols.ReplaceAll([Row("sym_a", "Demo.Alpha")], [], []);

        Assert.Equal(0, _symbols.RepairSearchIndex());
    }

    /// <summary>
    /// FTS matches whole tokens, so an interior fragment reaches nothing through it. Gating the substring
    /// fallback on "FTS returned zero rows" meant one weak token hit suppressed the fragment match; the
    /// fallback now tops up a short result instead of being skipped.
    /// </summary>
    [Fact]
    public void PartialFtsResultIsToppedUpFromTheSubstringMatcher()
    {
        // "Format" is a whole token of Demo.Format, so FTS reaches it. Demo.Reformat has no interior
        // capital, so its only token is "Reformat" and FTS cannot reach it for this query — but LIKE
        // '%Format%' can. The old code returned FTS's single hit and never consulted LIKE at all.
        _symbols.ReplaceAll([Row("sym_a", "Demo.Format"), Row("sym_b", "Demo.Reformat")], [], []);

        var hits = _symbols.Search("Format", null, 10);

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.FqName == "Demo.Reformat");
    }
}

/// <summary>
/// Responses travel an MCP stdio pipe, never an HTML document, so the serializer must not spend six
/// characters escaping every character that would matter in HTML.
/// </summary>
public class JsonEncodingTests
{
    [Fact]
    public void GenericsAndArrowsAreNotUnicodeEscaped()
    {
        var json = Formats.ToJson(new { source = "IReadOnlyList<SymbolLogEntry> f(x) => x + 1;" });

        Assert.Contains("IReadOnlyList<SymbolLogEntry>", json);
        Assert.Contains("=> x + 1", json);
        Assert.DoesNotContain("\\u003C", json);
        Assert.DoesNotContain("\\u003E", json);
        Assert.DoesNotContain("\\u002B", json);
    }

    /// <summary>Em dashes and section signs are everywhere in this codebase's docs; six bytes each adds up.</summary>
    [Fact]
    public void NonAsciiPunctuationSurvivesUnescaped()
    {
        var json = Formats.ToJson(new { doc = "Ranked lookup — spec §16." });

        Assert.Contains("— spec §16.", json);
        Assert.DoesNotContain("\\u2014", json);
    }

    /// <summary>Quotes still get escaped — that one JSON actually requires.</summary>
    [Fact]
    public void QuotesUseTheShortJsonEscape()
    {
        var json = Formats.ToJson(new { source = "var x = \"hi\";" });

        Assert.Contains("\\\"hi\\\"", json);
        Assert.DoesNotContain("\\u0022", json);
    }
}
