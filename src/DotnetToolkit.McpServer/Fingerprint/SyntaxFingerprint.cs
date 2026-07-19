using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetToolkit.McpServer.Fingerprint;

/// <summary>
/// Computes the deterministic <c>decl</c> and <c>body</c> version-token layers (spec §7) from a
/// declaration's syntax. Hashing is over the declaration's <em>token text</em> only: all trivia —
/// whitespace and comments alike — is excluded, so a comment-only or formatting-only edit changes
/// no layer (Conformance C1). The input is normalized syntax, never file paths or timestamps, so
/// the same declaration hashes identically across machines and restarts.
/// </summary>
public static class SyntaxFingerprint
{
    /// <summary>
    /// Returns the <c>decl</c> hash and, for members with a body/initializer, the <c>body</c> hash.
    /// Types and other declarations without an implementation return a null body layer.
    /// </summary>
    public static (string Decl, string? Body) Compute(SyntaxNode declaration)
    {
        switch (declaration)
        {
            case TypeDeclarationSyntax t:
                return (Hash(TokensBefore(t, t.OpenBraceToken)), null);
            case EnumDeclarationSyntax e:
                return (Hash(TokensBefore(e, e.OpenBraceToken)), null);
            case BaseMethodDeclarationSyntax m:
            {
                SyntaxNode? body = (SyntaxNode?)m.Body ?? m.ExpressionBody;
                return (Hash(TokensExcluding(m, body)), body is null ? null : Hash(AllTokens(body)));
            }
            case PropertyDeclarationSyntax p:
            {
                SyntaxNode? body = (SyntaxNode?)p.ExpressionBody ?? p.AccessorList;
                return (Hash(TokensExcluding(p, body)), body is null ? null : Hash(AllTokens(body)));
            }
            case BaseFieldDeclarationSyntax f:
            {
                // Body layer covers the initializer(s); decl covers the type + names + modifiers.
                var initializers = f.Declaration.Variables
                    .Select(v => (SyntaxNode?)v.Initializer).Where(i => i is not null).ToList();
                var declText = TokensExcludingMany(f, initializers!);
                var bodyText = initializers.Count == 0 ? null
                    : string.Join(" ", initializers.Select(i => AllTokens(i!)));
                return (Hash(declText), bodyText is null ? null : Hash(bodyText));
            }
            default:
                return (Hash(AllTokens(declaration)), null);
        }
    }

    private static string AllTokens(SyntaxNode node)
    {
        var sb = new StringBuilder();
        foreach (var token in node.DescendantTokens())
            Append(sb, token.Text);
        return sb.ToString();
    }

    private static string TokensBefore(SyntaxNode node, SyntaxToken boundary)
    {
        var start = boundary.SpanStart;
        var sb = new StringBuilder();
        foreach (var token in node.DescendantTokens())
        {
            if (token.SpanStart >= start)
                break;
            Append(sb, token.Text);
        }
        return sb.ToString();
    }

    private static string TokensExcluding(SyntaxNode node, SyntaxNode? excluded) =>
        excluded is null ? AllTokens(node) : TokensExcludingMany(node, [excluded]);

    private static string TokensExcludingMany(SyntaxNode node, IReadOnlyList<SyntaxNode> excluded)
    {
        var spans = excluded.Select(e => e.FullSpan).ToList();
        var sb = new StringBuilder();
        foreach (var token in node.DescendantTokens())
        {
            if (spans.Any(s => s.Contains(token.Span)))
                continue;
            Append(sb, token.Text);
        }
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string text)
    {
        if (sb.Length > 0)
            sb.Append(' ');
        sb.Append(text);
    }

    private static string Hash(string tokenText)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(tokenText));
        // 6 bytes → 12 hex chars, matching the version-token examples in spec §7.
        return Convert.ToHexStringLower(digest.AsSpan(0, 6));
    }
}
