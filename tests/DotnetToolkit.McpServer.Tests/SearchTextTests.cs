using DotnetToolkit.McpServer.Store;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

public class SearchTextTests
{
    [Fact]
    public void ForIndex_SplitsDottedSegmentsAndCamelCase()
    {
        var text = SearchText.ForIndex("PandaAI.Core.Strategy.FIFOLedger.TryBuy");

        // Whole segments stay searchable...
        Assert.Contains("FIFOLedger", text.Split(' '));
        Assert.Contains("TryBuy", text.Split(' '));
        // ...and so do their parts, which is what lets "Ledger" find FIFOLedger.
        Assert.Contains("Ledger", text.Split(' '));
        Assert.Contains("Try", text.Split(' '));
        Assert.Contains("Buy", text.Split(' '));
    }

    /// <summary>
    /// Acronym runs must not shatter into single letters: FIFOLedger is FIFO + Ledger, and a
    /// per-character split would make the index noise and rank everything alike.
    /// </summary>
    [Fact]
    public void ForIndex_KeepsAcronymRunsIntact()
    {
        var tokens = SearchText.ForIndex("Lib.FIFOLedger").Split(' ');

        Assert.Contains("FIFO", tokens);
        Assert.DoesNotContain("F", tokens);
        Assert.DoesNotContain("I", tokens);
    }

    /// <summary>
    /// The failure this phase exists to fix. The old matcher ran `fq_name LIKE '%Ledger TryBuy
    /// TrySell%'`, which no symbol name can contain, so a real multi-word query returned nothing.
    /// Terms must be OR-ed so each one can find its own symbol.
    /// </summary>
    [Fact]
    public void ForQuery_OrsTermsSoMultiWordQueriesCanMatch()
    {
        var match = SearchText.ForQuery("Ledger TryBuy TrySell");

        Assert.NotNull(match);
        Assert.Contains(" OR ", match);
        Assert.Contains("\"Ledger\"*", match);
        Assert.Contains("\"TryBuy\"*", match);
        Assert.Contains("\"TrySell\"*", match);
    }

    [Fact]
    public void ForQuery_QuotesTermsSoFtsOperatorsAreNotInterpreted()
    {
        // NOT and OR are FTS5 operators; unquoted they would change the query's meaning.
        var match = SearchText.ForQuery("NOT OR")!;

        Assert.Contains("\"NOT\"*", match);
        Assert.Contains("\"OR\"*", match);
    }

    [Fact]
    public void ForQuery_ReturnsNullWhenNothingUsable()
    {
        Assert.Null(SearchText.ForQuery("  .  "));
        Assert.Null(SearchText.ForQuery("x"));   // single char is below the useful-prefix floor
    }
}
