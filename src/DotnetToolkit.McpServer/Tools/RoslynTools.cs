using System.ComponentModel;
using System.Text;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace DotnetToolkit.McpServer.Tools;

[McpServerToolType]
public static class RoslynTools
{
    private const int MaxLimit = 100;

    [McpServerTool(Name = "find_references")]
    [Description("All references to a symbol across the solution (semantic, not text search). Use this instead of Grep to find callers/usages. Symbol: fully-qualified name or unique suffix, optionally with a parameter list to pick an overload.")]
    public static async Task<string> FindReferences(
        WorkspaceHost workspace,
        ProjectIndex index,
        SolutionLocator locator,
        [Description("e.g. 'OrderService.PlaceOrder' or 'Contoso.OrderService.PlaceOrder(OrderRequest)'.")] string symbol,
        [Description("Max results (default 20, cap 100).")] int limit = 20,
        [Description("compact | json")] string? format = null)
    {
        await index.EnsureFreshAsync();
        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
            return NotLoaded(workspace);

        var (sym, error) = await ResolveAsync(solution, symbol);
        if (sym is null)
            return error!;

        limit = Math.Clamp(limit, 1, MaxLimit);
        var rows = new List<(string File, int Line, string Member, string Src)>();
        foreach (var referenced in await SymbolFinder.FindReferencesAsync(sym, solution))
        {
            foreach (var location in referenced.Locations)
            {
                var doc = location.Document;
                var text = await doc.GetTextAsync();
                var span = location.Location.SourceSpan;
                var line = text.Lines.GetLineFromPosition(span.Start);
                var root = await doc.GetSyntaxRootAsync();
                var member = root is null ? "-" : ContainingMember(root.FindNode(span));
                rows.Add((
                    locator.RelPath(doc.FilePath ?? doc.Name),
                    line.LineNumber + 1,
                    member,
                    CompactFormatter.Truncate(line.ToString().Trim(), 100)));
            }
        }

        var ordered = rows.OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line).ToList();
        if (Formats.Parse(format ?? locator.Config.DefaultFormat) == OutputFormat.Json)
            return Formats.ToJson(new
            {
                symbol = sym.ToDisplayString(),
                total = ordered.Count,
                references = ordered.Take(limit).Select(r => new { file = r.File, line = r.Line, member = r.Member, src = r.Src }),
            });

