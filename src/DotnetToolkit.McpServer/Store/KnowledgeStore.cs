using DotnetToolkit.McpServer.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DotnetToolkit.McpServer.Store;

/// <summary>
/// Owns the single SQLite knowledge-store file under the target repo's cache dir.
/// </summary>
/// <remarks>
/// Every table but the development log is derived from source, git and this process's own telemetry
/// (spec §17), so a damaged store is quarantined and rebuilt rather than repaired, and the log is
/// carried across into the replacement. Connections are pooled by <see cref="SqliteConnection"/>, so
/// callers open short-lived connections via <see cref="Connect"/> per operation.
/// </remarks>
public sealed class KnowledgeStore : IKnowledgeStore
{
    /// <summary>The journal mode used wherever SQLite's write-ahead log is safe.</summary>
    private const string WriteAheadLog = "WAL";

    /// <summary>The journal mode used on a mount that only emulates shared memory and file locking.</summary>
    private const string RollbackJournal = "TRUNCATE";

    /// <summary>Names the file marking a store a running server found damaged.</summary>
    private const string DamageMarkerSuffix = ".corrupt";

    private readonly ILogger<KnowledgeStore> _log;
    private readonly string _connectionString;

    /// <summary>Pragmas restated on every open, or null when the journal mode persists in the file header.</summary>
    private readonly string? _perConnectionPragmas;

    /// <value>The store file; its <c>-wal</c>/<c>-shm</c> sidecars sit beside it.</value>
    public string DatabasePath { get; }

    /// <inheritdoc/>
    public bool Available { get; private set; }

    /// <value>
    /// What is wrong with the store, in words a caller can act on, or <see langword="null"/> while it
    /// is healthy.
    /// </value>
    public string? HealthMessage { get; private set; }

    /// <value>The SQLite journal mode this store runs in.</value>
    public string JournalMode { get; }

    /// <summary>Opens the store, migrating or rebuilding it as needed.</summary>
    /// <param name="locator">Supplies the cache directory the store file lives in.</param>
    /// <param name="log">Where migrations, quarantines and recoveries are reported.</param>
    public KnowledgeStore(SolutionLocator locator, ILogger<KnowledgeStore> log)
    {
        ArgumentNullException.ThrowIfNull(locator);

        _log = log;
        locator.EnsureCacheDir();
        DatabasePath = Path.Combine(locator.CacheDir, "knowledge.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
        }.ToString();

        var walIsSafe = CacheFilesystem.SupportsWriteAheadLog(DatabasePath, out var filesystem);
        JournalMode = walIsSafe ? WriteAheadLog : RollbackJournal;

        // WAL is recorded in the database header and outlives the connection that set it; a rollback
        // journal is per-connection state and has to be restated on every open.
        _perConnectionPragmas = walIsSafe
            ? null
            : $"PRAGMA journal_mode={RollbackJournal}; PRAGMA synchronous=FULL;";
        if (!walIsSafe)
        {
            _log.LogInformation(
                "Cache is on {Filesystem}, where WAL's shared memory and advisory locking are emulated; "
                + "using a {Mode} rollback journal instead", filesystem, RollbackJournal);
        }

        try
        {
            OpenOrRebuild();
            Available = true;
        }
        catch (Exception ex)
        {
            // A broken store must never take down the MCP server; tools degrade to no-telemetry.
            Available = false;
            HealthMessage = $"knowledge cache at {DatabasePath} could not be opened: {ex.Message}";
            _log.LogError(ex, "Knowledge store unavailable; telemetry and index features disabled");
        }
    }

