using DotnetToolkit.McpServer.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DotnetToolkit.McpServer.Store;

/// <summary>
/// Owns the single SQLite knowledge-store file (WAL mode) under the target repo's cache dir.
/// The store is always rebuildable from source + git + raw telemetry (spec §17); deleting the
/// file simply forces a rebuild. Connections are pooled by <see cref="SqliteConnection"/>, so
/// callers open short-lived connections via <see cref="Connect"/> per operation.
/// </summary>
public sealed class KnowledgeStore
{
    private readonly ILogger<KnowledgeStore> _log;
    private readonly string _connectionString;

    public string DatabasePath { get; }
    public bool Available { get; private set; }

    public KnowledgeStore(SolutionLocator locator, ILogger<KnowledgeStore> log)
    {
        _log = log;
        locator.EnsureCacheDir();
        DatabasePath = Path.Combine(locator.CacheDir, "knowledge.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();

        try
        {
            Initialize();
            Available = true;
        }
        catch (Exception ex)
        {
            // A broken store must never take down the MCP server; tools degrade to no-telemetry.
            _log.LogError(ex, "Knowledge store unavailable; telemetry and index features disabled");
            Available = false;
        }
    }

    /// <summary>Opens a pooled connection. Caller disposes.</summary>
    public SqliteConnection Connect()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        Execute(connection, "PRAGMA journal_mode=WAL;");
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
    }

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
