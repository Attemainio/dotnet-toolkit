using System.ComponentModel;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
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
        + "Grep cannot answer this: extension methods share no text with the call site. DIFFERENT from get_symbol's "
        + "'members' (a type's static declared list, no position involved) — call this when standing at a cursor "
        + "deciding what to call, before writing a helper that may already exist, or when the receiver's type "
        + "isn't known yet so get_symbol has no target to query.")]
    public static async Task<string> GetScope(
        WorkspaceHost workspace,
        SolutionLocator locator,
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
            return Formats.Render(new { error = "workspace_loading" });

        var documentId = solution.GetDocumentIdsWithFilePath(locator.AbsPath(file)).FirstOrDefault();
        if (documentId is null)
            return Formats.Render(new { error = "file_not_in_solution", file });

        var document = solution.GetDocument(documentId)!;
        var text = await document.GetTextAsync();
        if (line < 1 || line > text.Lines.Count)
            return Formats.Render(new { error = "line_out_of_range", line, lines = text.Lines.Count });

        var textLine = text.Lines[line - 1];
        var position = Math.Min(textLine.Start + Math.Max(0, column - 1), textLine.End);

        var model = await document.GetSemanticModelAsync();
        if (model is null)
            return Formats.Render(new { error = "no_semantic_model" });

        ITypeSymbol? receiverType = null;
        IEnumerable<ISymbol> symbols;

        if (!string.IsNullOrWhiteSpace(receiver))
        {
            receiverType = ResolveReceiverType(model, textLine.ToString(), receiver, position);
            if (receiverType is null)
                return Formats.Render(new { error = "receiver_not_resolved", receiver });

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

        return Formats.Render(new
        {
            position = new { file, line },
            receiverType = receiverType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            items,
        });
    }

    [McpServerTool(Name = "get_call_slice")]
    [Description("The shortest call path between two symbols — how a value or control flow reaches its "
        + "destination. Use for 'how does X reach Y' instead of walking the graph with repeated get_references "
        + "calls. A miss still reports the nearest reachable frontier from each end.")]
    public static async Task<string> GetCallSlice(
        WorkspaceHost workspace,
        SymbolStore symbolStore,
        CallSlice slice,
        SymbolIndexBuilder indexBuilder,
        [Description("Origin symbol: fully-qualified name, unique suffix, or sym_... id.")] string from,
        [Description("Destination symbol: fully-qualified name, unique suffix, or sym_... id.")] string to,
        [Description("Maximum path length to search (default 8).")] int maxDepth = 8)
    {
        if (!indexBuilder.Ready)
            return Formats.Render(new { error = "index_building", message = "The edge cache is still being built." });

        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
            return Formats.Render(new { error = "workspace_loading" });

        var fromId = await ResolveToIdAsync(solution, symbolStore, from);
        var toId = await ResolveToIdAsync(solution, symbolStore, to);
        if (fromId is null || toId is null)
            return Formats.Render(new
            {
                error = "symbol_not_found",
                message = fromId is null ? $"cannot resolve '{from}'" : $"cannot resolve '{to}'",
            });

        var result = slice.Find(fromId, toId, Math.Clamp(maxDepth, 1, 20));

        if (!result.Found)
        {
            return Formats.Render(new
            {
                found = false,
                nodesExplored = result.NodesExplored,
                forwardFrontier = result.ForwardFrontier.Select(id => symbolStore.DisplayFor(id) ?? id),
                backwardFrontier = result.BackwardFrontier.Select(id => symbolStore.DisplayFor(id) ?? id),
            });
        }

        return Formats.Render(new
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

    [McpServerTool(Name = "get_call_hierarchy")]
    [Description("An open-ended multi-level call tree from one symbol — 'who eventually calls this, up to the "
        + "entry points' (direction: callers, Visual Studio's View Call Hierarchy) or 'what does this eventually "
        + "call' (direction: callees). Different from get_call_slice: that tool needs both a known from AND a "
        + "known to and returns one shortest path; this tool needs only a root and returns every branch up to "
        + "maxDepth, plus a blastRadius summary (unique nodes reached, per depth) answering 'if I change this, "
        + "how much does it ripple' without paying for the full tree — set includeTree:false for just that "
        + "summary. Every node always carries symbolId (the join key back to get_symbol) and displayString; add "
        + "kind, file, line via fields. A symbol reached through two different branches (a diamond) legitimately "
        + "appears twice in the tree but counts once in blastRadius; true recursion (a symbol reappearing on its "
        + "own path) stops as a leaf marked recursive:true rather than looping. Internally capped at a few "
        + "thousand total nodes as a safety net against pathological fan-out — use a lower maxDepth or "
        + "maxChildrenPerNode for a predictably sized answer on a well-connected graph.")]
    public static async Task<string> GetCallHierarchy(
        WorkspaceHost workspace,
        SymbolStore symbolStore,
        ProjectIndex index,
        SymbolIndexBuilder indexBuilder,
        [Description("Root symbol: fully-qualified name, unique suffix, or sym_... id.")] string symbol,
        [Description("callers | callees (default callers). callers walks upward toward entry points; callees walks downward into what this symbol invokes.")] string direction = "callers",
        [Description("Maximum tree depth (default 3, clamped 1-8 — deeper trees grow exponentially on a well-connected graph).")] int maxDepth = 3,
        [Description("Maximum children expanded per node before truncating (default 25, clamped 1-200). A node past the cap keeps its own entry but stops expanding, marked truncated:true with omittedChildren.")] int maxChildrenPerNode = 25,
        [Description("Emit the full tree (default true). Set false to return only blastRadius — the cheapest possible answer to 'how much does changing this ripple'.")] bool includeTree = true,
        [Description("Comma list of extra fields to add to every node beyond the always-present symbolId/displayString: kind, file, line. Omit for just symbolId/displayString.")] string? fields = null)
    {
        if (!indexBuilder.Ready)
            return Formats.Render(new { error = "index_building", message = "The edge cache is still being built." });

        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
            return Formats.Render(new { error = "workspace_loading" });

        var rootId = await ResolveToIdAsync(solution, symbolStore, symbol);
        if (rootId is null)
            return Formats.Render(new { error = "symbol_not_found", message = $"cannot resolve '{symbol}'" });

        var callers = direction.Trim().ToLowerInvariant() != "callees";
        maxDepth = Math.Clamp(maxDepth, 1, 8);
        maxChildrenPerNode = Math.Clamp(maxChildrenPerNode, 1, 200);

        var result = new CallHierarchy(symbolStore).Build(rootId, callers, maxDepth, maxChildrenPerNode);

        var wantKind = false;
        var wantFile = false;
        var wantLine = false;
        if (!string.IsNullOrWhiteSpace(fields))
        {
            foreach (var f in fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (f.ToLowerInvariant())
                {
                    case "kind": wantKind = true; break;
                    case "file": wantFile = true; break;
                    case "line": wantLine = true; break;
                }
            }
        }

        var rows = symbolStore.RowsFor(CollectIds(result.Root));

        IReadOnlyDictionary<string, ProjectIndex.Site> sites = new Dictionary<string, ProjectIndex.Site>();
        if (wantFile || wantLine)
        {
            await index.EnsureFreshAsync();
            var names = rows.Values.Where(r => r.FqName is not null).Select(r => SymbolResolver.NameWithoutParameters(r.FqName!)).ToHashSet(StringComparer.Ordinal);
            sites = index.Locate(names);
        }

        object Project(CallHierarchy.Node node)
        {
            rows.TryGetValue(node.SymbolId, out var row);
            var display = row.DisplayString ?? symbolStore.DisplayFor(node.SymbolId) ?? node.SymbolId;
            var site = (wantFile || wantLine) && row.FqName is not null
                ? sites.GetValueOrDefault(SymbolResolver.NameWithoutParameters(row.FqName))
                : null;

            return new
            {
                symbolId = node.SymbolId,
                displayString = display,
                kind = wantKind ? row.Kind : null,
                file = wantFile ? site?.File : null,
                line = wantLine ? (int?)site?.Line : null,
                recursive = node.Recursive ? true : (bool?)null,
                truncated = node.Truncated ? true : (bool?)null,
                omittedChildren = node.OmittedChildren,
                children = node.Children?.Select(Project).ToList(),
            };
        }

        return Formats.Render(new
        {
            root = new
            {
                symbolId = rootId,
                displayString = rows.TryGetValue(rootId, out var rootRow) ? rootRow.DisplayString : symbolStore.DisplayFor(rootId) ?? rootId,
            },
            direction = callers ? "callers" : "callees",
            tree = includeTree ? Project(result.Root) : null,
            blastRadius = new
            {
                totalUniqueNodes = result.TotalUniqueNodes,
                perDepth = result.PerDepth,
                depthCapped = result.DepthCapped,
            },
            limitedBy = workspace.IsDegraded ? "degraded" : null,
        });
    }

    [McpServerTool(Name = "get_type_hierarchy")]
    [Description("A type's full base-type chain (up to object), transitive interfaces (tagged direct vs "
        + "inherited), and derived/implementing types — one hop further than get_symbol/get_references give "
        + "today. derived is a flat ranked list, not a nested tree — get_symbol on any result reveals its own "
        + "immediate base if you need one more level — and is omitted entirely when symbol is not a "
        + "class/interface (structs/enums/delegates cannot be derived from).")]
    public static async Task<string> GetTypeHierarchy(
        WorkspaceHost workspace,
        SymbolStore symbolStore,
        [Description("Type symbol: fully-qualified name, unique suffix, or sym_... id.")] string symbol,
        [Description("Max derived types returned (default 40, clamped 1-200).")] int limit = 40)
    {
        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
            return Formats.Render(new { error = "workspace_loading" });

        var handle = symbol.StartsWith("sym_", StringComparison.Ordinal) ? symbolStore.FqNameFor(symbol) ?? symbol : symbol;
        var resolution = await SymbolResolver.ResolveAsync(solution, handle);
        if (resolution.Symbol is null)
        {
            return resolution.Candidates.Count == 0
                ? Formats.Render(new { error = "symbol_not_found", symbol })
                : Formats.Render(new
                {
                    error = "ambiguous_symbol",
                    candidates = resolution.Candidates.Take(10).Select(c => new
                    {
                        symbolId = SymbolKey.IdOf(c),
                        displayString = c.ToDisplayString(),
                    }),
                });
        }

        if (resolution.Symbol is not INamedTypeSymbol type)
            return Formats.Render(new { error = "not_a_type", message = "symbol is not a class, interface, struct, enum, delegate or record" });

        limit = Math.Clamp(limit, 1, 200);

        var baseChain = new List<object>();
        for (var b = type.BaseType; b is not null; b = b.BaseType)
            baseChain.Add(HierarchyPointer(b));

        var interfaces = type.AllInterfaces
            .Select(i => new
            {
                symbolId = SymbolKey.IdOf(i),
                displayString = i.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                origin = type.Interfaces.Contains(i, SymbolEqualityComparer.Default) ? "direct" : "inherited",
            })
            .ToList();

        object? derived = null;
        if (type.TypeKind is TypeKind.Class or TypeKind.Interface)
        {
            IEnumerable<ISymbol> found = type.TypeKind == TypeKind.Interface
                ? await SymbolFinder.FindImplementationsAsync(type, solution)
                : await SymbolFinder.FindDerivedClassesAsync(type, solution);
            var ordered = found.OfType<INamedTypeSymbol>()
                .Select(s => new
                {
                    symbolId = SymbolKey.IdOf(s),
                    displayString = s.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    kind = SymbolKey.KindOf(s),
                })
                .OrderBy(x => x.displayString, StringComparer.Ordinal)
                .ToList();
            derived = new
            {
                items = ordered.Take(limit),
                totalItems = ordered.Count,
                truncated = ordered.Count > limit ? true : (bool?)null,
            };
        }

        return Formats.Render(new
        {
            symbolId = SymbolKey.IdOf(type),
            displayString = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            baseChain,
            interfaces,
            derived,
            limitedBy = workspace.IsDegraded ? "degraded" : null,
        });
    }

    private static List<string> CollectIds(CallHierarchy.Node node)
    {
        var ids = new List<string> { node.SymbolId };
        if (node.Children is not null)
            foreach (var child in node.Children)
                ids.AddRange(CollectIds(child));
        return ids;
    }

    private static object HierarchyPointer(ISymbol sym) => new
    {
        symbolId = SymbolKey.IdOf(sym),
        displayString = sym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
    };

    // ---- helpers -------------------------------------------------------------

    private static async Task<string?> ResolveToIdAsync(Solution solution, SymbolStore symbolStore, string spec)
    {
        if (spec.StartsWith("sym_", StringComparison.Ordinal))
            return symbolStore.FqNameFor(spec) is null ? null : spec;
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
