using Microsoft.Data.Sqlite;

namespace DotnetToolkit.McpServer.Store;

/// <summary>
/// Recognises the SQLite failures that mean the knowledge-store file itself is damaged, rather than
/// the operation over it merely having failed.
/// </summary>
/// <remarks>
/// Only the two result codes that say the bytes on disk cannot be read as a database count here.
/// <c>SQLITE_IOERR</c> is deliberately excluded even though a damaged file can raise it: it also
/// covers a full disk, a revoked lock and a transient read failure, and quarantining on one of those
/// would move a perfectly healthy store aside and force a rebuild that nothing needed.
/// </remarks>
internal static class StoreCorruption
{
    /// <summary><c>SQLITE_CORRUPT</c> — a B-tree page failed to parse.</summary>
    private const int SqliteCorrupt = 11;

    /// <summary><c>SQLITE_NOTADB</c> — the file does not begin with SQLite's header.</summary>
    private const int SqliteNotADb = 26;

    /// <summary>Whether an exception, or anything it wraps, reports a damaged database file.</summary>
    /// <param name="exception">The exception a store operation failed with.</param>
    /// <returns><c>true</c> when the file is damaged; <c>false</c> for every other failure.</returns>
    public static bool IsDamage(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite && sqlite.SqliteErrorCode is SqliteCorrupt or SqliteNotADb)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Runs SQLite's page-level integrity check over an open store.</summary>
    /// <param name="connection">An open connection to the store.</param>
    /// <param name="report">SQLite's own verdict — <c>"ok"</c>, or the first problem it found.</param>
    /// <returns><c>true</c> when the file is intact.</returns>
    /// <remarks>
    /// <c>quick_check</c> rather than <c>integrity_check</c>, and bounded to the first problem: the
    /// caller's only decision is whether to rebuild, so finding a second fault buys nothing and a badly
    /// damaged store makes the unbounded form throw instead of answering. Measured at ~0.03s on a 29 MB
    /// store on a local disk and ~0.7s across a 9p mount, which is why this runs on the background
    /// index pass rather than on the startup path the MCP handshake waits for.
    /// </remarks>
    public static bool IsIntact(SqliteConnection connection, out string report)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check(1);";
            report = command.ExecuteScalar() as string ?? "no result";
        }
        catch (SqliteException ex)
        {
            // Damage deep enough that the checker itself cannot run is still damage, and is the shape
            // an unreadable root page takes.
            report = ex.Message;
            return false;
        }

        return string.Equals(report, "ok", StringComparison.Ordinal);
    }
}
