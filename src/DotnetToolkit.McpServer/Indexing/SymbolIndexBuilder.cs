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
/// reload; hash-gated incremental invalidation is a Phase 3 refinement. While this has not yet
/// completed its first pass, <see cref="Ready"/> is false and symbol tools report
/// <c>staleness: index_only</c> for reference counts (Conformance C11).
/// </summary>
public sealed class SymbolIndexBuilder
{
    private readonly WorkspaceHost _workspace;
    private readonly SymbolStore _symbols;
    private readonly ILogger<SymbolIndexBuilder> _log;
    private int _running;

    public bool Ready { get; private set; }

    public SymbolIndexBuilder(WorkspaceHost workspace, SymbolStore symbols, ILogger<SymbolIndexBuilder> log)
    {
        _workspace = workspace;
        _symbols = symbols;
        _log = log;
    }

    public void Start() => _ = Task.Run(RebuildAsync);

    public async Task RebuildAsync()
    {
        if (!_symbols.Available)
            return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return;
        try
        {
            var solution = await _workspace.GetSolutionAsync(TimeSpan.FromMinutes(5));
            if (solution is null)
                return;

            var symbols = new Dictionary<string, SymbolStore.SymbolRow>(StringComparer.Ordinal);
            var sites = new List<(string, string, int, int, string?)>();
            var edges = new HashSet<SymbolStore.EdgeRow>();
            var facts = new List<SymbolStore.FactsRow>();

            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation is null)
                    continue;

                // Calls originating in a test project are recorded as test_reference edges as well as
                // ordinary calls, so referenceCounts.tests and ladder level 5 have real data.
                var isTestProject = IsTestProject(project);

                foreach (var document in project.Documents)
                {
                    var tree = await document.GetSyntaxTreeAsync();
                    if (tree is null)
                        continue;
                    var model = compilation.GetSemanticModel(tree);
                    var root = await tree.GetRootAsync();
                    var file = document.FilePath ?? tree.FilePath;

                    foreach (var node in root.DescendantNodes().Where(IsDeclaration))
                        IndexDeclaration(node, model, project.Name, file, symbols, sites, edges, facts, isTestProject);
                    IndexTopLevelStatements(root, model, edges, isTestProject);
                }
            }

            // Fingerprint-gated: only rows whose version layers actually moved are rewritten, so a
            // formatting-only sweep costs a comparison pass and no semantic writes at all.
            var stats = _symbols.ApplyIncremental([.. symbols.Values], sites, [.. edges], facts);
            Ready = true;
            _log.LogInformation(
                "Symbol index updated: {Updated} changed, {Removed} removed, {Unchanged} untouched ({Symbols} symbols, {Edges} edges)",
                stats.Updated, stats.Removed, stats.Unchanged, symbols.Count, edges.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Symbol index build failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static bool IsDeclaration(SyntaxNode node) => node
        is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or BaseMethodDeclarationSyntax
        or PropertyDeclarationSyntax or EventDeclarationSyntax or BaseFieldDeclarationSyntax;

    private void IndexDeclaration(
        SyntaxNode node, SemanticModel model, string project, string? file,
        Dictionary<string, SymbolStore.SymbolRow> symbols,
        List<(string, string, int, int, string?)> sites,
        HashSet<SymbolStore.EdgeRow> edges,
        List<SymbolStore.FactsRow> facts,
        bool isTestProject)
    {
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
            if (!symbols.ContainsKey(id))
            {
                symbols[id] = new SymbolStore.SymbolRow(
                    id,
                    // Default format: fully qualified WITHOUT the "global::" prefix, so the stored name
                    // is both compact and directly resolvable by SymbolResolver.
                    symbol.ToDisplayString(),
                    SymbolKey.KindOf(symbol),
                    symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
                    project,
                    decl,
                    body,
                    symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    OutlineBuilder.SummaryFromXml(symbol.GetDocumentationCommentXml()),
                    RefsHash: SemanticFingerprint.ComputeRefs(ReferencedSymbolIds(node, model)),
                    ApiHash: SemanticFingerprint.ComputeApi(symbol as INamedTypeSymbol ?? symbol.ContainingType, DeclHashOf));
            }
            sites.Add((id, file ?? "-", span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, null));
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
            CollectCallEdges(node, owner, model, ownerId, edges, isTestProject);

            if (body is not null && MechanicalFactsExtractor.Extract(node, owner, model) is { } extracted)
                facts.Add(new SymbolStore.FactsRow(ownerId, JsonSerializer.Serialize(extracted), body));
        }
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

    /// <summary>
    /// A project counts as a test project when it references a known test framework. This drives
    /// test_reference edges, so it must not be a guess based on the project name alone.
    /// </summary>
    private static bool IsTestProject(Project project) =>
        project.MetadataReferences.Any(r =>
            r.Display is { } d &&
            (d.Contains("xunit", StringComparison.OrdinalIgnoreCase)
             || d.Contains("nunit", StringComparison.OrdinalIgnoreCase)
             || d.Contains("Microsoft.VisualStudio.TestPlatform", StringComparison.OrdinalIgnoreCase)
             || d.Contains("MSTest", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Top-level statements are <see cref="GlobalStatementSyntax"/>, not member declarations, so the
    /// ordinary declaration walk never visits them. Without this, every call made from a Program.cs
    /// entry point is invisible to the edge cache and <c>referenceCounts.callers</c> under-reports —
    /// which matters because a false zero tells the agent to skip an expansion it needs (P1.4).
    /// </summary>
    private static void IndexTopLevelStatements(
        SyntaxNode root, SemanticModel model, HashSet<SymbolStore.EdgeRow> edges, bool isTestProject)
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
        foreach (var statement in statements)
            CollectCallEdges(statement, entry, model, entryId, edges, isTestProject);
    }

    /// <summary>Records call edges from a member body via the semantic model (spec §18 call edges).</summary>
    private static void CollectCallEdges(
        SyntaxNode member, ISymbol from, SemanticModel model, string fromId,
        HashSet<SymbolStore.EdgeRow> edges, bool isTestProject)
    {
        foreach (var expr in member.DescendantNodes().OfType<ExpressionSyntax>())
        {
            if (expr is not (InvocationExpressionSyntax or ObjectCreationExpressionSyntax
                or MemberAccessExpressionSyntax or IdentifierNameSyntax))
                continue;

            var target = model.GetSymbolInfo(expr).Symbol;
            if (target is not (IMethodSymbol or IPropertySymbol or IEventSymbol or IFieldSymbol))
                continue;
            if (!target.Locations.Any(l => l.IsInSource))
                continue;
            if (SymbolEqualityComparer.Default.Equals(target.ContainingSymbol, from))
                continue;

            var line = expr.SyntaxTree.GetLineSpan(expr.Span).StartLinePosition.Line + 1;
            var file = expr.SyntaxTree.FilePath;
            var toId = SymbolKey.IdOf(target.OriginalDefinition);
            edges.Add(new SymbolStore.EdgeRow(fromId, toId, "call", DispatchKind(target), file, line));
            if (isTestProject)
                edges.Add(new SymbolStore.EdgeRow(fromId, toId, "test_reference", null, file, line));
        }
    }

    private static string DispatchKind(ISymbol target)
    {
        if (target.ContainingType?.TypeKind == TypeKind.Interface)
            return "interface";
        if (target is IMethodSymbol { MethodKind: MethodKind.DelegateInvoke })
            return "delegate";
        if (target.IsVirtual || target.IsAbstract || target.IsOverride)
            return "virtual";
        return "direct";
    }
}
