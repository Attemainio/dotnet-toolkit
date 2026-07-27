using DotnetToolkit.McpServer.Devlog;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

public sealed class DevlogMigrationTests : IDisposable
{
    private readonly string _root;
    private readonly DevlogStore _devlog;
    private readonly FeatureLogStore _featureLog;

    public DevlogMigrationTests()
    {
        _root = Directory.CreateTempSubdirectory("devlog-migration-tests-").FullName;
        var locator = new SolutionLocator(NullLogger<SolutionLocator>.Instance, _root);
        _devlog = new DevlogStore(locator, NullLogger<DevlogStore>.Instance);
        var store = new KnowledgeStore(locator, NullLogger<KnowledgeStore>.Instance);
        _featureLog = new FeatureLogStore(store);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void EmptyDevlog_ImportsNothing()
    {
        var imported = DevlogMigration.Run(_devlog, _featureLog, NullLogger.Instance);

        Assert.Equal(0, imported);
        Assert.Equal(0, _featureLog.EntryCount());
    }

    [Fact]
    public void NonEmptyDevlog_ImportsEachEntryOnceAndIsIdempotent()
    {
        _devlog.Add(
            "Fixed decimal rounding in price calculation",
            "Replaced double math with decimal in PriceCalculator.Total.",
            "Totals drifted by cents on large orders.",
            null,
            "decimal end-to-end; added regression test.",
            "done",
            ["PriceCalculator"],
            "Ordering",
            ["bug"]);

        var firstRun = DevlogMigration.Run(_devlog, _featureLog, NullLogger.Instance);
        Assert.Equal(1, firstRun);
        Assert.Equal(1, _featureLog.EntryCount());

        // The log is no longer empty, so a second run must no-op rather than duplicate the entry.
        var secondRun = DevlogMigration.Run(_devlog, _featureLog, NullLogger.Instance);
        Assert.Equal(0, secondRun);
        Assert.Equal(1, _featureLog.EntryCount());
    }
}
