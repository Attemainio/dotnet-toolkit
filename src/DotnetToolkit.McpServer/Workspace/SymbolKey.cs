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
}
