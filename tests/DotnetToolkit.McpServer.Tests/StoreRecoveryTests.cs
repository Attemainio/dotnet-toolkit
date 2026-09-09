using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Covers what happens to a knowledge store whose file is damaged: that the damage is recognised, that
/// the store is rebuilt rather than left serving a frozen snapshot, and that the development log --
/// the one table nothing can regenerate -- survives the rebuild.
/// </summary>
public sealed class StoreRecoveryTests : IDisposable
{
    private readonly string _root;
    private readonly SolutionLocator _locator;

    public StoreRecoveryTests()
    {
        _root = Directory.CreateTempSubdirectory("store-recovery-tests-").FullName;
        _locator = new SolutionLocator(NullLogger<SolutionLocator>.Instance, _root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private KnowledgeStore Open() => new(_locator, NullLogger<KnowledgeStore>.Instance);

    private static string AppendLogEntry(KnowledgeStore store, string intent) =>
        new FeatureLogStore(store).Append(new FeatureLogStore.LogEntry(
            "tsk_recovery", null, null, intent, [], null,
            [new FeatureLogStore.LogSymbol("sym_a", null, ["signature"], "changed", "decl:old", "decl:new", "non-breaking")]));

    /// <summary>The quarantined databases, without the <c>-wal</c>/<c>-shm</c> sidecars moved with them.</summary>
    private string[] QuarantinedStores() =>
        [.. Directory.GetFiles(_locator.CacheDir, "knowledge.db.corrupt-*")
            .Where(file => !file.EndsWith("-wal", StringComparison.Ordinal)
                        && !file.EndsWith("-shm", StringComparison.Ordinal))];

    [Fact]
    public void IsDamage_RecognisesCorruptionAndNothingElse()
    {
        // SqliteException's error code is the constructor's second argument; 11 is SQLITE_CORRUPT,
        // 26 SQLITE_NOTADB, and 19 a constraint violation, which is an ordinary failure.
        Assert.True(StoreCorruption.IsDamage(new SqliteException("malformed", 11)));
        Assert.True(StoreCorruption.IsDamage(new SqliteException("not a database", 26)));
        Assert.False(StoreCorruption.IsDamage(new SqliteException("constraint failed", 19)));
        Assert.False(StoreCorruption.IsDamage(new InvalidOperationException("something else")));
        Assert.False(StoreCorruption.IsDamage(null));
    }

    [Fact]
    public void IsDamage_LooksThroughWrappingExceptions()
    {
        var wrapped = new InvalidOperationException("while indexing", new SqliteException("malformed", 11));

        Assert.True(StoreCorruption.IsDamage(wrapped));
    }

    [Fact]
    public void IsIntact_ReportsOkForAFreshStore()
    {
        using var store = Open().Connect();

        Assert.True(StoreCorruption.IsIntact(store, out var report));
        Assert.Equal("ok", report);
    }

    [Fact]
    public void AFileThatIsNotADatabaseIsQuarantinedAndRebuilt()
    {
        var path = Path.Combine(_locator.EnsureCacheDir(), "knowledge.db");
        File.WriteAllText(path, "this is not a SQLite database, not even slightly");

        var store = Open();

        Assert.True(store.Available);
        Assert.Null(store.HealthMessage);
        Assert.Single(QuarantinedStores());
        // The replacement is a working store, not merely a file that exists: the schema is queryable,
        // and it is empty because a file that was never a database had no log to carry across.
        Assert.Equal(0, new FeatureLogStore(store).TotalEntries());
    }

    [Fact]
    public void ADamageMarkerRebuildsTheStoreAndCarriesTheDevelopmentLogAcross()
    {
        var first = Open();
        AppendLogEntry(first, "the reason this code looks the way it does");
        Assert.Equal(1, new FeatureLogStore(first).TotalEntries());
        SqliteConnection.ClearAllPools();

        // What a previous session leaves behind when it hits a damaged page but cannot rename the file
        // itself -- the normal case on Windows, where the rename fails while a connection holds it.
        File.WriteAllText(Path.Combine(_locator.CacheDir, "knowledge.db.corrupt"), "damaged");

        var rebuilt = Open();

        Assert.True(rebuilt.Available);
        Assert.Single(QuarantinedStores());
        Assert.False(File.Exists(Path.Combine(_locator.CacheDir, "knowledge.db.corrupt")));

        // The whole point of salvaging rather than deleting: feature_log is a source of truth, and a
        // rebuild that dropped it would lose why every past change was made.
        var entries = new FeatureLogStore(rebuilt).SearchIntents("reason", 10);
        Assert.Single(entries);
        Assert.Equal("the reason this code looks the way it does", entries[0].Intent);
    }

    [Fact]
    public void ReportFailure_DisablesTheStoreAndMarksItForRebuild()
    {
        var store = Open();

        Assert.False(store.ReportFailure(new InvalidOperationException("an ordinary bug")));
        Assert.True(store.Available);

        Assert.True(store.ReportFailure(new SqliteException("database disk image is malformed", 11)));
        Assert.False(store.Available);
        Assert.NotNull(store.HealthMessage);
        Assert.Contains("knowledge.db", store.HealthMessage, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_locator.CacheDir, "knowledge.db.corrupt")));
    }

