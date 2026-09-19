using System.Text.Json;
using DotnetToolkit.McpServer.Fingerprint;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace DotnetToolkit.McpServer.Indexing;

/// <summary>
/// Background job that populates the SQLite symbol index and call-edge cache (spec §18) from the
/// loaded MSBuild workspace. Runs a full rebuild after the workspace becomes ready and after each
/// reload, and a document-scoped refresh whenever the syntax index reports that files moved on disk —
/// so an edit made with any tool reaches <c>search_index</c>, the <c>sym_</c> ids and the edge cache
/// without a manual <c>reload_workspace</c>. While the first full pass has not completed,
/// <see cref="Ready"/> is false and symbol tools report <c>staleness: index_only</c> for reference
/// counts (Conformance C11).
/// </summary>
/// <remarks>
/// Every pass — full or scoped — runs under one gate, in order, so a scoped refresh queued during a
/// rebuild runs after it rather than interleaving writes with it. Scoped refreshes coalesce: paths
/// queued while another refresh is waiting on the gate are picked up by that refresh, and the caller
/// who queued them learns they were handled without running a second pass over the same documents.
/// </remarks>
public sealed class SymbolIndexBuilder
{
    /// <summary>
    /// Above this many referring documents, a removed symbol's referrers are not re-indexed one by one;
    /// the pass queues a full rebuild instead. A rename of a widely used member is the case: its
    /// callers are already rewritten on disk, and one compilation walk is cheaper than a hundred
    /// per-document ones.
    /// </summary>
    private const int MaxReferrerDocumentsPerRefresh = 50;

    private readonly WorkspaceHost _workspace;
    private readonly SymbolStore _symbols;
    private readonly KnowledgeStore _store;
    private readonly ILogger<SymbolIndexBuilder> _log;
    private readonly SolutionLocator _locator;

    private readonly SemaphoreSlim _jobGate = new(1, 1);
    private readonly object _pendingLock = new();
    private readonly HashSet<string> _pendingPaths = new(StringComparer.Ordinal);
    private HashSet<string>? _inFlightPaths;
    private TaskCompletionSource<RefreshResult>? _inFlight;
    private int _rebuildRequested;

    private volatile bool _ready;
    public bool Ready => _ready;

    /// <summary>When the store last absorbed a pass, full or scoped; null before the first.</summary>
    public DateTimeOffset? LastRefreshUtc { get; private set; }

    /// <summary>Paths queued for a scoped refresh that no pass has picked up yet.</summary>
    public int PendingCount { get { lock (_pendingLock) return _pendingPaths.Count; } }

    /// <summary>What a scoped refresh did, for the control channel and <c>workspace_status</c> to report.</summary>
    /// <param name="Files">Documents this pass re-indexed (after sibling and referrer expansion).</param>
    /// <param name="Symbols">Rows whose version layers moved.</param>
    /// <param name="Removed">Ids that vanished from the scoped documents.</param>
    /// <param name="NotInSolution">Paths the loaded solution holds no document for — new files, which only a reload can add.</param>
    /// <param name="FullRebuildQueued">True when the pass fell back to a full rebuild (workspace behind disk, or too many referrers).</param>
    /// <param name="HandledByEarlierPass">True when another refresh had already absorbed every queued path before this one ran.</param>
    public sealed record RefreshResult(
        int Files, int Symbols, int Removed, IReadOnlyList<string> NotInSolution,
        bool FullRebuildQueued, bool HandledByEarlierPass);

    public SymbolIndexBuilder(
        WorkspaceHost workspace, SymbolStore symbols, KnowledgeStore store, SolutionLocator locator,
        ILogger<SymbolIndexBuilder> log, ProjectIndex? index = null)
    {
        _workspace = workspace;
        _symbols = symbols;
        _store = store;
        _locator = locator;
        _log = log;

        // The workspace subscribes to the same event and swaps the new text into the live solution
        // synchronously in its handler; it subscribed first (it is constructed first), so by the time
        // this runs the solution the scoped pass will read already holds the edit. Optional only so a
        // test can build the pieces by hand without wiring a sweep.
        if (index is not null)
            index.FilesChanged += OnFilesChanged;
    }

