using System.Security.Cryptography;
using System.Text;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.CodeAnalysis;

namespace DotnetToolkit.McpServer.Fingerprint;

/// <summary>
/// The semantic version-token layers of spec §7 — the two that need the semantic model rather than
/// bare syntax:
/// <list type="bullet">
///   <item><c>refs</c>: the sorted set of symbolIds this member references. Moves when a dependency
///   changes, which is what invalidates cached reference edges.</item>
///   <item><c>api</c>: the <c>decl</c> hashes of every public/internal member of the containing type.
///   Moves on any public-surface change to that type, even if this member is untouched.</item>
/// </list>
/// Both are order-independent by construction (inputs are sorted before hashing), so an unrelated
/// reordering of members or call sites does not churn the token.
/// </summary>
public static class SemanticFingerprint
{
    /// <summary>Hash of the sorted, de-duplicated symbolIds referenced from a member body.</summary>
    public static string? ComputeRefs(IEnumerable<string> referencedSymbolIds)
    {
        var sorted = referencedSymbolIds.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        return sorted.Count == 0 ? null : Hash(string.Join('\n', sorted));
    }

    /// <summary>
    /// Hash of the containing type's public/internal surface: each member's kind, display string and
    /// <c>decl</c> hash, sorted. Private members are excluded — they are not part of the API.
    /// </summary>
    public static string? ComputeApi(INamedTypeSymbol? type, Func<ISymbol, string?> declHashOf)
    {
        if (type is null)
            return null;

        var entries = new List<string>();
        foreach (var member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared)
                continue;
            if (member.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected
                or Accessibility.Internal or Accessibility.ProtectedOrInternal))
                continue;
            var decl = declHashOf(member);
            if (decl is null)
                continue;
            entries.Add($"{SymbolKey.KindOf(member)}|{member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}|{decl}");
        }

        if (entries.Count == 0)
            return null;
        entries.Sort(StringComparer.Ordinal);
        return Hash(string.Join('\n', entries));
    }

    private static string Hash(string input)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(digest.AsSpan(0, 6));
    }
}
