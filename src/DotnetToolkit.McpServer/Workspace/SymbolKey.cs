using DotnetToolkit.McpServer.Identity;
using Microsoft.CodeAnalysis;

namespace DotnetToolkit.McpServer.Workspace;

/// <summary>
/// Derives the stable <c>symbolId</c> (spec §6) and single-letter kind code for a Roslyn symbol.
/// The id is content-derived from the documentation-comment id (a stable metadata identifier) plus
/// the containing assembly, so it survives file renames and changes only on symbol rename.
/// </summary>
public static class SymbolKey
{
    public static string IdOf(ISymbol symbol)
    {
        var metadataName = symbol.GetDocumentationCommentId()
            ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var assembly = symbol.ContainingAssembly?.Name ?? "";
        return Ids.SymbolId(metadataName, assembly);
    }

    public static string KindOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol nt => nt.TypeKind switch
        {
            TypeKind.Interface => "Interface",
            TypeKind.Struct => "Struct",
            TypeKind.Enum => "Enum",
            TypeKind.Delegate => "Delegate",
            _ => nt.IsRecord ? "Record" : "Type",
        },
        IMethodSymbol => "Method",
        IPropertySymbol => "Property",
        IFieldSymbol => "Field",
        IEventSymbol => "Event",
        _ => symbol.Kind.ToString(),
    };

    /// <summary>
    /// Reduces a symbol to whatever <see cref="IdOf"/> should actually hash: the unreduced declaration
    /// behind a reduced extension-method call (<c>values.Where(...)</c> binds to a reduced form whose own
    /// <c>OriginalDefinition</c> stays reduced, never reaching the static method), then the open
    /// generic definition behind any constructed type or method (<c>List&lt;int&gt;</c>,
    /// <c>Foo.Bar&lt;string&gt;()</c>). A non-generic, non-reduced symbol passes through unchanged.
    /// </summary>
    public static ISymbol Canonicalize(ISymbol symbol) =>
        (symbol is IMethodSymbol { ReducedFrom: { } reducedFrom } ? reducedFrom : symbol).OriginalDefinition;

    /// <summary>
    /// The raw documentation-comment id <see cref="IdOf"/> hashes into a symbolId — stored alongside an
    /// external symbol row so it can be resolved back into a live <c>ISymbol</c> later via
    /// <c>DocumentationCommentId.GetSymbolsForDeclarationId</c> without reverse-engineering it from the
    /// hash. Null only for the rare symbol kind Roslyn cannot mint a doc-comment id for at all.
    /// </summary>
    public static string? DocumentationIdOf(ISymbol symbol) => symbol.GetDocumentationCommentId();
}
