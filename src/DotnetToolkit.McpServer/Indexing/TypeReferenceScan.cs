using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace DotnetToolkit.McpServer.Indexing;

/// <summary>
/// Resolves the members that REFERENCE a named type, from the compiler's own model rather than the
/// cached call-edge table.
/// </summary>
/// <remarks>
/// A class, record, interface or delegate has no call sites of its own, so the edge table
/// <see cref="CallHierarchy"/> walks holds no neighbours for one at all. get_references had always
/// fallen back to this scan; get_call_hierarchy had not, and answered "how much does changing this
/// ripple" with a blast radius of 1 for a record that 79 members referenced — a reassuring wrong number
/// for the one question that tool exists to answer.
/// <para>
/// Lifting the scan out of the tool layer is what makes the two agree by construction instead of by
/// coincidence: "who uses this type" now has exactly one definition, including which locations are
/// excluded from it.
/// </para>
/// </remarks>
internal static class TypeReferenceScan
{
    /// <summary>The member declaration enclosing a reference, or the enclosing symbol when there is none.</summary>
    /// <param name="location">One reference site from <see cref="SymbolFinder"/>.</param>
    /// <returns>The owning member, or null when the document yields no syntax root or semantic model.</returns>
    /// <remarks>
    /// GetEnclosingSymbol alone answers the containing TYPE for a reference sitting in a field or
    /// event-field declaration, which would collapse every such use onto one item; walking to the owning
    /// member declaration keeps the event or field itself as the reported user.
    /// </remarks>
    public static async Task<ISymbol?> OwningMemberAsync(ReferenceLocation location)
    {
        var root = await location.Document.GetSyntaxRootAsync();
        var model = await location.Document.GetSemanticModelAsync();
        if (root is null || model is null)
            return null;

        var member = root.FindNode(location.Location.SourceSpan)
            .AncestorsAndSelf()
            .OfType<MemberDeclarationSyntax>()
            .FirstOrDefault();
        return member switch
        {
            BaseFieldDeclarationSyntax field => model.GetDeclaredSymbol(field.Declaration.Variables[0]),
            null => model.GetEnclosingSymbol(location.Location.SourceSpan.Start),
            _ => model.GetDeclaredSymbol(member),
        };
    }

    /// <summary>
    /// Whether a reference sits inside an XML documentation <c>cref</c> rather than in real code.
    /// </summary>
    /// <param name="location">A reference site's source location.</param>
    /// <returns>True when the location resolves through a <c>&lt;see cref="..."/&gt;</c> or similar.</returns>
    /// <remarks>
    /// Roslyn binds a cref to the symbol it names, so FindReferences hands doc mentions back alongside
    /// code. They belong to the same category as the comment and string matches the reference tools
    /// already exclude, and those tools' whole claim over grep is that they do not return comment
    /// matches as hits.
    /// </remarks>
    public static bool IsCrefLocation(Location location)
    {
        var root = location.SourceTree?.GetRoot();
        return root is not null
            && root.FindNode(location.SourceSpan, findInsideTrivia: true, getInnermostNodeForTie: true)
                .AncestorsAndSelf().Any(n => n is CrefSyntax);
    }

    /// <summary>Each member referencing <paramref name="type"/> anywhere in source, once.</summary>
    /// <param name="type">The named type to scan for.</param>
    /// <param name="solution">The solution to search.</param>
    /// <param name="includeDocMentions">
    /// Whether to count a member that only names the type in a <c>cref</c>. False by default, matching
    /// get_references: a doc mention is not a dependency, and counting it would inflate a blast radius
    /// with members that would not need to change.
    /// </param>
    /// <returns>The referencing members in discovery order, deduplicated.</returns>
    public static async Task<IReadOnlyList<ISymbol>> ReferencingMembersAsync(
        INamedTypeSymbol type,
        Solution solution,
        bool includeDocMentions = false)
    {
        var owners = new List<ISymbol>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var reference in await SymbolFinder.FindReferencesAsync(type, solution))
        {
            foreach (var location in reference.Locations)
            {
                if (!location.Location.IsInSource)
                    continue;
                if (!includeDocMentions && IsCrefLocation(location.Location))
                    continue;

                var owner = await OwningMemberAsync(location);
                if (owner is not null && seen.Add(owner))
                    owners.Add(owner);
            }
        }

        return owners;
    }
}
