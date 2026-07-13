using System.Text.Json;
using System.Text.RegularExpressions;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Extensions.Logging;

namespace DotnetToolkit.McpServer.Indexing;

public sealed record SymbolHit(string Kind, string Name, string FqName, string File, int Line, string? Doc, string? Signature);

/// <summary>
/// Syntax-tier index of every .cs file under the target root: file tree, type outlines,
/// doc summaries. Built without MSBuild so it is available seconds after startup.
/// Invalidation is mtime-polling based because inotify does not work on /mnt/* (WSL DrvFs).
/// </summary>
public sealed class ProjectIndex
{
    private static readonly TimeSpan QuickSweepDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FullSweepInterval = TimeSpan.FromSeconds(30);

    private readonly SolutionLocator _locator;
    private readonly ILogger<ProjectIndex> _log;
    private readonly SemaphoreSlim _sweepGate = new(1, 1);
    private readonly Regex[] _excludes;

    private volatile Dictionary<string, FileEntry> _files = new(StringComparer.Ordinal);
    private Task _initTask = Task.CompletedTask;
    private DateTime _lastQuickSweepUtc = DateTime.MinValue;
    private DateTime _lastFullSweepUtc = DateTime.MinValue;

    public string State { get; private set; } = "not-started";
    public int FileCount => _files.Count;
    public int TypeCount => _files.Values.Sum(f => CountTypes(f.Types));

    /// <summary>Raised after a sweep: (changed rel paths, any files added/removed).</summary>
    public event Action<IReadOnlyList<string>, bool>? FilesChanged;

    public ProjectIndex(SolutionLocator locator, ILogger<ProjectIndex> log)
    {
        _locator = locator;
        _log = log;
        _excludes = locator.Config.ExcludeGlobs.Select(GlobToRegex).ToArray();
    }

    public void StartInitialization() => _initTask = Task.Run(InitializeAsync);

    private async Task InitializeAsync()
    {
        try
        {
            State = "building";
            LoadCache();
            await SweepAsync(full: true);
            State = "ready";
            _log.LogInformation("Index ready: {Files} files, {Types} types", FileCount, TypeCount);
        }
        catch (Exception ex)
        {
            State = $"failed: {ex.Message}";
            _log.LogError(ex, "Index initialization failed");
        }
    }

    /// <summary>Await initial build, then run a debounced staleness sweep. Call before every query.</summary>
    public async Task EnsureFreshAsync()
    {
        await _initTask;
        var now = DateTime.UtcNow;
        if (now - _lastFullSweepUtc > FullSweepInterval)
            await SweepAsync(full: true);
        else if (now - _lastQuickSweepUtc > QuickSweepDebounce)
            await SweepAsync(full: false);
    }

    /// <summary>Forces an immediate full re-scan regardless of debounce timers.</summary>
    public async Task ForceRescanAsync()
    {
        await _initTask;
        await SweepAsync(full: true);
    }

