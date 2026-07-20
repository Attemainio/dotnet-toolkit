using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Direct coverage of the rename-chain walk (old_symbol_id + ResolveIdChain's recursive CTE) added for
/// get_symbol's recentLog -- no MSBuild needed, just the SQLite-backed store itself.
/// </summary>
public sealed class FeatureLogStoreTests : IDisposable
{
    private readonly string _root;
    private readonly FeatureLogStore _featureLog;

    public FeatureLogStoreTests()
    {
        _root = Directory.CreateTempSubdirectory("featurelog-tests-").FullName;
        var locator = new SolutionLocator(NullLogger<SolutionLocator>.Instance, _root);
        var store = new KnowledgeStore(locator, NullLogger<KnowledgeStore>.Instance);
        _featureLog = new FeatureLogStore(store);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private static FeatureLogStore.LogSymbol Symbol(string id, string? oldId) =>
        new(id, oldId, ["signature"], "renamed", "decl:old", "decl:new", "non-breaking");

    [Fact]
    public void ResolveIdChain_SingleHop_ReturnsBothIds()
    {
        _featureLog.Append(new FeatureLogStore.LogEntry(
            "tsk_a", null, null, "renamed A to B", [], null, [Symbol("sym_B", "sym_A")]));

        var chain = _featureLog.ResolveIdChain("sym_B");

        Assert.Equal(2, chain.Count);
        Assert.Contains("sym_B", chain);
        Assert.Contains("sym_A", chain);
    }

    [Fact]
    public void ResolveIdChain_MultiHop_WalksTheFullChain()
    {
        // A -> B -> C: two separate renames recorded across two separate log entries.
        _featureLog.Append(new FeatureLogStore.LogEntry(
            "tsk_a", null, null, "renamed A to B", [], null, [Symbol("sym_B", "sym_A")]));
        _featureLog.Append(new FeatureLogStore.LogEntry(
            "tsk_b", null, null, "renamed B to C", [], null, [Symbol("sym_C", "sym_B")]));

        var chain = _featureLog.ResolveIdChain("sym_C");

        Assert.Equal(3, chain.Count);
        Assert.Contains("sym_C", chain);
        Assert.Contains("sym_B", chain);
        Assert.Contains("sym_A", chain);
    }

    [Fact]
    public void ResolveIdChain_NoChain_ReturnsJustItself()
    {
        _featureLog.Append(new FeatureLogStore.LogEntry(
            "tsk_a", null, null, "body edit", [], null, [Symbol("sym_X", null)]));

        var chain = _featureLog.ResolveIdChain("sym_X");

        Assert.Equal(["sym_X"], chain);
    }

    [Fact]
    public void ResolveIdChain_UnknownSymbol_ReturnsJustItself()
    {
        var chain = _featureLog.ResolveIdChain("sym_never_logged");

        Assert.Equal(["sym_never_logged"], chain);
    }

    [Fact]
    public void RecentForSymbol_WithChain_SurfacesHistoryFromEveryPriorId()
    {
        // Log history was recorded before the rename (under sym_A) and the rename itself (under sym_B) --
        // the whole point of the chain is that a query against the current id (sym_B) still finds both.
        _featureLog.Append(new FeatureLogStore.LogEntry(
            "tsk_a", null, null, "original body change", [], null, [Symbol("sym_A", null)]));
        _featureLog.Append(new FeatureLogStore.LogEntry(
            "tsk_b", null, null, "renamed A to B", [], null, [Symbol("sym_B", "sym_A")]));

        var chain = _featureLog.ResolveIdChain("sym_B");
        var entries = _featureLog.RecentForSymbol(chain);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Intent == "original body change");
        Assert.Contains(entries, e => e.Intent == "renamed A to B");
    }
    [Fact]
    public void RecentForSymbolWithChain_SurfacesHistoryFromEveryPriorId()
    {
        _featureLog.Append(new FeatureLogStore.LogEntry(
            "tsk_a", null, null, "original body change", [], null, [Symbol("sym_A", null)]));
        _featureLog.Append(new FeatureLogStore.LogEntry(
            "tsk_b", null, null, "renamed A to B", [], null, [Symbol("sym_B", "sym_A")]));

        var entries = _featureLog.RecentForSymbolWithChain("sym_B");

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Intent == "original body change");
        Assert.Contains(entries, e => e.Intent == "renamed A to B");
    }
}
