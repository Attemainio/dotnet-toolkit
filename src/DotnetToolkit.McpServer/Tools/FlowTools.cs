using System.ComponentModel;
using DotnetToolkit.McpServer.Identity;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Telemetry;
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
        + "isn't known yet so get_symbol has no target to query. Each item's displayString has its containing "
        + "type's prefix stripped. definedIn says where a member comes from, and is omitted where it would say "
        + "nothing: a receiverType header already states it, or the item is a local/parameter with no declaring "
        + "type at all; for a type-kind item it carries that type's NAMESPACE (or its outer type when nested). "
        + "Within one origin, symbols this solution declares come first, so a crowded cursor does not spend its "
        + "budget alphabetically in the A's of the referenced assemblies. When more is in scope than limit "
        + "allows, the budget is split across origins so applicable extension methods are never crowded out by "
        + "a receiver's own members, and totalItems/truncated report what was left out.")]

    public static async Task<string> GetScope(
        WorkspaceHost workspace,
        SolutionLocator locator,
        TelemetryRecorder telemetry,
        [Description("Root-relative path of the .cs file.")] string file,
        [Description("1-based line number.")] int line,
        [Description("1-based column (default 1).")] int column = 1,
        [Description("Optional variable/expression name; results become what is callable ON it, incl. extension methods.")] string? receiver = null,
        [Description("all | methods | properties | locals | types (default all).")] string filter = "all",
        [Description("Optional case-insensitive substring filter on the name.")] string? nameContains = null,
        [Description("Max results (default 40).")] int limit = 40,
        [Description(ToolTelemetry.TaskIdParam)] string? taskId = null)
    {
        var sessionId = Ids.AmbientSession;
        var attributedTask = Ids.TaskId(taskId);
        var toolCallId = Ids.ToolCall();
        var requested = $"{file}:{line}:{column}";

        string Fail(string kind, object payload, string? limitedBy = null) =>
            ToolTelemetry.Record(telemetry, toolCallId, sessionId, attributedTask, "get_scope",
                requested, Formats.Render(payload), limitedBy: limitedBy, errorKind: kind);

        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
            return Fail("workspace_loading", new { error = "workspace_loading" }, limitedBy: "index_only");

        var documentId = solution.GetDocumentIdsWithFilePath(locator.AbsPath(file)).FirstOrDefault();
        if (documentId is null)
            return Fail("file_not_in_solution", new { error = "file_not_in_solution", file });

        var document = solution.GetDocument(documentId)!;
        var text = await document.GetTextAsync();
        if (line < 1 || line > text.Lines.Count)
            return Fail("line_out_of_range", new { error = "line_out_of_range", line, lines = text.Lines.Count });

        var textLine = text.Lines[line - 1];
        var position = Math.Min(textLine.Start + Math.Max(0, column - 1), textLine.End);

        var model = await document.GetSemanticModelAsync();
        if (model is null)
            return Fail("no_semantic_model", new { error = "no_semantic_model" });

        ITypeSymbol? receiverType = null;
        IEnumerable<ISymbol> symbols;

        if (!string.IsNullOrWhiteSpace(receiver))
        {
            receiverType = ResolveReceiverType(model, textLine.ToString(), receiver, position);
            if (receiverType is null)
                return Fail("receiver_not_resolved", new { error = "receiver_not_resolved", receiver });

            symbols = model.LookupSymbols(position, receiverType, name: null, includeReducedExtensionMethods: true);
        }
        else
        {
            symbols = model.LookupSymbols(position);
        }

        var unqualifiedMemberFormat = SymbolDisplayFormat.MinimallyQualifiedFormat
            .WithMemberOptions(SymbolDisplayFormat.MinimallyQualifiedFormat.MemberOptions & ~SymbolDisplayMemberOptions.IncludeContainingType);
        var receiverTypeName = receiverType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        var ranked = symbols
            .Where(s => !s.IsImplicitlyDeclared)
            // A local or parameter is only in scope in the tree that declares it. Roslyn hands back the
            // synthesized top-level-statements entry point's locals -- Program.cs's `builder` and `app` --
            // from LookupSymbols at EVERY position in the compilation, so a cursor in an unrelated file was
            // being told two locals were callable that are not even in the same syntax tree.
            .Where(s => s.Kind is not (SymbolKind.Local or SymbolKind.Parameter or SymbolKind.RangeVariable)
                        || s.Locations.Any(l => l.SourceTree == model.SyntaxTree))
            .Where(s => MatchesFilter(s, filter))
            .Where(s => nameContains is null || s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(s => s.ToDisplayString())
            .Select(s => (Symbol: s, Origin: OriginOf(s, receiverType)))
            .OrderBy(t => OriginRank(t.Origin))
            .ThenBy(t => SourceRank(t.Symbol))
            .ThenBy(t => t.Symbol.Name, StringComparer.Ordinal)
            .ToList();

        var items = TakeAcrossOrigins(ranked, Math.Clamp(limit, 1, 200))
            .Select(t =>
            {
                var s = t.Symbol;
                var origin = t.Origin;
                var definedIn = DefinedIn(s, receiverTypeName);
                // Qualification is dropped from the FORMAT itself, not stripped from the rendered text
                // afterward: for a reduced extension method Roslyn renders the RECEIVER's type as that
                // qualification, which is exactly what the receiverType header already states once for the
                // whole response -- restating it per row (sometimes several times within one generic
                // signature) was pure repetition.
                var display = s.ToDisplayString(unqualifiedMemberFormat);
                return new
                {
                    displayString = display,
                    kind = SymbolKey.KindOf(s),
                    // "member" is derivable once a receiverType header exists: it is the only origin left
                    // once local/parameter/extension/type/inherited are ruled out, i.e. definedIn == receiverType.
                    origin = receiverType is not null && origin == "member" ? null : origin,
                    definedIn,
                };
            })
            .ToList();

        var json = Formats.Render(new
        {
            position = new { file, line },
            receiverType = receiverTypeName,
            totalItems = ranked.Count > items.Count ? (int?)ranked.Count : null,
            truncated = ranked.Count > items.Count ? (bool?)true : null,
            items,
        });


        return ToolTelemetry.Record(telemetry, toolCallId, sessionId, attributedTask, "get_scope",
            requested, json, returnedSymbols: items.Count);
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
        TelemetryRecorder telemetry,
        [Description("Origin symbol: fully-qualified name, unique suffix, or sym_... id.")] string from,
        [Description("Destination symbol: fully-qualified name, unique suffix, or sym_... id.")] string to,
        [Description("Maximum path length to search (default 8).")] int maxDepth = 8,
        [Description(ToolTelemetry.TaskIdParam)] string? taskId = null)
    {
        var sessionId = Ids.AmbientSession;
        var attributedTask = Ids.TaskId(taskId);
        var toolCallId = Ids.ToolCall();
        var requested = $"{from} -> {to}";

        string Fail(string kind, object payload, string? limitedBy = null) =>
            ToolTelemetry.Record(telemetry, toolCallId, sessionId, attributedTask, "get_call_slice",
                requested, Formats.Render(payload), limitedBy: limitedBy, errorKind: kind);

        if (!indexBuilder.Ready)
        {
            return Fail("index_building",
                new { error = "index_building", message = "The edge cache is still being built." },
                limitedBy: "index_only");
        }

        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
            return Fail("workspace_loading", new { error = "workspace_loading" }, limitedBy: "index_only");

        var fromId = await ResolveToIdAsync(solution, symbolStore, from);
        var toId = await ResolveToIdAsync(solution, symbolStore, to);
        if (fromId is null || toId is null)
        {
            return Fail("symbol_not_found", new
            {
                error = "symbol_not_found",
                message = fromId is null ? $"cannot resolve '{from}'" : $"cannot resolve '{to}'",
            });
        }

        var result = slice.Find(fromId, toId, Math.Clamp(maxDepth, 1, 20));

        if (!result.Found)
        {
            var miss = Formats.Render(new
            {
                found = false,
                nodesExplored = result.NodesExplored,
                forwardFrontier = result.ForwardFrontier.Select(id => symbolStore.DisplayFor(id) ?? id),
                backwardFrontier = result.BackwardFrontier.Select(id => symbolStore.DisplayFor(id) ?? id),
            });
            return ToolTelemetry.Record(telemetry, toolCallId, sessionId, attributedTask, "get_call_slice",
                requested, miss, resolution: "not_found");
        }

        var json = Formats.Render(new
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

        return ToolTelemetry.Record(telemetry, toolCallId, sessionId, attributedTask, "get_call_slice",
            requested, json, symbolId: toId, resolution: "found", returnedSymbols: result.Path.Count);
    }

[McpServerTool(Name = "get_call_hierarchy")]
    [Description("An open-ended multi-level call tree from one symbol — 'who eventually calls this, up to the "
        + "entry points' (direction: callers, Visual Studio's View Call Hierarchy) or 'what does this eventually "
        + "call' (direction: callees). Different from get_call_slice: that tool needs both a known from AND a "
        + "known to and returns one shortest path; this tool needs only a root and returns every branch up to "
        + "maxDepth, plus a blastRadius summary (unique nodes reached, per depth) answering 'if I change this, "
        + "how much does it ripple' without paying for the full tree — set includeTree:false for just that "
        + "summary plus the root. blastRadius counts every symbol REACHED, including the children a per-node "
        + "cap left unexpanded, and reports that cap as truncated/omittedChildren in BOTH shapes. A capped "
        + "node's own callers are never visited though, so a lower maxChildrenPerNode still yields a smaller "
        + "total at maxDepth>1 — the cap limits discovery, not just rendering. Every node always carries "
        + "symbolId (the join key back to get_symbol) and displayString — the containing type and member name "
        + "with the parameter list dropped (overloads still disambiguate via symbolId); add kind, file, line, "
        + "or the full signature (signature) via fields. A symbol reached through two different branches (a "
        + "diamond) legitimately appears twice in the tree but counts once in blastRadius; true recursion (a "
        + "symbol reappearing on its own path) stops as a leaf marked recursive:true rather than looping. "
        + "Internally capped at a few thousand total nodes as a safety net against pathological fan-out — use a "
        + "lower maxDepth or maxChildrenPerNode for a predictably sized answer on a well-connected graph.")]
    public static async Task<string> GetCallHierarchy(
        WorkspaceHost workspace,
        SymbolStore symbolStore,
        ProjectIndex index,
        SymbolIndexBuilder indexBuilder,
        TelemetryRecorder telemetry,
        [Description("Root symbol: fully-qualified name, unique suffix, or sym_... id.")] string symbol,
        [Description("callers | callees (default callers). callers walks upward toward entry points; callees walks downward into what this symbol invokes. An unrecognized value falls back to callers rather than erroring.")] string direction = "callers",
        [Description("Maximum tree depth (default 3, clamped 1-8 — deeper trees grow exponentially on a well-connected graph).")] int maxDepth = 3,
        [Description("Maximum children expanded per node before truncating (default 25, clamped 1-200). A node past the cap keeps its own entry but stops expanding, marked truncated:true with omittedChildren.")] int maxChildrenPerNode = 25,
        [Description("Emit the full tree (default true). Set false to return only blastRadius — the cheapest possible answer to 'how much does changing this ripple'.")] bool includeTree = true,
        [Description("Comma list of extra fields to add to every node beyond the always-present symbolId/displayString: kind, file, line, signature (the full parameter-list displayString instead of the default bare name). Omit for just symbolId/displayString.")] string? fields = null,
        [Description(ToolTelemetry.TaskIdParam)] string? taskId = null)
    {
        var sessionId = Ids.AmbientSession;
        var attributedTask = Ids.TaskId(taskId);
        var toolCallId = Ids.ToolCall();

        string Fail(string kind, object payload, string? limitedBy = null) =>
            ToolTelemetry.Record(telemetry, toolCallId, sessionId, attributedTask, "get_call_hierarchy",
                symbol, Formats.Render(payload), limitedBy: limitedBy, errorKind: kind, direction: direction);

        if (!indexBuilder.Ready)
        {
            return Fail("index_building",
                new { error = "index_building", message = "The edge cache is still being built." },
                limitedBy: "index_only");
        }

        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
            return Fail("workspace_loading", new { error = "workspace_loading" }, limitedBy: "index_only");

        var rootId = await ResolveToIdAsync(solution, symbolStore, symbol);
        if (rootId is null)
            return Fail("symbol_not_found", new { error = "symbol_not_found", message = $"cannot resolve '{symbol}'" });

        var callers = direction.Trim().ToLowerInvariant() != "callees";
        maxDepth = Math.Clamp(maxDepth, 1, 8);
        maxChildrenPerNode = Math.Clamp(maxChildrenPerNode, 1, 200);

        var result = new CallHierarchy(symbolStore).Build(rootId, callers, maxDepth, maxChildrenPerNode);

        var wantKind = false;
        var wantFile = false;
        var wantLine = false;
        var wantSignature = false;
        if (!string.IsNullOrWhiteSpace(fields))
        {
            foreach (var f in fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (f.ToLowerInvariant())
                {
                    case "kind": wantKind = true; break;
                    case "file": wantFile = true; break;
                    case "line": wantLine = true; break;
                    case "signature": wantSignature = true; break;
                }
            }
        }

        var rows = symbolStore.RowsFor(CollectIds(result.Root));

        // Default displayString is the containing type and member name (parameter list dropped) — the full
        // signature with types and default values made an 18-node tree 23x the size of its own
        // blastRadius-only summary for no reader benefit, and the namespace in front of it was another
        // third of the tree, repeated once per sibling. symbolId still disambiguates overloads, and
        // fields:"signature" restores the full form. get_references' default rows are the same shape.
        string DisplayFor(string symbolId, (string? FqName, string? Kind, string? DisplayString) row) =>
            wantSignature
                ? row.DisplayString ?? symbolStore.DisplayFor(symbolId) ?? symbolId
                : row.FqName is { } fq ? SymbolResolver.MemberWithContainingType(SymbolResolver.NameWithoutParameters(fq)) : row.DisplayString ?? symbolStore.DisplayFor(symbolId) ?? symbolId;

        IReadOnlyDictionary<string, ProjectIndex.Site> sites = new Dictionary<string, ProjectIndex.Site>();
        if (wantFile || wantLine)
        {
            await index.EnsureFreshAsync();
            var names = rows.Values.Where(r => r.FqName is not null).Select(r => r.FqName!).ToHashSet(StringComparer.Ordinal);
            sites = index.Locate(names);
        }

        object Project(CallHierarchy.Node node)
        {
            rows.TryGetValue(node.SymbolId, out var row);
            var display = DisplayFor(node.SymbolId, row);
            var site = (wantFile || wantLine) && row.FqName is not null
                ? sites.GetValueOrDefault(row.FqName)
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

        rows.TryGetValue(rootId, out var rootRow);
        var degradedBy = workspace.IsDegraded ? "degraded" : null;
        var json = Formats.Render(new
        {
            // With a tree, its head node IS the root and already carries both of these fields. Emitting
            // them again under root made a one-caller maxDepth:1 answer cost 39% MORE than get_references
            // while saying less. Without a tree, nothing else names what the answer is about.
            root = includeTree ? null : new
            {
                symbolId = rootId,
                displayString = DisplayFor(rootId, rootRow),
            },
            direction = callers ? "callers" : "callees",
            tree = includeTree ? Project(result.Root) : null,
            blastRadius = new
            {
                totalUniqueNodes = result.TotalUniqueNodes,
                perDepth = result.PerDepth,
                depthCapped = result.DepthCapped,
                // Present in the summary-only shape too, which is the whole point: the caller who opted out
                // of the tree opted out of the tree's truncated/omittedChildren markers with it.
                truncated = result.OmittedChildren > 0 ? (bool?)true : null,
                omittedChildren = result.OmittedChildren > 0 ? (int?)result.OmittedChildren : null,
            },
            limitedBy = degradedBy,
        });

        return ToolTelemetry.Record(telemetry, toolCallId, sessionId, attributedTask, "get_call_hierarchy",
            symbol, json, symbolId: rootId, returnedSymbols: result.TotalUniqueNodes,
            limitedBy: degradedBy, direction: callers ? "callers" : "callees");
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
        TelemetryRecorder telemetry,
        [Description("Type symbol: fully-qualified name, unique suffix, or sym_... id.")] string symbol,
        [Description("Max derived types returned (default 40, clamped 1-200).")] int limit = 40,
        [Description(ToolTelemetry.TaskIdParam)] string? taskId = null)
    {
        var sessionId = Ids.AmbientSession;
        var attributedTask = Ids.TaskId(taskId);
        var toolCallId = Ids.ToolCall();

        string Fail(string kind, object payload, string? limitedBy = null) =>
            ToolTelemetry.Record(telemetry, toolCallId, sessionId, attributedTask, "get_type_hierarchy",
                symbol, Formats.Render(payload), limitedBy: limitedBy, errorKind: kind);

        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
            return Fail("workspace_loading", new { error = "workspace_loading" }, limitedBy: "index_only");

        var handle = symbol.StartsWith("sym_", StringComparison.Ordinal) ? symbolStore.FqNameFor(symbol) ?? symbol : symbol;
        var resolution = await SymbolResolver.ResolveAsync(solution, handle);
        if (resolution.Symbol is null)
        {
            return resolution.Candidates.Count == 0
                ? Fail("symbol_not_found", new { error = "symbol_not_found", symbol })
                : Fail("ambiguous_symbol", new
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
            return Fail("not_a_type", new { error = "not_a_type", message = "symbol is not a class, interface, struct, enum, delegate or record" });

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

        var degradedBy = workspace.IsDegraded ? "degraded" : null;
        var json = Formats.Render(new
        {
            symbolId = SymbolKey.IdOf(type),
            displayString = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            baseChain,
            interfaces,
            derived,
            limitedBy = degradedBy,
        });

        return ToolTelemetry.Record(telemetry, toolCallId, sessionId, attributedTask, "get_type_hierarchy",
            symbol, json, symbolId: SymbolKey.IdOf(type),
            returnedSymbols: baseChain.Count + interfaces.Count, limitedBy: degradedBy);
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

    // get_scope's default filter ("all") mixes locals/parameters/members with the general type universe
    // reachable from the cursor (every visible BCL/NuGet type). Plain alphabetical order let that
    // universe dominate the page — a dotnet-toolkit self-evaluation on an external repo found a
    // default-filter call returning 40 alphabetically-sorted BCL exception types and zero of the
    // position-relevant locals/parameters/members, because there are always vastly more visible types
    // than locals. Ranking origin first keeps position-relevant items ahead of the type universe
    // regardless of alphabetical luck.
    private static int OriginRank(string origin) => origin switch
    {
        "local" => 0,
        "parameter" => 1,
        "member" => 2,
        "inherited" => 3,
        "extension" => 4,
        _ => 5,
    };

    /// <summary>
    /// 0 for a symbol this solution declares, 1 for one that came from metadata — the tiebreak that keeps
    /// the repo's own symbols ahead of the BCL's within one origin group.
    /// </summary>
    /// <remarks>
    /// Rank-then-alphabetical order alone spent the type share of the budget in the A's of the referenced
    /// assemblies: at a cursor with 919 symbols in scope, three of ten returned rows were
    /// AbandonedMutexException, AccessViolationException and AccessedThroughPropertyAttribute — none of
    /// which is what a caller standing in this repo's code is deciding between.
    /// </remarks>
    /// <param name="symbol">The in-scope symbol being ordered.</param>
    /// <returns>0 when any of its locations is in source, 1 otherwise.</returns>
    private static int SourceRank(ISymbol symbol) =>
        symbol.Locations.Any(location => location.IsInSource) ? 0 : 1;

    /// <summary>
    /// Where an in-scope symbol comes from, or null when nothing informative is left to say: a
    /// receiverType header already states it, or a local/parameter has no declaring type at all.
    /// </summary>
    /// <remarks>
    /// A type's own home is its namespace (or its outer type, when nested) rather than a containing type,
    /// which is why every type-kind item used to carry a constant empty field here.
    /// </remarks>
    /// <param name="symbol">The symbol being described.</param>
    /// <param name="receiverTypeName">The receiverType the response header already states, if any.</param>
    /// <returns>The defining type or namespace, or null when it would carry no information.</returns>
    private static string? DefinedIn(ISymbol symbol, string? receiverTypeName)
    {
        if (symbol is ILocalSymbol or IParameterSymbol)
            return null;

        if (symbol is INamedTypeSymbol type)
        {
            if (type.ContainingType is { } outer)
                return outer.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            return type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null;
        }

        var declaring = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        return declaring == receiverTypeName ? null : declaring;
    }

    /// <summary>
    /// Takes up to <paramref name="limit"/> items, round-robin across origin groups in rank order, so one
    /// crowded origin cannot spend the whole budget.
    /// </summary>
    /// <remarks>
    /// Rank-then-alphabetical order alone buried this tool's own value proposition: on a
    /// <c>List&lt;Trade&lt;T&gt;&gt;</c> receiver, members from Add to ConvertAll (three BinarySearch
    /// overloads among them) filled a limit of 10, and not one applicable extension method -- the thing
    /// grep genuinely cannot answer -- appeared at that limit or at the default one.
    /// </remarks>
    /// <param name="ranked">Every in-scope symbol, already ordered by origin rank, then source-first, then name.</param>
    /// <param name="limit">How many items the response may carry.</param>
    /// <returns>The chosen items, back in that same order.</returns>
    private static List<(ISymbol Symbol, string Origin)> TakeAcrossOrigins(
        List<(ISymbol Symbol, string Origin)> ranked, int limit)
    {
        if (ranked.Count <= limit)
            return ranked;

        var groups = ranked
            .GroupBy(t => t.Origin, StringComparer.Ordinal)
            .OrderBy(g => OriginRank(g.Key))
            .Select(g => g.ToList())
            .ToList();

        var taken = new List<(ISymbol Symbol, string Origin)>(limit);
        for (var round = 0; taken.Count < limit; round++)
        {
            var addedThisRound = false;
            foreach (var group in groups)
            {
                if (round >= group.Count)
                    continue;

                taken.Add(group[round]);
                addedThisRound = true;
                if (taken.Count == limit)
                    break;
            }

            if (!addedThisRound)
                break;
        }

        return taken
            .OrderBy(t => OriginRank(t.Origin))
            .ThenBy(t => SourceRank(t.Symbol))
            .ThenBy(t => t.Symbol.Name, StringComparer.Ordinal)
            .ToList();
    }
}
