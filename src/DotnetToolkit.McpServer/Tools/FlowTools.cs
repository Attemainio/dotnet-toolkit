using System.ComponentModel;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace DotnetToolkit.McpServer.Tools;

/// <summary>
/// Flow surface (spec §11, §12): what is callable at a position, and how one symbol reaches another.
/// These answer the two questions a text search structurally cannot — extension methods share no text
/// with the call site, and a call path crosses files and interfaces.
/// </summary>
[McpServerToolType]
public static class FlowTools
{
    [McpServerTool(Name = "get_scope")]
    [Description("What is callable HERE — members, inherited members, locals, parameters and applicable "
        + "extension methods at a file/line/column, filtered to what is actually accessible from that position. "
        + "Grep cannot answer this: extension methods share no text with the call site. Use before writing a "
        + "helper that may already exist. Requires sessionId and taskId.")]
    public static async Task<string> GetScope(
        WorkspaceHost workspace,
        SolutionLocator locator,
        [Description("Agent conversation id (ses_...).")] string sessionId,
        [Description("User task id (tsk_...).")] string taskId,
        [Description("Root-relative path of the .cs file.")] string file,
        [Description("1-based line number.")] int line,
        [Description("1-based column (default 1).")] int column = 1,
        [Description("Optional variable/expression name; results become what is callable ON it, incl. extension methods.")] string? receiver = null,
        [Description("all | methods | properties | locals | types (default all).")] string filter = "all",
        [Description("Optional case-insensitive substring filter on the name.")] string? nameContains = null,
        [Description("Max results (default 40).")] int limit = 40)
    {
        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
            return Formats.ToJson(new { error = "workspace_loading" });

        var documentId = solution.GetDocumentIdsWithFilePath(locator.AbsPath(file)).FirstOrDefault();
        if (documentId is null)
            return Formats.ToJson(new { error = "file_not_in_solution", file });

        var document = solution.GetDocument(documentId)!;
        var text = await document.GetTextAsync();
        if (line < 1 || line > text.Lines.Count)
            return Formats.ToJson(new { error = "line_out_of_range", line, lines = text.Lines.Count });

        var textLine = text.Lines[line - 1];
        var position = Math.Min(textLine.Start + Math.Max(0, column - 1), textLine.End);

        var model = await document.GetSemanticModelAsync();
        if (model is null)
            return Formats.ToJson(new { error = "no_semantic_model" });

        ITypeSymbol? receiverType = null;
        IEnumerable<ISymbol> symbols;

        if (!string.IsNullOrWhiteSpace(receiver))
        {
            receiverType = ResolveReceiverType(model, textLine.ToString(), receiver, position);
            if (receiverType is null)
                return Formats.ToJson(new { error = "receiver_not_resolved", receiver });

            // includeReducedExtensionMethods honours the file's using directives, so only extension
            // methods actually in scope here are returned.
            symbols = model.LookupSymbols(position, receiverType, name: null, includeReducedExtensionMethods: true);
        }
        else
        {
            symbols = model.LookupSymbols(position);
        }

        var items = symbols
            .Where(s => !s.IsImplicitlyDeclared)
            .Where(s => MatchesFilter(s, filter))
            .Where(s => nameContains is null || s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(s => s.ToDisplayString())
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(s => new
            {
                displayString = s.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                kind = SymbolKey.KindOf(s),
                origin = OriginOf(s, receiverType),
                definedIn = s.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            })
            .ToList();

        return Formats.ToJson(new
        {
            position = new { file, line },
            receiverType = receiverType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            items,
        });
    }

    [McpServerTool(Name = "get_call_slice")]
    [Description("The shortest call path between two symbols — how a value or control flow reaches its "
        + "destination. Use for 'how does X reach Y' instead of walking the graph with repeated get_references "
        + "calls. A miss still reports the nearest reachable frontier from each end. Requires sessionId and taskId.")]
    public static async Task<string> GetCallSlice(
        WorkspaceHost workspace,
        SymbolStore symbolStore,
        CallSlice slice,
        SymbolIndexBuilder indexBuilder,
        [Description("Agent conversation id (ses_...).")] string sessionId,
        [Description("User task id (tsk_...).")] string taskId,
        [Description("Origin symbol: fully-qualified name, unique suffix, or sym_... id.")] string from,
        [Description("Destination symbol: fully-qualified name, unique suffix, or sym_... id.")] string to,
        [Description("Maximum path length to search (default 8).")] int maxDepth = 8)
    {
        if (!indexBuilder.Ready)
            return Formats.ToJson(new { error = "index_building", message = "The edge cache is still being built." });

        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
            return Formats.ToJson(new { error = "workspace_loading" });

        var fromId = await ResolveToIdAsync(solution, symbolStore, from);
        var toId = await ResolveToIdAsync(solution, symbolStore, to);
        if (fromId is null || toId is null)
            return Formats.ToJson(new
            {
                error = "symbol_not_found",
                message = fromId is null ? $"cannot resolve '{from}'" : $"cannot resolve '{to}'",
            });

        var result = slice.Find(fromId, toId, Math.Clamp(maxDepth, 1, 20));

        if (!result.Found)
        {
            // A miss names where each side ran out, so the next question is informed.
            return Formats.ToJson(new
            {
                found = false,
                nodesExplored = result.NodesExplored,
                forwardFrontier = result.ForwardFrontier.Select(id => symbolStore.DisplayFor(id) ?? id),
                backwardFrontier = result.BackwardFrontier.Select(id => symbolStore.DisplayFor(id) ?? id),
            });
        }

        return Formats.ToJson(new
        {
            found = true,
            path = result.Path.Select(id => new
            {
                symbolId = id,
                displayString = symbolStore.DisplayFor(id) ?? id,
            }),
            depth = result.Path.Count - 1,
            nodesExplored = result.NodesExplored,
        });
    }

    // ---- helpers -------------------------------------------------------------

    private static async Task<string?> ResolveToIdAsync(Solution solution, SymbolStore symbolStore, string spec)
    {
        if (spec.StartsWith("sym_", StringComparison.Ordinal))
            return symbolStore.FqNameFor(spec) is null ? spec : spec;
        var resolution = await SymbolResolver.ResolveAsync(solution, spec);
        return resolution.Symbol is null ? null : SymbolKey.IdOf(resolution.Symbol);
    }

    /// <summary>
    /// Finds the receiver's type by locating the identifier on the line and asking the semantic model.
    /// Position-based rather than name-based lookup, so a shadowed local resolves the way the compiler
    /// would see it.
    /// </summary>
    private static ITypeSymbol? ResolveReceiverType(SemanticModel model, string lineText, string receiver, int fallbackPosition)
    {
        var root = model.SyntaxTree.GetRoot();
        var node = root.FindToken(fallbackPosition).Parent;

        // Walk outward from the position looking for an identifier matching the receiver name.
        for (var current = node; current is not null; current = current.Parent)
        {
            foreach (var identifier in current.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                if (!string.Equals(identifier.Identifier.Text, receiver, StringComparison.Ordinal))
                    continue;
                var type = model.GetTypeInfo(identifier).Type;
                if (type is not null)
                    return type;
            }
            if (current is MemberDeclarationSyntax)
                break;
        }
        return null;
    }

    private static bool MatchesFilter(ISymbol symbol, string filter) => filter.Trim().ToLowerInvariant() switch
    {
        "methods" => symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.ReducedExtension },
        "properties" => symbol is IPropertySymbol,
        "locals" => symbol is ILocalSymbol or IParameterSymbol,
        "types" => symbol is INamedTypeSymbol,
        _ => symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol
            or ILocalSymbol or IParameterSymbol or INamedTypeSymbol,
    };

    private static string OriginOf(ISymbol symbol, ITypeSymbol? receiverType) => symbol switch
    {
        ILocalSymbol => "local",
        IParameterSymbol => "parameter",
        IMethodSymbol { MethodKind: MethodKind.ReducedExtension } => "extension",
        INamedTypeSymbol => "type",
        _ when receiverType is not null
               && !SymbolEqualityComparer.Default.Equals(symbol.ContainingType, receiverType) => "inherited",
        _ => "member",
    };
}
