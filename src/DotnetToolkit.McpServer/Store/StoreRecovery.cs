using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DotnetToolkit.McpServer.Store;

/// <summary>
/// Moves a damaged knowledge-store file aside and carries the development log across to the store
/// that replaces it.
/// </summary>
/// <remarks>
/// Every table in the store except <c>feature_log</c> and <c>feature_log_symbols</c> is derived from
/// source, git and this process's own telemetry, so one index pass refills it. The development log is
/// not derived from anything — it records why code changed, which nothing else in the repository
/// holds — so deleting a corrupt store outright is lossy in exactly one place, and this copies that
/// place out before the rebuild starts.
/// </remarks>
internal static class StoreRecovery
{
    /// <summary>The tables carried across from a quarantined store, parents before children.</summary>
    private static readonly string[] IrreplaceableTables = ["feature_log", "feature_log_symbols"];

    /// <summary>The files SQLite keeps alongside the database, which belong to it and not to its replacement.</summary>
    private static readonly string[] SidecarSuffixes = ["", "-wal", "-shm"];

    // Small enough that one unreadable page costs few rows, large enough that salvaging a log of a few
    // thousand entries is not thousands of round trips.
    private const int SalvageWindow = 200;

    /// <summary>Renames a damaged store and its sidecars out of the way.</summary>
    /// <param name="databasePath">The store file to move aside.</param>
    /// <param name="now">The timestamp distinguishing this quarantine from an earlier one.</param>
    /// <returns>The path the store was moved to, whose sidecars carry the usual suffixes.</returns>
    /// <remarks>
    /// The <c>-wal</c> and <c>-shm</c> files move with the database. Leaving either behind would let
    /// SQLite replay the damaged store's journal into the fresh one and corrupt it on first open.
    /// </remarks>
    public static string Quarantine(string databasePath, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var stamp = now.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var quarantined = $"{databasePath}.corrupt-{stamp}";

        foreach (var suffix in SidecarSuffixes)
        {
            var from = databasePath + suffix;
            if (File.Exists(from))
            {
                File.Move(from, quarantined + suffix, overwrite: true);
            }
        }

        return quarantined;
    }

    /// <summary>Copies the development log out of a quarantined store into the store replacing it.</summary>
    /// <param name="quarantinedPath">The store <see cref="Quarantine"/> moved aside.</param>
    /// <param name="destination">An open connection to the fresh store, already migrated.</param>
    /// <param name="log">Where per-table outcomes and skipped windows are reported.</param>
    /// <returns>How many development-log rows were recovered across both tables.</returns>
    /// <remarks>
    /// Best-effort by construction: rows are read in <c>rowid</c> windows so an unreadable page costs
    /// only the window containing it, and a window that throws is logged and stepped over rather than
    /// abandoning the rest of the table.
    /// </remarks>
    public static int RecoverDevelopmentLog(string quarantinedPath, SqliteConnection destination, ILogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantinedPath);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(log);

        var source = new SqliteConnectionStringBuilder
        {
            DataSource = quarantinedPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        using var damaged = new SqliteConnection(source);
        damaged.Open();

        // A lost parent row must not abort its children's insert. SQLite's ON CONFLICT clauses do not
        // apply to foreign keys, so INSERT OR IGNORE cannot skip such a row -- the constraint is
        // suspended for the copy and the orphans it admits are deleted immediately afterwards.
        Execute(destination, "PRAGMA foreign_keys=OFF;");
        try
        {
            var recovered = 0;
            foreach (var table in IrreplaceableTables)
            {
                recovered += CopyTable(damaged, destination, table, log);
            }

            Execute(destination,
                "DELETE FROM feature_log_symbols WHERE log_id NOT IN (SELECT log_id FROM feature_log);");
            return recovered;
        }
        finally
        {
            Execute(destination, "PRAGMA foreign_keys=ON;");
        }
    }

