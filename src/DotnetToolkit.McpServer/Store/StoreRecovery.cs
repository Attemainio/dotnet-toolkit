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

    /// <summary>How many quarantined stores are kept; older ones are deleted when a new one is made.</summary>
    /// <remarks>
    /// Two: the one just moved aside and the one before it, enough to compare when a store is damaged
    /// twice in a row. A quarantined file is a full copy of the cache, and on a repository that trips the
    /// corruption path on every start — a 9p mount, say — an uncapped pile of them fills the cache
    /// directory with hundreds of megabytes nobody reads. Measured on FluidScript (2026-09-19): five
    /// copies before anyone noticed.
    /// </remarks>
    internal const int KeptQuarantines = 2;

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

        PruneOlderQuarantines(databasePath, quarantined);
        return quarantined;
    }

    /// <summary>Deletes every quarantined store older than the newest <see cref="KeptQuarantines"/>.</summary>
    /// <param name="databasePath">The live store, whose quarantines sit beside it.</param>
    /// <param name="justMade">The quarantine made in this call, always kept.</param>
    /// <remarks>
    /// The stamp is UTC and lexically sortable, so ordering by name is ordering by time. A file that
    /// will not delete — held open by another process, say — is left where it is; pruning is
    /// housekeeping, and a failure to prune must never fail the quarantine that needed it.
    /// </remarks>
    private static void PruneOlderQuarantines(string databasePath, string justMade)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        var prefix = Path.GetFileName(databasePath) + ".corrupt-";
        var stores = Directory.GetFiles(directory, prefix + "*")
            .Where(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal))
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .ToList();
        if (!stores.Contains(justMade))
            stores.Insert(0, justMade);

        foreach (var stale in stores.Skip(KeptQuarantines))
        {
            foreach (var suffix in SidecarSuffixes)
            {
                try
                {
                    File.Delete(stale + suffix);
                }
                catch (IOException)
                {
                    // Held open elsewhere; the next quarantine will try again.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