    /// <summary>Opens a pooled connection. Caller disposes.</summary>
    /// <returns>An open connection carrying this store's journal settings.</returns>
    public SqliteConnection Connect()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        if (_perConnectionPragmas is not null)
        {
            Execute(connection, _perConnectionPragmas);
        }
        return connection;
    }

    /// <summary>Checks the store's pages and rebuilds it when they are damaged.</summary>
    /// <returns><c>true</c> when the store is usable once this returns.</returns>
    /// <remarks>
    /// Deliberately not called from the constructor: the check reads every page, measured at ~0.03s on
    /// a local disk but ~0.7s across a 9p mount, and the MCP handshake waits on the constructor. The
    /// background index pass is the earliest point at which that cost is free, and it runs before any
    /// tool has been answered from the store.
    /// </remarks>
    public bool VerifyAndHeal()
    {
        if (!Available)
        {
            return false;
        }

        try
        {
            using (var connection = Connect())
            {
                if (StoreCorruption.IsIntact(connection, out var report))
                {
                    return true;
                }

                _log.LogError("Knowledge cache at {Path} failed its integrity check: {Report}", DatabasePath, report);
            }

            var quarantined = Quarantine("its pages failed SQLite's integrity check");
            Initialize();
            RecoverDevelopmentLog(quarantined);
            HealthMessage = null;
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            MarkDamaged(ex, "could not be rebuilt");
            return false;
        }
    }

    /// <summary>Records that a store operation failed, and says whether the file itself was the cause.</summary>
    /// <param name="exception">The exception the operation failed with.</param>
    /// <returns><c>true</c> when the failure was damage to the store rather than an ordinary error.</returns>
    /// <remarks>
    /// A damaged store keeps answering from whatever pages still read, which is worse than answering
    /// nothing: the caller cannot tell a frozen snapshot from a current one, and this plugin's own
    /// skills push it toward re-fetching, which a damaged page will never satisfy. Marking the store
    /// unavailable ends that, and the marker file makes the next start rebuild it.
    /// </remarks>
    public bool ReportFailure(Exception exception)
    {
        if (!StoreCorruption.IsDamage(exception))
        {
            return false;
        }

        MarkDamaged(exception, "is damaged");
        return true;
    }

    /// <summary>Empties the raw telemetry tables, leaving every other table untouched.</summary>
    /// <remarks>
    /// <c>get_retrieval_metrics</c> reports the calls of the process it runs in and nothing else, so the
    /// tables it reads start empty on every start. Startup is the load-bearing end of that: an MCP server
    /// is normally killed rather than stopped, and a killed process never reaches the shutdown callback in
    /// <c>Program</c>, so rows surviving one would be exactly the stale history this removes. The
    /// development log (<c>feature_log</c>) is deliberately outside the purge - it records why code
    /// changed, which outlives the process that recorded it, and so is the symbol index, which caches the
    /// code rather than recording this session's use of it.
    /// </remarks>
    public void PurgeTelemetry()
    {
        if (!Available)
            return;

        try
        {
            using var connection = Connect();
            PurgeTelemetry(connection);
        }
        catch (Exception ex)
        {
            // Same posture as the constructor: telemetry housekeeping must never take the server down.
            _log.LogWarning(ex, "Could not clear telemetry; the next start will clear it instead");
        }
    }

    /// <summary>The marker a damaged store leaves behind for the next server start to find.</summary>
    private string MarkerPath => DatabasePath + DamageMarkerSuffix;

    /// <summary>Migrates the store, rebuilding it first when it is known or found to be damaged.</summary>
    private void OpenOrRebuild()
    {
        // A marker means a previous session hit a damaged page and could not rename the file itself,
        // which is the normal case on Windows: the rename fails while any connection still holds it.
        var quarantined = File.Exists(MarkerPath)
            ? Quarantine("a previous session reported it damaged")
            : null;

        try
        {
            Initialize();
        }
        catch (Exception ex) when (StoreCorruption.IsDamage(ex) && quarantined is null)
        {
            // Damage bad enough to stop the migrations: the file cannot be opened as a database at all.
            quarantined = Quarantine("it could not be opened as a database");
            Initialize();
        }

        if (quarantined is not null)
        {
            RecoverDevelopmentLog(quarantined);
        }
    }

    /// <summary>Moves the damaged store aside and clears its marker, leaving the next open to create a fresh one.</summary>
    /// <param name="reason">Why it is being quarantined, for the log line.</param>
    /// <returns>The path the damaged store now sits at.</returns>
    private string Quarantine(string reason)
    {
        // A pooled handle still holding the file would fail the rename on Windows, and everywhere else
        // would let the damaged store's journal be replayed into its replacement.
        SqliteConnection.ClearAllPools();

        var quarantined = StoreRecovery.Quarantine(DatabasePath, DateTimeOffset.UtcNow);
        File.Delete(MarkerPath);
        _log.LogWarning(
            "Knowledge cache at {Path} moved to {Quarantined} because {Reason}; rebuilding it from source",
            DatabasePath, quarantined, reason);
        return quarantined;
    }

    /// <summary>Carries the development log out of a quarantined store into the fresh one.</summary>
    /// <param name="quarantined">The store <see cref="Quarantine"/> moved aside.</param>
    private void RecoverDevelopmentLog(string quarantined)
    {
        try
        {
            using var connection = Connect();
            var rows = StoreRecovery.RecoverDevelopmentLog(quarantined, connection, _log);
            _log.LogInformation(
                "Carried {Rows} development-log rows across from {Quarantined}", rows, quarantined);
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            // The rebuild has already succeeded. Losing the salvage is worse than a clean store but far
            // better than refusing to open one, and the quarantined file is still there to read by hand.
            _log.LogWarning(ex, "Could not recover the development log from {Quarantined}", quarantined);
        }
    }

    /// <summary>Disables the store for this session and marks it for rebuild on the next start.</summary>
    /// <param name="exception">The failure that revealed the damage.</param>
    /// <param name="what">How the store is broken, phrased to follow its path in a sentence.</param>
    private void MarkDamaged(Exception exception, string what)
    {
        Available = false;
        HealthMessage =
            $"knowledge cache at {DatabasePath} {what}; symbol search and the development log are "
            + "disabled for this session. It is rebuilt automatically on the next server start, or "
            + "delete the file to rebuild it now.";
        _log.LogError(exception, "Knowledge cache at {Path} {What}; disabling it for this session",
            DatabasePath, what);

        try
        {
            File.WriteAllText(MarkerPath, HealthMessage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the marker the next start opens the damaged store again and reports the same
            // thing; nothing else depends on it existing.
            _log.LogWarning(ex, "Could not write the damage marker at {Path}", MarkerPath);
        }
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        ApplyJournalMode(connection);
        Execute(connection, "PRAGMA foreign_keys=ON;");
        Execute(connection,
            "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL);");

        var applied = AppliedVersions(connection);
        foreach (var migration in Schema.Migrations.Where(m => !applied.Contains(m.Version)))
        {
            using var tx = connection.BeginTransaction();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = migration.Sql;
                cmd.ExecuteNonQuery();
            }
            using (var record = connection.CreateCommand())
            {
                record.Transaction = tx;
                record.CommandText =
                    "INSERT INTO schema_migrations (version, name, applied_at) VALUES ($v, $n, $t);";
                record.Parameters.AddWithValue("$v", migration.Version);
                record.Parameters.AddWithValue("$n", migration.Name);
                record.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                record.ExecuteNonQuery();
            }
            tx.Commit();
            _log.LogInformation("Applied knowledge-store migration {Version} ({Name})", migration.Version, migration.Name);
        }
        PurgeTelemetry(connection);
    }

    /// <summary>Puts the store into <see cref="JournalMode"/>, reporting it if SQLite declined.</summary>
    /// <param name="connection">An open connection to the store.</param>
    /// <remarks>
    /// <para>
    /// <strong>SQLite answers a refused change with the mode still in force rather than failing.</strong>
    /// Converting an existing WAL store to a rollback journal needs exclusive access, so a store another
    /// connection still holds silently stays in WAL -- which is the mode this whole path exists to get
    /// off a 9p or network mount. Left unchecked it would keep running the unsafe mode while the startup
    /// line claimed otherwise, so the one thing worth doing here is reading the answer back.
    /// </para>
    /// <para>
    /// <c>synchronous=FULL</c> accompanies the rollback journal deliberately: the mounts that rule WAL
    /// out are the same ones whose write ordering cannot be relied on, and the store's writes are a
    /// handful of batched transactions per index pass rather than a per-request cost.
    /// </para>
    /// </remarks>
    private void ApplyJournalMode(SqliteConnection connection)
    {
        using (var journal = connection.CreateCommand())
        {
            journal.CommandText = $"PRAGMA journal_mode={JournalMode};";
            if (journal.ExecuteScalar() is string actual
                && !string.Equals(actual, JournalMode, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning(
                    "Asked SQLite for journal_mode={Wanted} at {Path}, but it stayed {Actual}; another "
                    + "connection is holding the store open", JournalMode, DatabasePath, actual);
            }
        }

        if (JournalMode != WriteAheadLog)
        {
            Execute(connection, "PRAGMA synchronous=FULL;");
        }
    }

    // These two tables are the whole of raw telemetry: migration 14 dropped surfaced_symbols and
    // session_events, and naming a table that no longer exists throws here - which the constructor
    // catches as "store unavailable", silently disabling telemetry rather than reporting the typo.
    private static void PurgeTelemetry(SqliteConnection connection) => Execute(connection, """
        DELETE FROM retrieval_events;
        DELETE FROM patch_events;
        DELETE FROM tool_call_events;
        """);

    private static HashSet<int> AppliedVersions(SqliteConnection connection)
    {
        var versions = new HashSet<int>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_migrations;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            versions.Add(reader.GetInt32(0));
        return versions;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
