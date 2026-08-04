using System.Text.Json;
using System.Text.RegularExpressions;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Extensions.Logging;

namespace DotnetToolkit.McpServer.Indexing;

/// <summary>
/// One syntax-index match from <see cref="ProjectIndex.FindSymbol"/>. <paramref name="Kind"/> is the
/// same single-letter code documented on <see cref="MemberEntry"/> (C class, I interface, S struct,
/// R record, E enum, D delegate, M method, K constructor, P property/indexer, F field/enum-member, V event).
/// </summary>
public sealed record SymbolHit(string Kind, string Name, string FqName, string File, int Line, string? Doc, string? Signature);

/// <summary>
/// Syntax-tier index of every .cs file under the target root: file tree, type outlines,
/// doc summaries. Built without MSBuild so it is available seconds after startup.
/// Invalidation is mtime-polling based because inotify does not work on /mnt/* (WSL DrvFs).
/// </summary>
public sealed class ProjectIndex : IDisposable
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

    // Which files may contribute a synthesized Program.Main. Cached because computing it walks the tree
    // for project files, and invalidated only when a project/solution file actually moves.
    private EntryPointScope? _entryPointScope;

    private volatile string _state = "not-started";
    public string State => _state;
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
            _state = "building";
            LoadCache();
            await SweepAsync(full: true);
            _state = "ready";
            _log.LogInformation("Index ready: {Files} files, {Types} types", FileCount, TypeCount);
        }
        catch (Exception ex)
        {
            _state = $"failed: {ex.Message}";
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

            // Computed ONCE per sweep, never per file: it walks the tree for project files, and the parse
            // loop below runs over every changed .cs on what may be slow /mnt/* IO.
            _entryPointScope ??= BuildEntryPointScope();
            var entryPointScope = _entryPointScope;

            Parallel.ForEach(toParse, item =>
            {
                try
                {
                    var entry = OutlineBuilder.Build(
                        File.ReadAllText(item.Abs), item.Mtime, item.Len,
                        synthesizeEntryPoint: entryPointScope.Covers(item.Rel));
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
            if (projectFilesMoved)
                _entryPointScope = null;   // a moved .csproj/.sln can change which files a project compiles

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
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to enumerate {Dir} while sweeping for .cs files", dir);
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
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to enumerate {Dir} while sweeping for project files", dir);
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
    /// Which repo-relative files belong to a project this solution compiles, for deciding whether a
    /// top-level-statements file may contribute its synthesized <c>Program.Main</c> to the index.
    /// </summary>
    /// <remarks>
    /// Deliberately the same rule <c>scripts/lib-cs-membership.sh</c> applies for the read guards: a file
    /// is in scope when some ancestor directory holds a <c>.csproj</c> and no ancestor below the repo root
    /// holds its own <c>.sln</c>/<c>.slnx</c>. The nested-solution test is what excludes a test fixture's
    /// throwaway sample solution, and the <c>.csproj</c> requirement is what excludes a standalone
    /// <c>dotnet run</c> script. Both otherwise claim the name Program alongside the real entry point.
    /// </remarks>
    private sealed record EntryPointScope(HashSet<string> ProjectDirs, HashSet<string> NestedSolutionDirs)
    {
        public bool Covers(string relPath)
        {
            var dir = ParentOf(relPath);
            var underAProject = false;
            while (true)
            {
                // The repo root's own solution is the one being indexed, so only a solution BELOW the root
                // marks an independent tree — hence the non-empty test rather than checking the root too.
                if (dir.Length > 0 && NestedSolutionDirs.Contains(dir))
                    return false;
                underAProject |= ProjectDirs.Contains(dir);
                if (dir.Length == 0)
                    return underAProject;
                dir = ParentOf(dir);
            }
        }

        private static string ParentOf(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash < 0 ? "" : path[..slash];
        }
    }

    private EntryPointScope BuildEntryPointScope()
    {
        var projectDirs = new HashSet<string>(StringComparer.Ordinal);
        var nestedSolutionDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var abs in EnumerateProjectFiles())
        {
            var rel = _locator.RelPath(abs);
            var slash = rel.LastIndexOf('/');
            var dir = slash < 0 ? "" : rel[..slash];
            switch (Path.GetExtension(abs).ToLowerInvariant())
            {
                case ".csproj":
                    projectDirs.Add(dir);
                    break;
                case ".sln" or ".slnx":
                    if (dir.Length > 0)
                        nestedSolutionDirs.Add(dir);
                    break;
            }
        }
        return new EntryPointScope(projectDirs, nestedSolutionDirs);
    }

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

    /// <summary>Where a declaration sits, its extracted XML doc &lt;summary&gt; text if any, its declaration's
    /// own end line, the namespace its declaring type belongs to, which XML doc sections beyond
    /// summary it carries (comma-joined tags, e.g. "remarks,returns") for search_index's xmlDoc filter,
    /// and every count search_index's shape column is rendered from.</summary>
    /// <remarks>
    /// The counts split into two groups on purpose. <paramref name="MemberCount"/>,
    /// <paramref name="NestedCount"/>, <paramref name="ParameterCount"/> and
    /// <paramref name="LandmarkCount"/> are NULLABLE, and null means the fact cannot apply to this kind
    /// of declaration — a member has no members, a field has no parameter list, a delegate has no member
    /// list a caller could navigate by. <paramref name="DocLines"/>, <paramref name="CommentLines"/> and
    /// <paramref name="AttributeCount"/> are plain counts every declaration can have, where zero means it
    /// genuinely has none. Both render as an absent letter; the distinction exists so that this is the
    /// only place deciding which letters a kind can ever show.
    ///
    /// They are read straight off the outline the index already built, so they cost nothing to carry.
    /// On a TYPE, <paramref name="CommentLines"/> is the transitive total across its members — see
    /// <see cref="OutlineBuilder.CommentLines"/>.
    /// </remarks>
    public sealed record DocSite(
        string File, int Line, int EndLine, string? Doc, string Namespace,
        string? DocSections = null, int? MemberCount = null, int DocLines = 0, int CommentLines = 0,
        int? NestedCount = null, int? ParameterCount = null, int? LandmarkCount = null,
        int AttributeCount = 0);

    /// <summary>Members a type entry declares, or null for a delegate.</summary>
    /// <param name="type">The outline entry to count.</param>
    /// <returns>The declared-member count, or null when a member list is meaningless for the kind.</returns>
    /// <remarks>
    /// A delegate's outline carries one synthesized entry standing for its own signature, so reporting it
    /// as "1 member" would invite a caller to fetch a member list that does not exist.
    /// </remarks>
    private static int? MemberCountOf(TypeEntry type) => type.Kind == "D" ? null : type.Members.Count;

    /// <summary>Parameter count of a delegate's signature, or null for any other kind of type.</summary>
    /// <param name="type">The outline entry to read.</param>
    /// <returns>The delegate's declared parameter count, or null.</returns>
    private static int? DelegateArity(TypeEntry type) =>
        type.Kind == "D" && type.Members.Count > 0
        && SymbolResolver.ParameterArity(type.Members[0].Signature) is var arity and >= 0
            ? arity
            : null;

    /// <summary>The member's parameter count, or null for a kind that declares no parameter list.</summary>
    /// <param name="member">The outline entry to read.</param>
    /// <param name="arity">Its already-computed arity, <c>-1</c> when its signature has no parameter list.</param>
    /// <returns>The parameter count, or null so a field never reports a structural zero.</returns>
    private static int? MemberArity(MemberEntry member, int arity) =>
        arity >= 0 && member.Kind is "M" or "K" ? arity : null;

    /// <summary>
    /// Resolves fully-qualified names to their declaration site, in one pass over the index.
    ///
    /// Read from the syntax index rather than stored alongside the symbol row on purpose. A line number
    /// stored next to a symbol would be invalidated by that symbol's own hashes, and editing *above* a
    /// declaration moves its line without changing a single one of them: the row would not be rewritten
    /// and the stored line would rot silently. The index is mtime-swept per file, so it moves whenever
    /// the file does, which is exactly what a line number depends on.
    /// </summary>
    /// <param name="fqNames">
    /// The names to resolve, each keeping its parameter list where the caller has one — that list is what
    /// tells the members of an overload set apart. Results come back keyed by the exact string passed in.
    /// </param>
    /// <returns>One site per name that resolved; a name that stayed ambiguous is absent.</returns>
    public IReadOnlyDictionary<string, Site> Locate(IReadOnlySet<string> fqNames)
        => LocateWithDocs(fqNames).ToDictionary(
            kv => kv.Key, kv => new Site(kv.Value.File, kv.Value.Line), StringComparer.Ordinal);

    /// <summary>
    /// <see cref="Locate"/> plus each declaration's doc summary, namespace and doc-section tags.
    /// </summary>
    /// <remarks>
    /// The index keys members by bare name, so a requested name is matched with its parameter list
    /// dropped and then disambiguated by parameter count, and — for members that collide on count too —
    /// by their parameter TYPES, which both the stored name and the indexed signature can be reduced to a
    /// comparable key for. A member name that still resolves to more than one distinct site (a caller that
    /// had no parameter list to offer, or types that reduce to different text for the same member) is
    /// omitted rather than guessed at: pointing at the wrong overload is worse than saying nothing, and
    /// absent already means "look it up". A TYPE name resolving to more than one site is never that kind
    /// of ambiguity — C# forbids two distinct types sharing one fully-qualified name, so multiple sites
    /// there can only be partial-class fragments of the same symbol, and they collapse to one stable
    /// representative instead of being dropped. A method that declares its own type parameters is keyed
    /// under both its bare identifier and its declared <c>Name&lt;T&gt;</c> form, because the syntax index
    /// stores the bare one while the symbol store asks for the declared one.
    /// </remarks>
    /// <param name="fqNames">The names to resolve, keyed as described on <see cref="Locate"/>.</param>
    /// <returns>One site per name that resolved; a name that stayed ambiguous is absent.</returns>
    public IReadOnlyDictionary<string, DocSite> LocateWithDocs(IReadOnlySet<string> fqNames)
    {
        var requested = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var name in fqNames)
        {
            var bare = SymbolResolver.NameWithoutParameters(name);
            if (!requested.TryGetValue(bare, out var sameBareName))
                requested[bare] = sameBareName = [];
            sameBareName.Add(name);
        }

        var candidates = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
        void Offer(string bareName, DocSite site, int arity, string? parameterTypes, bool isType)
        {
            if (!requested.ContainsKey(bareName))
                return;
            if (!candidates.TryGetValue(bareName, out var forName))
                candidates[bareName] = forName = [];
            forName.Add(new Candidate(site, arity, parameterTypes, isType));
        }

        foreach (var (file, entry) in _files)
        {
            foreach (var type in Flatten(entry.Types))
            {
                Offer(
                    type.FqName,
                    new DocSite(
                        file, type.Line, type.EndLine, type.Doc, type.Namespace, type.DocSections,
                        MemberCountOf(type), type.DocLines, type.CommentLines,
                        NestedCount: type.Nested.Count, ParameterCount: DelegateArity(type),
                        AttributeCount: type.AttributeCount),
                    -1, null, isType: true);
                foreach (var member in type.Members)
                {
                    var arity = SymbolResolver.ParameterArity(member.Signature);
                    var site = new DocSite(
                        file, member.Line, member.EndLine, member.Doc, type.Namespace, member.DocSections,
                        MemberCount: null, member.DocLines, member.CommentLines,
                        ParameterCount: MemberArity(member, arity), LandmarkCount: member.LandmarkCount,
                        AttributeCount: member.AttributeCount);
                    var parameterTypes = SymbolResolver.SignatureParameterTypeKey(member.Signature);
                    Offer($"{type.FqName}.{member.Name}", site, arity, parameterTypes, isType: false);

                    // A method's own type-parameter list lives in its stored signature, not in its stored
                    // name, so a generic method was only ever offered as "Pick" while the symbol store asks
                    // for "Pick<T>" -- the key never matched and every such hit came back locationless.
                    // Offer the declared form as a SECOND key rather than stripping the list off the
                    // request, which would collapse a generic method onto a same-named non-generic sibling.
                    var declaredName = SymbolResolver.NameWithoutParameters(member.Signature);
                    if (declaredName.Length > member.Name.Length
                        && declaredName.StartsWith(member.Name, StringComparison.Ordinal)
                        && declaredName[member.Name.Length] == '<')
                    {
                        Offer($"{type.FqName}.{declaredName}", site, arity, parameterTypes, isType: false);
                    }
                }
            }
        }

        var found = new Dictionary<string, DocSite>(StringComparer.Ordinal);
        foreach (var (bareName, names) in requested)
        {
            if (!candidates.TryGetValue(bareName, out var forName))
                continue;
            foreach (var name in names)
            {
                if (Disambiguate(forName, SymbolResolver.ParameterArity(name), SymbolResolver.ParameterTypeKey(name)) is { } site)
                    found[name] = site;
            }
        }

        return found;
    }

    /// <summary>One declaration the index holds under a bare name, with what it takes to tell it apart.</summary>
    private sealed record Candidate(DocSite Site, int Arity, string? ParameterTypes, bool IsType);

    /// <summary>Picks the site a requested name meant, or null when the choice stays genuinely ambiguous.</summary>
    private static DocSite? Disambiguate(List<Candidate> candidates, int requestedArity, string? requestedParameterTypes)
    {
        if (candidates.Count == 1)
            return candidates[0].Site;

        if (candidates.All(c => c.IsType))
        {
            return candidates
                .OrderBy(c => c.Site.File, StringComparer.Ordinal)
                .ThenBy(c => c.Site.Line)
                .First().Site;
        }

        var distinct = candidates.Select(c => c.Site).Distinct().ToList();
        if (distinct.Count == 1)
            return distinct[0];

        if (requestedArity < 0)
            return null;

        var byArity = candidates.Where(c => c.Arity == requestedArity).ToList();
        var arityChoice = byArity.Select(c => c.Site).Distinct().ToList();
        if (arityChoice.Count == 1)
            return arityChoice[0];

        // Same name AND same parameter count leaves only the parameter types to choose on. Stopping at
        // arity dropped the location from every member of an arity-colliding overload set -- five
        // constructors of one type in the measured case -- forcing a get_symbol round trip purely to
        // navigate to something the index already knew the line of.
        if (requestedParameterTypes is null)
            return null;

        var typeChoice = byArity
            .Where(c => string.Equals(c.ParameterTypes, requestedParameterTypes, StringComparison.Ordinal))
            .Select(c => c.Site)
            .Distinct()
            .ToList();
        return typeChoice.Count == 1 ? typeChoice[0] : null;
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

    public void Dispose() => _sweepGate.Dispose();
}