    private async Task SweepAsync(bool full)
    {
        await _sweepGate.WaitAsync();
        try
        {
            var previous = _files;
            var changed = new List<string>();
            var next = new Dictionary<string, FileEntry>(previous, StringComparer.Ordinal);
            var structural = false;

            IEnumerable<string> candidates = full
                ? EnumerateCsFiles()
                : previous.Keys.Select(_locator.AbsPath).Where(File.Exists);

            var seen = full ? new HashSet<string>(StringComparer.Ordinal) : null;
            var toParse = new List<(string Rel, string Abs, long Mtime, long Len)>();

            foreach (var abs in candidates)
            {
                var rel = _locator.RelPath(abs);
                seen?.Add(rel);
                var info = new FileInfo(abs);
                if (!info.Exists)
                    continue;
                if (previous.TryGetValue(rel, out var existing)
                    && existing.MtimeTicks == info.LastWriteTimeUtc.Ticks
                    && existing.Length == info.Length)
                    continue;
                toParse.Add((rel, abs, info.LastWriteTimeUtc.Ticks, info.Length));
            }

            if (full)
            {
                foreach (var gone in previous.Keys.Where(k => !seen!.Contains(k)).ToList())
                {
                    next.Remove(gone);
                    structural = true;
                }
            }

            Parallel.ForEach(toParse, item =>
            {
                try
                {
                    var entry = OutlineBuilder.Build(File.ReadAllText(item.Abs), item.Mtime, item.Len);
                    lock (changed)
                    {
                        if (!next.ContainsKey(item.Rel))
                            structural = true;
                        next[item.Rel] = entry;
                        changed.Add(item.Rel);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to parse {File}", item.Rel);
                }
            });

            _lastQuickSweepUtc = DateTime.UtcNow;
            if (full)
                _lastFullSweepUtc = _lastQuickSweepUtc;

            if (changed.Count > 0 || structural)
            {
                _files = next;
                SaveCache(next);
                FilesChanged?.Invoke(changed, structural);
            }
        }
        finally
        {
            _sweepGate.Release();
        }
    }

    private IEnumerable<string> EnumerateCsFiles()
    {
        var stack = new Stack<string>();
        stack.Push(_locator.Root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, subdirs;
            try
            {
                files = Directory.GetFiles(dir, "*.cs");
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var f in files)
            {
                if (!IsExcluded(_locator.RelPath(f)))
                    yield return f;
            }
            foreach (var d in subdirs)
            {
                var name = Path.GetFileName(d);
                if (!SolutionLocator.ShouldSkipDir(name) && !IsExcluded(_locator.RelPath(d) + "/"))
                    stack.Push(d);
            }
        }
    }

    private bool IsExcluded(string relPath) => _excludes.Any(r => r.IsMatch(relPath));

    private static Regex GlobToRegex(string glob)
    {
        var pattern = Regex.Escape(glob.Replace('\\', '/'))
            .Replace(@"\*\*/", "(.*/)?")
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", ".");
        return new Regex($"^{pattern}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private string CachePath => Path.Combine(_locator.CacheDir, "index.json");

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
                return;
            var doc = JsonSerializer.Deserialize<IndexDocument>(File.ReadAllText(CachePath));
            if (doc is { Version: IndexDocument.CurrentVersion } && doc.Root == _locator.Root)
                _files = doc.Files;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ignoring unreadable index cache");
        }
    }

    private void SaveCache(Dictionary<string, FileEntry> files)
    {
        try
        {
            _locator.EnsureCacheDir();
            var doc = new IndexDocument { Root = _locator.Root, Files = files };
            File.WriteAllText(CachePath, JsonSerializer.Serialize(doc));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to save index cache");
        }
    }

    private static int CountTypes(List<TypeEntry> types) =>
        types.Sum(t => 1 + CountTypes(t.Nested));

    // ---- queries -------------------------------------------------------------

    public IReadOnlyDictionary<string, FileEntry> Snapshot() => _files;

    public FileEntry? GetFile(string relPath) =>
        _files.TryGetValue(relPath.Replace('\\', '/'), out var entry) ? entry : null;

    public (List<SymbolHit> Hits, int Total) FindSymbol(string query, string? kind, int limit)
    {
        var kindCode = MapKind(kind);
        var hits = new List<(SymbolHit Hit, int Rank)>();

        foreach (var (file, entry) in _files)
        {
            foreach (var type in Flatten(entry.Types))
            {
                if (kindCode is null || type.Kind == kindCode)
                {
                    var rank = MatchRank(type.Name, type.FqName, query);
                    if (rank >= 0)
                        hits.Add((new SymbolHit(type.Kind, type.Name, type.FqName, file, type.Line, type.Doc, null), rank));
                }
                if (kindCode is null or "M" or "K" or "P" or "F" or "V")
                {
                    foreach (var m in type.Members)
                    {
                        if (kindCode is not null && m.Kind != kindCode)
                            continue;
                        var rank = MatchRank(m.Name, $"{type.FqName}.{m.Name}", query);
                        if (rank >= 0)
                            hits.Add((new SymbolHit(m.Kind, m.Name, $"{type.FqName}.{m.Name}", file, m.Line, m.Doc, m.Signature), rank + 10));
                    }
                }
            }
        }

        var ordered = hits.OrderBy(h => h.Rank).ThenBy(h => h.Hit.FqName, StringComparer.Ordinal).Select(h => h.Hit).ToList();
        return (ordered.Take(limit).ToList(), ordered.Count);
    }

    private static int MatchRank(string name, string fqName, string query)
    {
        var bare = StripGenerics(name);
        if (bare.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (bare.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (bare.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 2;
        if (fqName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 3;
        return -1;
    }

    private static string StripGenerics(string name)
    {
        var idx = name.IndexOf('<');
        return idx < 0 ? name : name[..idx];
    }

    public static IEnumerable<TypeEntry> Flatten(IEnumerable<TypeEntry> types)
    {
        foreach (var t in types)
        {
            yield return t;
            foreach (var n in Flatten(t.Nested))
                yield return n;
        }
    }

    private static string? MapKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "class" or "c" => "C",
        "interface" or "i" => "I",
        "struct" or "s" => "S",
        "record" or "r" => "R",
        "enum" or "e" => "E",
        "delegate" or "d" => "D",
        "method" or "m" => "M",
        "constructor" or "ctor" or "k" => "K",
        "property" or "p" => "P",
        "field" or "f" => "F",
        "event" or "v" => "V",
        _ => null,
    };
}
