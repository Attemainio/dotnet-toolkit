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
        var assembly = symbol.ContainingAssembly?.Name ?? "";

        // Local functions and lambdas are routed to the fallback deliberately, even though Roslyn does mint a
        // doc-comment id for them: that id is built as though the symbol were a member of the containing TYPE,
        // because the container walk skips the enclosing method. Four same-signature `Fail` helpers in four
        // different methods of one class therefore minted ONE id -- simultaneously ambiguous and dead, since a
        // local function is not in the symbol index and get_symbol answers symbol_not_found for it. Qualifying
        // by the enclosing symbol makes them distinct, and the fallback prefix says "not a fetch target" out loud.
        if (symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction })
            return Ids.FallbackSymbolId(
                symbol.ContainingSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    + "/" + symbol.ToDisplayString(),
                assembly);

        if (symbol.GetDocumentationCommentId() is { } docId)
            return Ids.SymbolId(docId, assembly);

        // No doc-comment id at all (some symbol kinds structurally lack one, or the symbol was bound
        // against a transiently incomplete compilation) -- a disjoint prefix so this can never be
        // silently mistaken for the same id a clean bind of the same logical symbol would produce.
        return Ids.FallbackSymbolId(symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), assembly);
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

    /// <summary>Whether a <see cref="KindOf"/> word names a type rather than a member.</summary>
    /// <remarks>
    /// The distinction decides which reference counts can apply to a symbol at all: call edges bind to
    /// members, so a named type's caller count is structurally zero rather than measured, while
    /// implementations is the relationship a type actually has. Kept beside <see cref="KindOf"/> because it
    /// is that method's vocabulary being tested -- a copy of the word list anywhere else silently stops
    /// agreeing the moment a kind is added.
    /// </remarks>
    public static bool IsNamedTypeKind(string kind) =>
        kind is "Type" or "Interface" or "Struct" or "Enum" or "Delegate" or "Record";

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
