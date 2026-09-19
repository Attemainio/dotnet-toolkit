using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DotnetToolkit.McpServer.Store;

/// <summary>Moves a damaged knowledge-store file aside so a fresh one can replace it.</summary>
/// <remarks>
/// Every table in the store is derived from source, git and this process's own telemetry, so one
/// index pass refills it; nothing needs carrying across. (The development log that once did is gone
/// with the patch tool that wrote it.)
/// </remarks>
internal static class StoreRecovery
{
    /// <summary>The files SQLite keeps alongside the database, which belong to it and not to its replacement.</summary>
    private static readonly string[] SidecarSuffixes = ["", "-wal", "-shm"];

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
}