    [Fact]
    public void VerifyAndHeal_LeavesAHealthyStoreAlone()
    {
        var store = Open();
        var logId = AppendLogEntry(store, "unchanged by a clean verification");

        Assert.True(store.VerifyAndHeal());

        Assert.Empty(QuarantinedStores());
        Assert.NotEmpty(logId);
        Assert.Equal(1, new FeatureLogStore(store).TotalEntries());
    }

    [Fact]
    public void AStoreOnAnOrdinaryLocalPathKeepsTheWriteAheadLog()
    {
        // The temp directory is a local filesystem on every platform this runs on, so the detection has
        // no reason to fall back -- and a false positive here would silently slow every store down.
        Assert.True(CacheFilesystem.SupportsWriteAheadLog(_root, out var filesystem));
        Assert.NotNull(filesystem);
        Assert.Equal("WAL", Open().JournalMode);
    }

    /// <summary>
    /// The failure this whole path exists for: readable schema, unreadable rows. Page 1 carries the
    /// header and the schema root, so overwriting later pages leaves the store openable and its
    /// migrations satisfied while <c>quick_check</c> rejects it -- which is the shape observed in the
    /// wild, where the file opened fine and only queries touching the damaged table threw.
    /// </summary>
    private static void DamageALaterPage(string path)
    {
        const int PageSize = 4096;
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var garbage = new byte[PageSize];
        Array.Fill(garbage, (byte)0xA5);

        // Everything past the first few pages, so the damage is certain to land on rows rather than on
        // whichever page the schema happens to occupy.
        for (var offset = (long)PageSize * 3; offset + PageSize <= file.Length; offset += PageSize)
        {
            file.Seek(offset, SeekOrigin.Begin);
            file.Write(garbage);
        }
    }

    [Fact]
    public void RealPageDamageIsDetectedAndTheStoreIsRebuilt()
    {
        var store = Open();
        for (var i = 0; i < 300; i++)
            AppendLogEntry(store, $"entry {i} explaining why something changed");

        // WAL keeps recent writes out of the database file, so without a checkpoint the pages damaged
        // below would not be the pages holding these rows.
        using (var connection = store.Connect())
        {
            using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        DamageALaterPage(Path.Combine(_locator.CacheDir, "knowledge.db"));

        var damaged = Open();
        Assert.True(damaged.Available, "migrations still pass: the damage is in data pages, not the schema");

        Assert.True(damaged.VerifyAndHeal());

        Assert.Single(QuarantinedStores());
        Assert.True(damaged.Available);
        Assert.Null(damaged.HealthMessage);
        using var healed = damaged.Connect();
        Assert.True(StoreCorruption.IsIntact(healed, out var report));
        Assert.Equal("ok", report);
    }
}