    private void OnFilesChanged(IReadOnlyList<string> changedRelPaths, bool structural)
    {
        if (structural)
        {
            // A file appeared or vanished: the workspace is reloading (its own handler triggered it),
            // and the rebuild below waits on that load before walking the new solution.
            Start();
            return;
        }
        var abs = changedRelPaths.Select(_locator.AbsPath).ToList();
        _ = RefreshDocumentsAsync(abs).ContinueWith(
            t => _log.LogWarning(t.Exception, "Scoped symbol refresh failed"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Queues a full rebuild. Coalesces: a request made while one is running yields exactly one more.</summary>
    public void Start()
    {
        Interlocked.Exchange(ref _rebuildRequested, 1);
        _ = Task.Run(RebuildAsync);
    }

    /// <summary>
    /// Runs a full rebuild if one is requested (or if called directly), waiting for any pass in flight.
    /// Direct callers (the test fixture) always get a pass; queued ones get it once per request burst.
    /// </summary>
    public async Task RebuildAsync()
    {
        if (!_symbols.Available)
            return;
        await _jobGate.WaitAsync();
        try
        {
            Interlocked.Exchange(ref _rebuildRequested, 0);
            await RebuildCoreAsync();
        }
        finally
        {
            _jobGate.Release();
        }
    }

    private async Task RebuildCoreAsync()
    {
        try
        {
            // The store's own pages are checked here rather than in its constructor: the check reads
            // every one of them (~0.7s across a 9p mount) and the MCP handshake waits on that
            // constructor. A damaged cache is quarantined and rebuilt from source before this pass
            // repopulates it, so the repair costs one index build rather than a lost session.
            if (!_store.VerifyAndHeal())
                return;

            // Before anything else: heal an FTS mirror left inconsistent by an older build. This runs
            // ahead of the workspace wait because it needs no semantic model, and search_index answers
            // from the store long before MSBuild finishes.
            var repaired = _symbols.RepairSearchIndex();
            if (repaired > 0)
                _log.LogInformation("Search index mirror rebuilt: {Rows} symbols", repaired);

            var solution = await _workspace.GetSolutionAsync(TimeSpan.FromMinutes(5));
            if (solution is null)
                return;

            var pass = new Pass();
            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation is null)
                    continue;
                foreach (var document in project.Documents)
                    await IndexDocumentAsync(document, compilation, project.Name, pass);
            }

            // Fingerprint-gated: only rows whose version layers actually moved are rewritten, so a
            // formatting-only sweep costs a comparison pass and no semantic writes at all.
            var stats = _symbols.ApplyIncremental([.. pass.Symbols.Values], [.. pass.Edges], pass.Facts, pass.Sites);
            lock (_pendingLock)
                _pendingPaths.Clear();   // a full pass covers everything a scoped one was waiting to do
            LastRefreshUtc = DateTimeOffset.UtcNow;
            _ready = true;
            _log.LogInformation(
                "Symbol index updated: {Updated} changed, {Removed} removed, {Unchanged} untouched ({Symbols} symbols, {Edges} edges)",
                stats.Updated, stats.Removed, stats.Unchanged, pass.Symbols.Count, pass.Edges.Count);
        }
        catch (Exception ex)
        {
            // A failed index write leaves the store serving whatever it already held, which a caller
            // cannot tell apart from a current answer -- the symptom is a cache silently frozen at an
            // older revision. ReportFailure disables it when the file itself is the cause, and logs
            // the path; anything else is an ordinary build failure and only reported.
            if (!_store.ReportFailure(ex))
                _log.LogError(ex, "Symbol index build failed");
        }
    }

    /// <summary>
    /// Re-indexes the symbols declared in the given files, and only those, into the store.
    /// </summary>
    /// <param name="absPaths">Absolute paths of <c>.cs</c> files that changed on disk.</param>
    /// <remarks>
    /// Expands the set twice before writing. First to the other parts of any partial type the files
    /// declare, so a partial's row is computed from all of its parts rather than one. Then, once the
    /// pass knows which symbols vanished from the scope, to the documents whose call edges still name
    /// them — a renamed or deleted member's callers — so their edges are recomputed too; above
    /// <see cref="MaxReferrerDocumentsPerRefresh"/> of those it queues a full rebuild instead. A
    /// workspace whose copy of any path is still behind disk (the change arrived mid-load) also falls
    /// back to a full rebuild, which waits for that load and then sees the text.
    /// </remarks>
    public async Task<RefreshResult> RefreshDocumentsAsync(IReadOnlyList<string> absPaths)
    {
        var none = new RefreshResult(0, 0, 0, [], false, false);
        if (!_symbols.Available || absPaths.Count == 0)
            return none;

        var wanted = absPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
        Task<RefreshResult>? sameAsInFlight = null;
        lock (_pendingLock)
        {
            // The sweep's handler and the refresh hook ask for the same paths within milliseconds of
            // each other; the second asker joins the pass already running rather than queuing a repeat
            // that would recompile the same documents to find nothing moved.
            if (_inFlight is not null && _inFlightPaths is not null && wanted.IsSubsetOf(_inFlightPaths))
                sameAsInFlight = _inFlight.Task;
            else
                _pendingPaths.UnionWith(wanted);
        }
        if (sameAsInFlight is not null)
            return await sameAsInFlight;

        await _jobGate.WaitAsync();
        TaskCompletionSource<RefreshResult>? completion = null;
        try
        {
            string[] batch;
            lock (_pendingLock)
            {
                batch = [.. _pendingPaths];
                _pendingPaths.Clear();
                if (batch.Length > 0)
                {
                    _inFlightPaths = [.. batch];
                    _inFlight = completion = new TaskCompletionSource<RefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            if (batch.Length == 0)
                return none with { HandledByEarlierPass = true };

            RefreshResult result;
            // A rebuild requested in the meantime supersedes this batch: it will walk these files too.
            if (Volatile.Read(ref _rebuildRequested) == 1)
            {
                Interlocked.Exchange(ref _rebuildRequested, 0);
                await RebuildCoreAsync();
                result = none with { Files = batch.Length, FullRebuildQueued = true };
            }
            else
            {
                result = await RefreshCoreAsync(batch);
            }
            completion!.SetResult(result);
            return result;
        }
        catch (Exception ex)
        {
            completion?.TrySetException(ex);
            throw;
        }
        finally
        {
            lock (_pendingLock)
            {
                _inFlight = null;
                _inFlightPaths = null;
            }
            _jobGate.Release();
        }
    }

    private async Task<RefreshResult> RefreshCoreAsync(IReadOnlyList<string> batch)
    {
        var solution = await _workspace.GetSolutionAsync(TimeSpan.FromMinutes(5));
        if (solution is null)
            return new RefreshResult(0, 0, 0, [], false, false);

        if (await _workspace.IsBehindDiskAsync(batch))
        {
            _log.LogInformation("Workspace is behind disk for a refreshed file; falling back to a full rebuild");
            await RebuildCoreAsync();
            return new RefreshResult(batch.Count, 0, 0, [], true, false);
        }

        var notInSolution = new List<string>();
        var documents = new Dictionary<DocumentId, Document>();
        foreach (var path in batch)
        {
            var ids = solution.GetDocumentIdsWithFilePath(path);
            if (ids.IsEmpty)
            {
                notInSolution.Add(path);
                continue;
            }
            foreach (var id in ids)
            {
                if (solution.GetDocument(id) is { } document)
                    documents[id] = document;
            }
        }
        if (documents.Count == 0)
            return new RefreshResult(0, 0, 0, notInSolution, false, false);

        try
        {
            await ExpandToPartialSiblingsAsync(solution, documents);
            var pass = await IndexDocumentsAsync(documents.Values);
            var scope = ScopeFilesOf(documents.Values);

            // Symbols the scope used to declare and no longer does are known only after indexing, and
            // their referrers only from the store — so the expansion to referrers is a second round.
            var previously = _symbols.SymbolsDeclaredIn(scope);
            var vanished = previously.Where(id => !pass.Symbols.ContainsKey(id)).ToList();
            if (vanished.Count > 0)
            {
                var referrers = _symbols.ReferrerFilesOf(vanished, scope.ToHashSet(StringComparer.Ordinal));
                if (referrers.Count > MaxReferrerDocumentsPerRefresh)
                {
                    _log.LogInformation(
                        "{Count} documents refer to symbols removed from a refreshed file; falling back to a full rebuild",
                        referrers.Count);
                    await RebuildCoreAsync();
                    return new RefreshResult(documents.Count, 0, vanished.Count, notInSolution, true, false);
                }
                var grew = false;
                foreach (var file in referrers)
                {
                    foreach (var id in solution.GetDocumentIdsWithFilePath(file))
                    {
                        if (!documents.ContainsKey(id) && solution.GetDocument(id) is { } document)
                        {
                            documents[id] = document;
                            grew = true;
                        }
                    }
                }
                if (grew)
                {
                    pass = await IndexDocumentsAsync(documents.Values);
                    scope = ScopeFilesOf(documents.Values);
                }
            }

            var (stats, removed) = _symbols.ApplyIncrementalScoped(
                [.. pass.Symbols.Values], [.. pass.Edges], pass.Facts, pass.Sites, scope);
            LastRefreshUtc = DateTimeOffset.UtcNow;
            _log.LogInformation(
                "Symbol index refreshed: {Files} files, {Updated} changed, {Removed} removed",
                documents.Count, stats.Updated, removed.Count);
            return new RefreshResult(documents.Count, stats.Updated, removed.Count, notInSolution, false, false);
        }
        catch (Exception ex)
        {
            if (!_store.ReportFailure(ex))
                _log.LogError(ex, "Scoped symbol refresh failed");
            return new RefreshResult(0, 0, 0, notInSolution, false, false);
        }
    }

    private static IReadOnlyCollection<string> ScopeFilesOf(IEnumerable<Document> documents) =>
        documents.Select(d => d.FilePath).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Adds the documents declaring the other parts of every partial type the given documents declare.
    /// </summary>
    private static async Task ExpandToPartialSiblingsAsync(Solution solution, Dictionary<DocumentId, Document> documents)
    {
        foreach (var document in documents.Values.ToList())
        {
            var model = await document.GetSemanticModelAsync();
            var root = await document.GetSyntaxRootAsync();
            if (model is null || root is null)
                continue;
            foreach (var node in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (!node.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword)))
                    continue;
                if (model.GetDeclaredSymbol(node) is not { } type)
                    continue;
                foreach (var reference in type.DeclaringSyntaxReferences)
                {
                    var sibling = solution.GetDocument(reference.SyntaxTree);
                    if (sibling is not null && !documents.ContainsKey(sibling.Id))
                        documents[sibling.Id] = sibling;
                }
            }
        }
    }

    /// <summary>Everything one indexing pass collects, whether over the whole solution or a few documents.</summary>
    private sealed class Pass
    {
        public Dictionary<string, SymbolStore.SymbolRow> Symbols { get; } = new(StringComparer.Ordinal);
        public HashSet<SymbolStore.EdgeRow> Edges { get; } = [];
        public List<SymbolStore.FactsRow> Facts { get; } = [];
        public List<SymbolStore.SiteRow> Sites { get; } = [];
    }

    private async Task<Pass> IndexDocumentsAsync(IEnumerable<Document> documents)
    {
        var pass = new Pass();
        foreach (var document in documents)
        {
            var compilation = await document.Project.GetCompilationAsync();
            if (compilation is null)
                continue;
            await IndexDocumentAsync(document, compilation, document.Project.Name, pass);
        }
        return pass;
    }

    private async Task IndexDocumentAsync(Document document, Compilation compilation, string project, Pass pass)
    {
        var tree = await document.GetSyntaxTreeAsync();
        if (tree is null)
            return;
        var model = compilation.GetSemanticModel(tree);
        var root = await tree.GetRootAsync();
        var file = document.FilePath ?? tree.FilePath;
        // This is the only pass that ever sees the path, so the reason a symbol will have no
        // indexed location has to be captured here or the row reaches a caller with no file,
        // no lines and nothing saying why. Two reasons, and they are not the same: a generator
        // writes under obj/, which ProjectIndex prunes, while a package can contribute a
        // Compile item from outside the repo root altogether (the test SDK's synthesized entry
        // point is one), which ProjectIndex never had in scope to begin with.
        var placement = PlacementOf(file);

        foreach (var node in root.DescendantNodes().Where(IsDeclaration))
            IndexDeclaration(node, model, project, placement, file, pass);
        IndexTopLevelStatements(root, model, pass.Edges, pass.Symbols);
    }

    private static bool IsDeclaration(SyntaxNode node) => node
        is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or BaseMethodDeclarationSyntax
        or PropertyDeclarationSyntax or EventDeclarationSyntax or BaseFieldDeclarationSyntax;

    private static void IndexDeclaration(
        SyntaxNode node, SemanticModel model, string project, DeclarationPlacement placement,
        string? file, Pass pass)
    {
        var symbols = pass.Symbols;
        var edges = pass.Edges;
        var facts = pass.Facts;
        // A field declaration can introduce several symbols; every other kind introduces one.
        var declared = node is BaseFieldDeclarationSyntax field
            ? field.Declaration.Variables.Select(v => model.GetDeclaredSymbol(v)).OfType<ISymbol>().ToList()
            : model.GetDeclaredSymbol(node) is { } single ? [single] : [];

        if (declared.Count == 0)
            return;

        var (decl, body) = SyntaxFingerprint.Compute(node);
        var span = node.SyntaxTree.GetLineSpan(node.Span);

        foreach (var symbol in declared)
        {
            var id = SymbolKey.IdOf(symbol);
            if (!string.IsNullOrEmpty(file))
                pass.Sites.Add(new SymbolStore.SiteRow(id, file, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1));
            if (!symbols.ContainsKey(id))
            {
                symbols[id] = new SymbolStore.SymbolRow(
                    id,
                    // Default format: fully qualified WITHOUT the "global::" prefix, so the stored name
                    // is both compact and directly resolvable by SymbolResolver.
                    symbol.ToDisplayString(),
                    SymbolKey.KindOf(symbol),
                    project,
                    decl,
                    body,
                    symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    RefsHash: SemanticFingerprint.ComputeRefs(ReferencedSymbolIds(node, model)),
                    ApiHash: SemanticFingerprint.ComputeApi(symbol as INamedTypeSymbol ?? symbol.ContainingType, DeclHashOf),
                    IsTest: TestAttributes.IsTestMethod(symbol),
                    Modifiers: string.Join(' ', DotnetToolkit.McpServer.Fingerprint.ModifierText.Tags(symbol)),
                    Placement: placement);
            }
        }

        // Call edges are collected per DECLARATION, not per declared symbol: a multi-variable field
        // would otherwise record the same initializer calls once per variable. Gated on the node kind
        // rather than the symbol kind so field/event initializers are covered, while type declarations
        // are excluded (walking a type would re-attribute every member's calls to the type itself).
        if (node is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax
            or EventDeclarationSyntax or BaseFieldDeclarationSyntax)
        {
            var owner = declared[0];
            var ownerId = SymbolKey.IdOf(owner);
            CollectCallEdges(node, owner, model, ownerId, edges, symbols);
            CollectDispatchEdges(owner, ownerId, edges, symbols);

            if (body is not null && MechanicalFactsExtractor.Extract(node, owner, model) is { } extracted)
                facts.Add(new SymbolStore.FactsRow(ownerId, JsonSerializer.Serialize(extracted), body));
        }

        // search_index's implements filter (interfaces only, direct — matches get_symbol's interfaces
        // component). Reuses the generic reference_edges table with a new edge_kind rather than a
        // dedicated table, the same way call edges already do. Canonicalized the same way a call target
        // is, so a constructed interface (IEnumerable<int>, not the open IEnumerable<T>) resolves to the
        // same symbolId as any other reference to that interface, and an external interface (IDisposable)
        // gets a minimal row instead of leaving this edge pointing at nothing in the symbols table.
        if (node is BaseTypeDeclarationSyntax && declared[0] is INamedTypeSymbol namedType)
        {
            var ownerId = SymbolKey.IdOf(namedType);
            foreach (var iface in namedType.Interfaces)
            {
                var canonical = SymbolKey.Canonicalize(iface);
                var ifaceId = SymbolKey.IdOf(canonical);
                edges.Add(new SymbolStore.EdgeRow(ownerId, ifaceId, "implements", null, null));
                RecordExternalIfNeeded(canonical, ifaceId, symbols);
            }

            // The base-class counterpart of the interface edges above, under its OWN edge_kind so that the
            // implements filter (SymbolStore.ImplementorsOf) keeps meaning interfaces and only interfaces.
            // The two together are what let a derived-type count be a SQL lookup instead of
            // SymbolFinder.FindDerivedClassesAsync per hit, matching what get_symbol's implementations
            // component already reports. object is excluded: every type inherits it, so counting it would
            // add the same constant to every row and tell a caller nothing.
            if (namedType.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
                AddDispatchEdge(ownerId, baseType, "inherits", edges, symbols);
        }
    }

    /// <summary>Where a document's declarations sit relative to the tree the syntax index scans.</summary>
    /// <param name="file">The document's own path, absolute, or empty when it has none.</param>
    /// <returns>The placement a symbol declared in this document carries.</returns>
    /// <remarks>
    /// A path that escapes the root reads as <c>../..</c> once made relative, which is the cheapest
    /// reliable test — <see cref="SolutionLocator.IsGeneratedOrBuildPath"/> would not catch it, since a
    /// NuGet package's own directories are named nothing like a build directory.
    /// </remarks>
    private DeclarationPlacement PlacementOf(string? file)
    {
        if (string.IsNullOrEmpty(file))
            return DeclarationPlacement.InTree;

        var relative = _locator.RelPath(file);
        if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return DeclarationPlacement.OutsideRoot;

        return SolutionLocator.IsGeneratedOrBuildPath(relative)
            ? DeclarationPlacement.Generated
            : DeclarationPlacement.InTree;
    }

    /// <summary>Distinct source symbols referenced from a declaration — the input to the refs layer.</summary>
    private static IEnumerable<string> ReferencedSymbolIds(SyntaxNode node, SemanticModel model)
    {
        foreach (var expr in node.DescendantNodes().OfType<ExpressionSyntax>())
        {
            var target = model.GetSymbolInfo(expr).Symbol;
            if (target is not (IMethodSymbol or IPropertySymbol or IEventSymbol or IFieldSymbol or INamedTypeSymbol))
                continue;
            if (!target.Locations.Any(l => l.IsInSource))
                continue;
            yield return SymbolKey.IdOf(target.OriginalDefinition);
        }
    }

    /// <summary>decl-layer hash for an arbitrary member, used when hashing a type's api surface.</summary>
    private static string? DeclHashOf(ISymbol member)
    {
        var reference = member.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null)
            return null;
        var node = reference.GetSyntax();
        if (node is VariableDeclaratorSyntax && node.FirstAncestorOrSelf<BaseFieldDeclarationSyntax>() is { } field)
            node = field;
        return SyntaxFingerprint.Compute(node).Decl;
    }


    private static void IndexTopLevelStatements(
        SyntaxNode root, SemanticModel model, HashSet<SymbolStore.EdgeRow> edges,
        Dictionary<string, SymbolStore.SymbolRow> symbols)
    {
        if (root is not CompilationUnitSyntax unit)
            return;
        var statements = unit.Members.OfType<GlobalStatementSyntax>().ToList();
        if (statements.Count == 0)
            return;

        // The synthesized entry point is the symbol enclosing the first top-level statement.
        if (model.GetEnclosingSymbol(statements[0].SpanStart) is not { } entry)
            return;

        var entryId = SymbolKey.IdOf(entry);
        // entry.ToDisplayString() renders as the literal "<top-level-statements-entry-point>" for a
        // top-level-statements Main, which is neither typeable nor findable by search_index's free-text
        // query -- so the row is named after the synthesized containing type instead ("Program" in the
        // common case), matching the aliases ContextTools.ResolveEntryPointAsync accepts and giving
        // get_call_hierarchy a real displayString. This row has no meaningful project/decl-hash lineage of
        // its own, so it mirrors RecordExternalIfNeeded's minimal-row shape rather than IndexDeclaration's
        // full one.
        if (!symbols.ContainsKey(entryId))
        {
            var typeName = entry.ContainingType?.Name;
            var friendlyName = typeName is { Length: > 0 } ? $"{typeName}.Main" : entry.ToDisplayString();
            symbols[entryId] = new SymbolStore.SymbolRow(
                entryId,
                friendlyName,
                SymbolKey.KindOf(entry),
                Project: "",
                DeclHash: SyntaxFingerprint.Compute(unit).Decl,
                BodyHash: null,
                DisplayString: friendlyName);
        }
        foreach (var statement in statements)
            CollectCallEdges(statement, entry, model, entryId, edges, symbols);
    }

    /// <summary>Records call edges from a member body via the semantic model (spec §18 call edges).</summary>
    private static void CollectCallEdges(
        SyntaxNode member, ISymbol from, SemanticModel model, string fromId,
        HashSet<SymbolStore.EdgeRow> edges, Dictionary<string, SymbolStore.SymbolRow> symbols)
    {
        foreach (var expr in member.DescendantNodes().OfType<ExpressionSyntax>())
        {
            if (expr is not (InvocationExpressionSyntax or ObjectCreationExpressionSyntax
                or MemberAccessExpressionSyntax or IdentifierNameSyntax))
                continue;

            var target = model.GetSymbolInfo(expr).Symbol;
            if (target is not (IMethodSymbol or IPropertySymbol or IEventSymbol or IFieldSymbol))
                continue;
            // A genuinely broken binding (unresolved API, typo) surfaces as an error-typed symbol rather
            // than a null Symbol in some shapes — reject those explicitly now that IsInSource no longer
            // does it as a side effect of filtering out everything without a source location.
            if (target is IErrorTypeSymbol || target.ContainingType?.TypeKind == TypeKind.Error)
                continue;
            if (SymbolEqualityComparer.Default.Equals(target.ContainingSymbol, from))
                continue;

            var canonical = SymbolKey.Canonicalize(target);
            var line = expr.SyntaxTree.GetLineSpan(expr.Span).StartLinePosition.Line + 1;
            var file = expr.SyntaxTree.FilePath;
            var toId = SymbolKey.IdOf(canonical);
            edges.Add(new SymbolStore.EdgeRow(fromId, toId, "call", file, line));
            RecordExternalIfNeeded(canonical, toId, symbols);
        }
    }

    /// <summary>
    /// Records the dispatch edges a member takes part in: <c>overrides</c> to the base member it overrides,
    /// and <c>implements</c> to each interface member it implements.
    /// </summary>
    /// <remarks>
    /// Both exist so a reference count can be answered from one batched SQL lookup over reference_edges. The
    /// alternative is SymbolFinder.FindOverridesAsync / FindImplementationsAsync per symbol -- a solution-wide
    /// walk each, which is affordable for one get_symbol call and not for a 200-hit search page. Paying for it
    /// here moves the cost to index time, where it happens once per declaration instead of once per query.
    ///
    /// The TYPE-level implements edge is written in IndexDeclaration from namedType.Interfaces; this is its
    /// member-level counterpart, sharing an edge_kind because both answer "what implements this". Fields are
    /// skipped because no interface can declare one, which keeps the AllInterfaces scan off the commonest
    /// member kind.
    /// </remarks>
    private static void CollectDispatchEdges(
        ISymbol member, string memberId,
        HashSet<SymbolStore.EdgeRow> edges, Dictionary<string, SymbolStore.SymbolRow> symbols)
    {
        var overridden = member switch
        {
            IMethodSymbol m => (ISymbol?)m.OverriddenMethod,
            IPropertySymbol p => p.OverriddenProperty,
            IEventSymbol e => e.OverriddenEvent,
            _ => null,
        };
        if (overridden is not null)
            AddDispatchEdge(memberId, overridden, "overrides", edges, symbols);

        if (member is IFieldSymbol || member.ContainingType is not { } type)
            return;

        foreach (var iface in type.AllInterfaces)
        {
            foreach (var ifaceMember in iface.GetMembers())
            {
                if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(ifaceMember), member))
                    AddDispatchEdge(memberId, ifaceMember, "implements", edges, symbols);
            }
        }
    }

    /// <summary>Adds one dispatch edge, canonicalizing the target the same way a call target is.</summary>
    private static void AddDispatchEdge(
        string fromId, ISymbol target, string kind,
        HashSet<SymbolStore.EdgeRow> edges, Dictionary<string, SymbolStore.SymbolRow> symbols)
    {
        var canonical = SymbolKey.Canonicalize(target);
        var toId = SymbolKey.IdOf(canonical);
        edges.Add(new SymbolStore.EdgeRow(fromId, toId, kind, null, null));
        RecordExternalIfNeeded(canonical, toId, symbols);
    }

    /// <summary>
    /// Mints a minimal row for a symbol discovered only as an edge target — never as a declaration this
    /// pass walked — because it lives outside this repo's own solution (BCL, a NuGet package, another
    /// assembly). No decl/body hash and no owning project, matching Store/Schema.cs's SymbolOriginColumn
    /// migration: origin marks a referenced identity, not something this repo declares or re-derives from
    /// source, so there is nothing here for a later pass to invalidate against. Namespace IS captured
    /// (unlike decl/body hash, it costs nothing to recompute and needs no source to derive), so
    /// search_index's namespace grouping does not have to fall back to "(unresolved)" just because
    /// there is no local file for the ProjectIndex-based lookup every other row uses.
    /// </summary>
    private static void RecordExternalIfNeeded(
        ISymbol target, string id, Dictionary<string, SymbolStore.SymbolRow> symbols)
    {
        if (target.Locations.Any(l => l.IsInSource) || symbols.ContainsKey(id))
            return;
        symbols[id] = new SymbolStore.SymbolRow(
            id,
            target.ToDisplayString(),
            SymbolKey.KindOf(target),
            Project: "",
            DeclHash: "",
            BodyHash: null,
            DisplayString: target.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Origin: "external",
            DocumentationId: SymbolKey.DocumentationIdOf(target),
            Namespace: target.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null);
    }

}