    /// <summary>Copies one table across, window by window, skipping the windows that cannot be read.</summary>
    private static int CopyTable(SqliteConnection damaged, SqliteConnection fresh, string table, ILogger log)
    {
        var columns = SharedColumns(damaged, fresh, table);
        if (columns.Count == 0)
        {
            log.LogWarning("Nothing to recover from {Table}: it has no columns in common with the fresh store", table);
            return 0;
        }

        // Both the table and the column names come from the fresh store's own schema, never from data,
        // so this interpolation cannot carry caller input into SQL. Every row value is parameterized.
        var columnList = string.Join(", ", columns);
        var placeholders = string.Join(", ", columns.Select((_, i) => $"$p{i}"));
        var selectSql = $"SELECT {columnList} FROM {table} WHERE rowid >= $low AND rowid < $high ORDER BY rowid;";
        var insertSql = $"INSERT OR IGNORE INTO {table} ({columnList}) VALUES ({placeholders});";

        var highest = HighestRowId(damaged, table, log);
        var copied = 0;
        var skipped = 0;

        using var transaction = fresh.BeginTransaction();
        for (long low = 1; low <= highest; low += SalvageWindow)
        {
            try
            {
                copied += CopyWindow(damaged, fresh, transaction, selectSql, insertSql, columns.Count, low);
            }
            catch (SqliteException ex) when (StoreCorruption.IsDamage(ex))
            {
                // The scan addresses each window by rowid, so the next one is reachable independently
                // of the one that just failed.
                skipped++;
                log.LogWarning(ex, "Skipped {Table} rows {Low}-{High}: that page is unreadable",
                    table, low, low + SalvageWindow - 1);
            }
        }
        transaction.Commit();

        if (skipped > 0)
        {
            log.LogWarning("Recovered {Rows} rows from {Table}; {Skipped} windows were unreadable", copied, table, skipped);
        }
        else
        {
            log.LogInformation("Recovered {Rows} rows from {Table}", copied, table);
        }

        return copied;
    }

    /// <summary>Reads one rowid window from the damaged store and writes it to the fresh one.</summary>
    private static int CopyWindow(
        SqliteConnection damaged,
        SqliteConnection fresh,
        SqliteTransaction transaction,
        string selectSql,
        string insertSql,
        int columnCount,
        long low)
    {
        using var read = damaged.CreateCommand();
        read.CommandText = selectSql;
        read.Parameters.AddWithValue("$low", low);
        read.Parameters.AddWithValue("$high", low + SalvageWindow);

        using var write = fresh.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = insertSql;
        for (var i = 0; i < columnCount; i++)
        {
            write.Parameters.Add(write.CreateParameter());
            write.Parameters[i].ParameterName = $"$p{i}";
        }

        var copied = 0;
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            for (var i = 0; i < columnCount; i++)
            {
                write.Parameters[i].Value = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
            }
            copied += write.ExecuteNonQuery();
        }

        return copied;
    }

    /// <summary>The columns a table has in both stores, so a schema that has since gained one still copies.</summary>
    private static List<string> SharedColumns(SqliteConnection damaged, SqliteConnection fresh, string table)
    {
        var target = ColumnNames(fresh, table);
        if (target.Count == 0)
        {
            return [];
        }

        var available = ColumnNames(damaged, table);
        return [.. target.Where(available.Contains)];
    }

    /// <summary>Reads a table's column names, or none when the table is absent or unreadable.</summary>
    private static HashSet<string> ColumnNames(SqliteConnection connection, string table)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info($table);";
            command.Parameters.AddWithValue("$table", table);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }
        }
        catch (SqliteException)
        {
            // An unreadable schema page means this table cannot be salvaged; the caller reports zero
            // recovered rows rather than failing the whole recovery.
        }

        return names;
    }

    /// <summary>The table's highest rowid, or zero when even that cannot be read.</summary>
    private static long HighestRowId(SqliteConnection connection, string table, ILogger log)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT max(rowid) FROM {table};";
            return command.ExecuteScalar() is long highest ? highest : 0;
        }
        catch (SqliteException ex)
        {
            log.LogWarning(ex, "Could not size {Table} in the quarantined store; skipping it", table);
            return 0;
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
