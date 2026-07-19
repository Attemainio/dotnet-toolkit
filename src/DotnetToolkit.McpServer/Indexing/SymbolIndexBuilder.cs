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

            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation is null)
                    continue;

                foreach (var document in project.Documents)
                {
                    var tree = await document.GetSyntaxTreeAsync();
                    if (tree is null)
                        continue;
                    var model = compilation.GetSemanticModel(tree);
                    var root = await tree.GetRootAsync();
                    var file = document.FilePath ?? tree.FilePath;

                    foreach (var node in root.DescendantNodes().Where(IsDeclaration))
                        IndexDeclaration(node, model, project.Name, file, symbols, sites, edges);
                }
            }

            _symbols.ReplaceAll([.. symbols.Values], sites, [.. edges]);
            Ready = true;
            _log.LogInformation("Symbol index built: {Symbols} symbols, {Edges} edges", symbols.Count, edges.Count);
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
        HashSet<SymbolStore.EdgeRow> edges)
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
                    OutlineBuilder.SummaryFromXml(symbol.GetDocumentationCommentXml()));
            }
            sites.Add((id, file ?? "-", span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, null));

            if (symbol is IMethodSymbol or IPropertySymbol)
                CollectCallEdges(node, symbol, model, id, edges);
        }
    }

    /// <summary>Records call edges from a member body via the semantic model (spec §18 call edges).</summary>
    private static void CollectCallEdges(
        SyntaxNode member, ISymbol from, SemanticModel model, string fromId, HashSet<SymbolStore.EdgeRow> edges)
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
            edges.Add(new SymbolStore.EdgeRow(fromId, SymbolKey.IdOf(target.OriginalDefinition), "call",
                DispatchKind(target), file, line));
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