        var tableRows = ordered.Take(limit)
            .Select(r => new[] { r.File, r.Line.ToString(), r.Member, r.Src })
            .ToList();
        return CompactFormatter.Table("refs", ["file", "line", "member", "src"], tableRows, ordered.Count);
    }

    [McpServerTool(Name = "find_implementations")]
    [Description("Implementations of an interface (or interface member), classes derived from a class, or overrides of a virtual member.")]
    public static async Task<string> FindImplementations(
        WorkspaceHost workspace,
        ProjectIndex index,
        SolutionLocator locator,
        [Description("Fully-qualified name or unique suffix.")] string symbol,
        [Description("Max results (default 20, cap 100).")] int limit = 20,
        [Description("compact | json")] string? format = null)
    {
        await index.EnsureFreshAsync();
        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
            return NotLoaded(workspace);

        var (sym, error) = await ResolveAsync(solution, symbol);
        if (sym is null)
            return error!;

        limit = Math.Clamp(limit, 1, MaxLimit);
        IEnumerable<ISymbol> results = sym switch
        {
            INamedTypeSymbol { TypeKind: TypeKind.Interface } nt =>
                await SymbolFinder.FindImplementationsAsync(nt, solution),
            INamedTypeSymbol nt =>
                await SymbolFinder.FindDerivedClassesAsync(nt, solution),
            _ when sym.ContainingType?.TypeKind == TypeKind.Interface =>
                await SymbolFinder.FindImplementationsAsync(sym, solution),
            _ => await SymbolFinder.FindOverridesAsync(sym, solution),
        };

        var all = results.Select(s =>
        {
            var (file, line) = Loc(s, locator);
            return new[] { KindCode(s), s.ToDisplayString(), file, line.ToString() };
        }).OrderBy(r => r[1], StringComparer.Ordinal).ToList();

        if (Formats.Parse(format ?? locator.Config.DefaultFormat) == OutputFormat.Json)
            return Formats.ToJson(new
            {
                symbol = sym.ToDisplayString(),
                total = all.Count,
                implementations = all.Take(limit).Select(r => new { kind = r[0], symbol = r[1], file = r[2], line = r[3] }),
            });

        return CompactFormatter.Table("impls", ["kind", "symbol", "file", "line"], all.Take(limit).ToList(), all.Count);
    }

    [McpServerTool(Name = "get_symbol")]
    [Description("Details of one symbol: signature, XML doc summary, base types/interfaces, member list, and source location. Use this instead of reading the file to learn a type's API.")]
    public static async Task<string> GetSymbol(
        WorkspaceHost workspace,
        ProjectIndex index,
        SolutionLocator locator,
        [Description("Fully-qualified name or unique suffix.")] string symbol,
        [Description("List type members (default true).")] bool include_members = true,
        [Description("Include doc summaries (default true).")] bool include_docs = true,
        [Description("compact | json")] string? format = null)
    {
        await index.EnsureFreshAsync();
        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
            return NotLoaded(workspace);

        var (sym, error) = await ResolveAsync(solution, symbol);
        if (sym is null)
            return error!;

        var (file, line) = Loc(sym, locator);
        var doc = include_docs ? OutlineBuilder.SummaryFromXml(sym.GetDocumentationCommentXml()) : null;

        var sb = new StringBuilder();
        sb.Append(KindCode(sym)).Append(' ').Append(sym.ToDisplayString());
        if (sym is INamedTypeSymbol type)
        {
            var bases = new List<string>();
            if (type.BaseType is { SpecialType: SpecialType.None } bt)
                bases.Add(bt.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            bases.AddRange(type.Interfaces.Select(i => i.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            if (bases.Count > 0)
                sb.Append(" : ").Append(string.Join(", ", bases));
        }
        sb.Append("  ").Append(file).Append(':').Append(line).Append('\n');
        if (doc is not null)
            sb.Append("// ").Append(CompactFormatter.Truncate(doc, 240)).Append('\n');

        if (sym is INamedTypeSymbol nt && include_members)
        {
            foreach (var member in nt.GetMembers().Where(IsListableMember))
            {
                sb.Append("  ").Append(KindCode(member)).Append(' ')
                  .Append(member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                if (include_docs && OutlineBuilder.SummaryFromXml(member.GetDocumentationCommentXml()) is { } mdoc)
                    sb.Append("  // ").Append(CompactFormatter.FirstSentence(mdoc));
                sb.Append('\n');
            }
        }
        else if (sym is IMethodSymbol method)
        {
            var overloads = method.ContainingType.GetMembers(method.Name).OfType<IMethodSymbol>().Count();
            if (overloads > 1)
                sb.Append("  (").Append(overloads).Append(" overloads in ")
                  .Append(method.ContainingType.Name).Append(")\n");
        }

        return sb.ToString().TrimEnd('\n');
    }

    [McpServerTool(Name = "diagnostics")]
    [Description("Compiler diagnostics (errors/warnings) for a file, project, or the whole solution — use this instead of running dotnet build and reading its output.")]
    public static async Task<string> Diagnostics(
        WorkspaceHost workspace,
        ProjectIndex index,
        SolutionLocator locator,
        [Description("file | project | solution (default solution).")] string scope = "solution",
        [Description("Root-relative file path (scope=file) or project name (scope=project).")] string? path = null,
        [Description("Minimum severity: info | warning | error (default warning).")] string min_severity = "warning",
        [Description("Max results (default 50, cap 100).")] int limit = 50,
        [Description("compact | json")] string? format = null)
    {
        await index.EnsureFreshAsync();
        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
            return NotLoaded(workspace);

        var minSev = min_severity.Trim().ToLowerInvariant() switch
        {
            "error" => DiagnosticSeverity.Error,
            "info" => DiagnosticSeverity.Info,
            _ => DiagnosticSeverity.Warning,
        };

        List<Project> projects;
        string? fileFilter = null;
        switch (scope.Trim().ToLowerInvariant())
        {
            case "file":
            {
                if (path is null)
                    return "scope=file requires path";
                var abs = locator.AbsPath(path);
                var docIds = solution.GetDocumentIdsWithFilePath(abs);
                if (docIds.IsEmpty)
                    return $"file is not part of the loaded solution: {path}";
                projects = docIds.Select(d => solution.GetProject(d.ProjectId)!).DistinctBy(p => p.Id).ToList();
                fileFilter = abs;
                break;
            }
            case "project":
            {
                if (path is null)
                    return "scope=project requires path (project name or .csproj path)";
                projects = solution.Projects
                    .Where(p => p.Name.Equals(path, StringComparison.OrdinalIgnoreCase)
                        || (p.FilePath is not null && p.FilePath.EndsWith(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (projects.Count == 0)
                    return $"no project matches: {path} (projects: {string.Join(", ", solution.Projects.Select(p => p.Name))})";
                break;
            }
            default:
                projects = solution.Projects.ToList();
                break;
        }

        var found = new List<(DiagnosticSeverity Sev, string Id, string File, int Line, string Msg)>();
        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null)
                continue;
            foreach (var diag in compilation.GetDiagnostics())
            {
                if (diag.Severity < minSev || diag.Severity == DiagnosticSeverity.Hidden)
                    continue;
                var pos = diag.Location.GetLineSpan();
                var file = diag.Location.IsInSource ? pos.Path : null;
                if (fileFilter is not null && !string.Equals(file, fileFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                found.Add((
                    diag.Severity,
                    diag.Id,
                    file is null ? "-" : locator.RelPath(file),
                    pos.StartLinePosition.Line + 1,
                    CompactFormatter.Truncate(diag.GetMessage(), 160)));
            }
        }

        var distinct = found.Distinct()
            .OrderByDescending(d => d.Sev)
            .ThenBy(d => d.File, StringComparer.Ordinal)
            .ThenBy(d => d.Line)
            .ToList();

        limit = Math.Clamp(limit, 1, MaxLimit);
        if (Formats.Parse(format ?? locator.Config.DefaultFormat) == OutputFormat.Json)
            return Formats.ToJson(new
            {
                total = distinct.Count,
                diagnostics = distinct.Take(limit).Select(d => new
                {
                    id = d.Id,
                    severity = d.Sev.ToString().ToLowerInvariant(),
                    file = d.File,
                    line = d.Line,
                    message = d.Msg,
                }),
            });

        if (distinct.Count == 0)
            return $"no diagnostics at {min_severity}+ severity";
        var rows = distinct.Take(limit)
            .Select(d => new[] { d.Id, SevCode(d.Sev), d.File, d.Line.ToString(), d.Msg })
            .ToList();
        return CompactFormatter.Table("diags", ["id", "sev", "file", "line", "message"], rows, distinct.Count);
    }

    // ---- helpers -------------------------------------------------------------

    private static string NotLoaded(WorkspaceHost workspace) => workspace.State switch
    {
        WorkspaceState.Loading =>
            $"workspace still loading ({(int)workspace.LoadElapsed.TotalSeconds}s elapsed); retry shortly or call workspace_status",
        WorkspaceState.Failed =>
            "workspace load failed; call workspace_status for diagnostics",
        WorkspaceState.NoSolution =>
            "no solution/project found; set \"solution\" in .claude/dotnet-toolkit/config.json and call reload_workspace",
        _ => "workspace not loaded yet; call workspace_status",
    };

    private static async Task<(ISymbol? Symbol, string? Error)> ResolveAsync(Solution solution, string symbol)
    {
        var resolution = await SymbolResolver.ResolveAsync(solution, symbol);
        if (resolution.Symbol is not null)
            return (resolution.Symbol, null);
        if (resolution.Candidates.Count == 0)
            return (null, $"symbol not found: {symbol} (try find_symbol to locate it first)");
        var lines = resolution.Candidates.Take(10).Select(c => $"{KindCode(c)} {c.ToDisplayString()}");
        return (null, $"ambiguous ({resolution.Candidates.Count} candidates), use one of:\n{string.Join('\n', lines)}");
    }

    internal static string KindCode(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol nt => nt.TypeKind switch
        {
            TypeKind.Interface => "I",
            TypeKind.Struct => "S",
            TypeKind.Enum => "E",
            TypeKind.Delegate => "D",
            _ => nt.IsRecord ? "R" : "C",
        },
        IMethodSymbol { MethodKind: MethodKind.Constructor } => "K",
        IMethodSymbol => "M",
        IPropertySymbol => "P",
        IFieldSymbol => "F",
        IEventSymbol => "V",
        _ => "?",
    };

    private static (string File, int Line) Loc(ISymbol symbol, SolutionLocator locator)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null)
            return ("-", 0);
        var span = location.GetLineSpan();
        return (locator.RelPath(span.Path), span.StartLinePosition.Line + 1);
    }

    private static bool IsListableMember(ISymbol member)
    {
        if (member.IsImplicitlyDeclared)
            return false;
        if (member is IMethodSymbol { MethodKind: not (MethodKind.Ordinary or MethodKind.Constructor) })
            return false;
        return member.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.Internal;
    }

    private static string SevCode(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => "E",
        DiagnosticSeverity.Warning => "W",
        _ => "I",
    };

    private static string ContainingMember(SyntaxNode node)
    {
        foreach (var ancestor in node.AncestorsAndSelf())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax m:
                    return m.Identifier.Text;
                case ConstructorDeclarationSyntax c:
                    return c.Identifier.Text + ".ctor";
                case PropertyDeclarationSyntax p:
                    return p.Identifier.Text;
                case FieldDeclarationSyntax f:
                    return f.Declaration.Variables.First().Identifier.Text;
                case BaseTypeDeclarationSyntax t:
                    return t.Identifier.Text;
            }
        }
        return "-";
    }
}
