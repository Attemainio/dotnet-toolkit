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

    /// <summary>
    /// Project-file mtimes as of the last full sweep, or null before the first one has run. Null is
    /// meaningfully distinct from empty here — it means "no baseline yet", which is what suppresses a
    /// redundant reload on startup.
    /// </summary>
    private Dictionary<string, long>? _projectFiles;

    public string State { get; private set; } = "not-started";
    public int FileCount => _files.Count;
    public int TypeCount => _files.Values.Sum(f => CountTypes(f.Types));

    /// <summary>Raised after a sweep: (changed rel paths, any files added/removed).</summary>
    public event Action<IReadOnlyList<string>, bool>? FilesChanged;

    /// <summary>
    /// Raised when a .csproj, .props, .targets, or solution file changed. Carries no payload: the only
    /// sound response is a full workspace reload, so which file moved does not change what happens.
    /// </summary>
    public event Action? ProjectFilesChanged;

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

            // Only on the full sweep, and read before the .cs notifications go out so a project-file
            // reload is not raced by the per-document patch it would discard anyway.
            var projectFilesMoved = full && SweepProjectFiles();

            if (changed.Count > 0 || structural)
            {
                _files = next;
                SaveCache(next);
                FilesChanged?.Invoke(changed, structural);
            }

            if (projectFilesMoved)
                ProjectFilesChanged?.Invoke();
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

    /// <summary>
    /// Files whose content feeds the design-time build: each project, the MSBuild files that flow into
    /// it by convention (Directory.Build.props and friends), and the solution. None of these are parsed
    /// into the syntax index — they are not this tier's subject — but they are the inputs whose change
    /// makes the *semantic* tier wrong, and this class already owns the only mtime poll in the server.
    ///
    /// <see cref="SolutionLocator.ShouldSkipDir"/> excluding obj/ is load-bearing rather than incidental
    /// here: restore writes .nuget.g.props and .nuget.g.targets into obj/ on every run, so descending
    /// into it would make each reload's own restore trip the next reload, indefinitely.
    /// </summary>
    private IEnumerable<string> EnumerateProjectFiles()
    {
        var stack = new Stack<string>();
        stack.Push(_locator.Root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, subdirs;
            try
            {
                files = Directory.GetFiles(dir);
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var f in files)
            {
                if (IsProjectFile(f))
                    yield return f;
            }
            foreach (var d in subdirs)
            {
                if (!SolutionLocator.ShouldSkipDir(Path.GetFileName(d)))
                    stack.Push(d);
            }
        }
    }

    private static bool IsProjectFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".csproj" or ".props" or ".targets" or ".sln" or ".slnx" => true,
            _ => false,
        };

    /// <summary>
    /// Re-stats the project files and reports whether any moved since the last full sweep.
    ///
    /// Deliberately not run on the quick sweep: a reload costs a full <c>dotnet restore</c>, which is
    /// slow on /mnt/*, and project files change a few times a day rather than a few times a minute — the
    /// full-sweep cadence is the right granularity. The first call returns false however much it finds,
    /// because it is establishing the baseline: startup already loads the workspace, and reporting change
    /// there would make every server start pay for an immediate redundant reload.
    /// </summary>
    private bool SweepProjectFiles()
    {
        var next = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var abs in EnumerateProjectFiles())
        {
            var info = new FileInfo(abs);
            if (info.Exists)
                next[_locator.RelPath(abs)] = info.LastWriteTimeUtc.Ticks;
        }

        var previous = _projectFiles;
        _projectFiles = next;
        if (previous is null || previous.Count != next.Count)
            return previous is not null;

        foreach (var (rel, mtime) in next)
        {
            if (!previous.TryGetValue(rel, out var was) || was != mtime)
                return true;
        }
        return false;
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

    /// <summary>Where a declaration sits: the file it is in and the line it starts on.</summary>
    public sealed record Site(string File, int Line);

    /// <summary>Where a declaration sits, its extracted XML doc &lt;summary&gt; text if any, and the namespace its declaring type belongs to.</summary>
    public sealed record DocSite(string File, int Line, string? Doc, string Namespace);

    /// <summary>
    /// Resolves fully-qualified names — without parameter lists — to their declaration site, in one pass
    /// over the index.
    ///
    /// Read from the syntax index rather than stored alongside the symbol row on purpose. A line number
    /// stored next to a symbol would be invalidated by that symbol's own hashes, and editing *above* a
    /// declaration moves its line without changing a single one of them: the row would not be rewritten
    /// and the stored line would rot silently. The index is mtime-swept per file, so it moves whenever
    /// the file does, which is exactly what a line number depends on.
    ///
    /// A name that resolves to more than one distinct site — overloads, or a partial declared twice — is
    /// omitted rather than guessed at. The names here carry no parameter list, so the two cannot be told
    /// apart, and pointing at the wrong overload is worse than saying nothing: absent already means
    /// "look it up".
    /// </summary>
    public IReadOnlyDictionary<string, Site> Locate(IReadOnlySet<string> fqNamesWithoutParameters)
        => LocateWithDocs(fqNamesWithoutParameters).ToDictionary(
            kv => kv.Key, kv => new Site(kv.Value.File, kv.Value.Line), StringComparer.Ordinal);

    /// <summary>
    /// Same resolution as <see cref="Locate"/>, but each site also carries its declaration's extracted
    /// XML doc &lt;summary&gt; text (null when absent) — the data <c>search_index</c>'s <c>summary</c>
    /// argument surfaces, computed here rather than re-parsed, since <see cref="Indexing.TypeEntry.Doc"/>/
    /// <see cref="Indexing.MemberEntry.Doc"/> already hold it from the syntax pass.
    /// </summary>
    public IReadOnlyDictionary<string, DocSite> LocateWithDocs(IReadOnlySet<string> fqNamesWithoutParameters)
    {
        var found = new Dictionary<string, DocSite>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        void Offer(string fqName, DocSite site)
        {
            if (!fqNamesWithoutParameters.Contains(fqName) || ambiguous.Contains(fqName))
                return;
            if (found.TryGetValue(fqName, out var existing))
            {
                if (existing != site)
                {
                    found.Remove(fqName);
                    ambiguous.Add(fqName);
                }
                return;
            }
            found[fqName] = site;
        }

        foreach (var (file, entry) in _files)
        {
            foreach (var type in Flatten(entry.Types))
            {
                Offer(type.FqName, new DocSite(file, type.Line, type.Doc, type.Namespace));
                foreach (var member in type.Members)
                    Offer($"{type.FqName}.{member.Name}", new DocSite(file, member.Line, member.Doc, type.Namespace));
            }
        }
        return found;
    }

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
