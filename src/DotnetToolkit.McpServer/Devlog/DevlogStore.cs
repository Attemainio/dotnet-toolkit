using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Extensions.Logging;

namespace DotnetToolkit.McpServer.Devlog;

/// <summary>
/// Development log keeper. Weekly markdown files under the target repo (committed,
/// human-readable source of truth) plus a rebuildable JSON search index in the cache dir.
/// The agent interacts only through the devlog_* tools, never by reading the files.
/// </summary>
public sealed class DevlogStore
{
    private readonly SolutionLocator _locator;
    private readonly ILogger<DevlogStore> _log;
    private readonly object _gate = new();
    private DevlogIndexDoc? _index;

    public DevlogStore(SolutionLocator locator, ILogger<DevlogStore> log)
    {
        _locator = locator;
        _log = log;
    }

    private string DevlogDirAbs => Path.Combine(_locator.Root, _locator.Config.DevlogDir);
    private string IndexPath => Path.Combine(_locator.CacheDir, "devlog-index.json");

    public (string Id, string File) Add(
        string title,
        string what,
        string why,
        string? observations,
        string? fix,
        string status,
        string[] classes,
        string? domain,
        string[] tags)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            var fileName = $"{ISOWeek.GetYear(now.Date):0000}-W{ISOWeek.GetWeekOfYear(now.Date):00}.md";
            var abs = Path.Combine(DevlogDirAbs, fileName);
            Directory.CreateDirectory(DevlogDirAbs);

            var id = $"{now:yyyyMMdd-HHmm}-{RandomSuffix()}";
            var meta = new DevlogMeta
            {
                Id = id,
                Ts = now,
                Status = status,
                Classes = classes,
                Domain = domain,
                Tags = tags,
            };

            var sb = new StringBuilder();
            if (!File.Exists(abs))
                sb.Append("# Devlog ").Append(Path.GetFileNameWithoutExtension(fileName)).Append("\n\n");
            sb.Append("## ").Append(now.ToString("yyyy-MM-dd")).Append(" — ").Append(title.Trim()).Append('\n');
            sb.Append("<!-- devlog ").Append(JsonSerializer.Serialize(meta)).Append(" -->\n\n");
            sb.Append("**WHAT:** ").Append(what.Trim()).Append("\n\n");
            sb.Append("**WHY:** ").Append(why.Trim()).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(observations))
                sb.Append("**OBSERVATIONS:** ").Append(observations.Trim()).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(fix))
                sb.Append("**FIX:** ").Append(fix.Trim()).Append("\n\n");

            File.AppendAllText(abs, sb.ToString());
            EnsureFreshLocked();
            return (id, _locator.RelPath(abs));
        }
    }

    public List<(DevlogIndexEntry Entry, double Score)> Search(
        string? query,
        string? affectedClass,
        string? domain,
        string? tag,
        string? status,
        DateOnly? from,
        DateOnly? to,
        int limit)
    {
        lock (_gate)
        {
            EnsureFreshLocked();
            IEnumerable<DevlogIndexEntry> entries = _index!.Entries;

            if (affectedClass is not null)
                entries = entries.Where(e => e.Classes.Any(c => c.Contains(affectedClass, StringComparison.OrdinalIgnoreCase)));
            if (domain is not null)
                entries = entries.Where(e => e.Domain?.Equals(domain, StringComparison.OrdinalIgnoreCase) == true);
            if (tag is not null)
                entries = entries.Where(e => e.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)));
            if (status is not null)
                entries = entries.Where(e => e.Status.Equals(status, StringComparison.OrdinalIgnoreCase));
            if (from is { } f)
                entries = entries.Where(e => DateOnly.FromDateTime(e.Ts.LocalDateTime) >= f);
            if (to is { } t2)
                entries = entries.Where(e => DateOnly.FromDateTime(e.Ts.LocalDateTime) <= t2);

            if (string.IsNullOrWhiteSpace(query))
                return entries.OrderByDescending(e => e.Ts).Take(limit).Select(e => (e, 0.0)).ToList();

            var tokens = DevlogSearch.Tokenize(query).ToList();
            return entries
                .Select(e => (Entry: e, Score: DevlogSearch.Score(e, tokens)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Entry.Ts)
                .Take(limit)
                .ToList();
        }
    }

    public int TotalMatching(
        string? query, string? affectedClass, string? domain, string? tag, string? status,
        DateOnly? from, DateOnly? to)
        => Search(query, affectedClass, domain, tag, status, from, to, int.MaxValue).Count;

    /// <summary>Full markdown of a single entry (metadata comment stripped).</summary>
    public string? Get(string id)
    {
        lock (_gate)
        {
            EnsureFreshLocked();
            var hit = _index!.Entries.FirstOrDefault(e => e.Id == id);
            if (hit is null)
                return null;
            var abs = _locator.AbsPath(hit.File);
            if (!File.Exists(abs))
                return null;
            var entry = DevlogParser.ParseFile(abs, hit.File).FirstOrDefault(e => e.Id == id);
            return entry is null ? null : DevlogParser.StripMeta(entry.Markdown).Trim();
        }
    }

    public int EntryCount
    {
        get { lock (_gate) { EnsureFreshLocked(); return _index!.Entries.Count; } }
    }

    private void EnsureFreshLocked()
    {
        _index ??= LoadIndex();
        var current = new Dictionary<string, long>(StringComparer.Ordinal);
        if (Directory.Exists(DevlogDirAbs))
        {
            foreach (var abs in Directory.EnumerateFiles(DevlogDirAbs, "*.md"))
                current[_locator.RelPath(abs)] = File.GetLastWriteTimeUtc(abs).Ticks;
        }

        var stale = current.Where(kv => _index.Files.GetValueOrDefault(kv.Key, -1) != kv.Value)
            .Select(kv => kv.Key)
            .ToList();
        var removed = _index.Files.Keys.Where(k => !current.ContainsKey(k)).ToList();
        if (stale.Count == 0 && removed.Count == 0)
            return;

        foreach (var rel in stale.Concat(removed))
            _index.Entries.RemoveAll(e => e.File == rel);

        foreach (var rel in stale)
        {
            try
            {
                foreach (var entry in DevlogParser.ParseFile(_locator.AbsPath(rel), rel))
                {
                    _index.Entries.Add(new DevlogIndexEntry
                    {
                        Id = entry.Id,
                        Ts = entry.Ts,
                        File = entry.File,
                        Title = entry.Title,
                        Status = entry.Status,
                        Classes = entry.Classes,
                        Domain = entry.Domain,
                        Tags = entry.Tags,
                        Terms = DevlogSearch.BuildTerms(entry.Title, DevlogParser.StripMeta(entry.Markdown)),
                    });
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to parse devlog file {File}", rel);
            }
        }

        _index.Files = current;
        SaveIndex();
    }

    private DevlogIndexDoc LoadIndex()
    {
        try
        {
            if (File.Exists(IndexPath))
            {
                var doc = JsonSerializer.Deserialize<DevlogIndexDoc>(File.ReadAllText(IndexPath));
                if (doc is { Version: DevlogIndexDoc.CurrentVersion })
                    return doc;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ignoring unreadable devlog index");
        }
        return new DevlogIndexDoc();
    }

    private void SaveIndex()
    {
        try
        {
            _locator.EnsureCacheDir();
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(_index));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to save devlog index");
        }
    }

    private static string RandomSuffix()
    {
        Span<byte> bytes = stackalloc byte[2];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }
}
