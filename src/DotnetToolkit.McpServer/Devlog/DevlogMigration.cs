using DotnetToolkit.McpServer.Store;
using Microsoft.Extensions.Logging;

namespace DotnetToolkit.McpServer.Devlog;

/// <summary>
/// One-time import of the legacy markdown devlog into the SQLite <c>feature_log</c> (v2 hard cut).
/// Runs only when the log is empty, so it never duplicates entries and never fights the patch/commit
/// streams that own the log going forward. The import is lossy by nature — old entries carry class
/// names rather than symbolIds and no version tokens — so imported symbol rows are tagged "legacy".
/// </summary>
public static class DevlogMigration
{
    public static int Run(DevlogStore devlog, FeatureLogStore log, ILogger logger)
    {
        if (!log.Available || log.EntryCount() > 0)
            return 0;

        var entries = devlog.Search(null, null, null, null, null, null, null, int.MaxValue);
        if (entries.Count == 0)
            return 0;

        foreach (var (entry, _) in entries)
        {
            var tags = entry.Tags.Concat(entry.Domain is null ? [] : [entry.Domain]).ToList();
            var symbols = entry.Classes
                .Select(c => new FeatureLogStore.LogSymbol(c, null, ["legacy"], "migrated from devlog", null, null, null))
                .ToList();
            log.Append(new FeatureLogStore.LogEntry(
                "tsk_devlog_import", null, null, entry.Title, tags, null, symbols));
        }

        logger.LogInformation("Imported {Count} devlog entries into feature_log", entries.Count);
        return entries.Count;
    }
}
