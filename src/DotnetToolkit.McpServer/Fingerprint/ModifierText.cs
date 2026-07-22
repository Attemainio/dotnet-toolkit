using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetToolkit.McpServer.Fingerprint;

/// <summary>
/// Renders a symbol's C# modifiers straight from the declaration — no semantic-model body walk,
/// same cost tier as reading <see cref="ISymbol.DeclaredAccessibility"/> itself. Two views over the
/// same underlying facts: <see cref="Render"/> is the literal keyword phrase a reader expects
/// ("public static readonly"), <see cref="Tags"/> is the superset used for indexing/filtering —
/// every keyword from <see cref="Render"/> plus a few cheap boolean facts that aren't keywords
/// (extension method, indexer, init-only setter, IDisposable/IAsyncDisposable).
/// </summary>
public static class ModifierText
{
    /// <summary>
    /// The canonical modifier phrase for <paramref name="sym"/>, in C# declaration order:
    /// accessibility, static, const, readonly, volatile, virtual, abstract, sealed, override,
    /// async, extern, partial. Only modifiers that actually apply to this symbol's kind appear.
    /// </summary>
    public static string Render(ISymbol sym) => string.Join(' ', Tokens(sym));

    /// <summary>
    /// The filterable tag set for <paramref name="sym"/>: every token from <see cref="Render"/>,
    /// lowercased, plus synthetic tags for cheap boolean facts that are not C# keywords —
    /// "extension", "indexer", "initonly", "disposable", "asyncdisposable" (the latter two only
    /// for a type symbol that implements the corresponding interface).
    /// </summary>
    public static IReadOnlyList<string> Tags(ISymbol sym)
    {
        var tags = new List<string>(Tokens(sym));

        if (sym is IMethodSymbol { IsExtensionMethod: true })
            tags.Add("extension");

        if (sym is IPropertySymbol property)
        {
            if (property.IsIndexer)
                tags.Add("indexer");
            if (property.SetMethod is { IsInitOnly: true })
                tags.Add("initonly");
        }

        if (sym is INamedTypeSymbol type)
        {
            if (type.AllInterfaces.Any(i => i.Name == "IDisposable"))
                tags.Add("disposable");
            if (type.AllInterfaces.Any(i => i.Name == "IAsyncDisposable"))
                tags.Add("asyncdisposable");
        }

        return tags;
    }

    private static List<string> Tokens(ISymbol sym)
    {
        var tokens = new List<string> { AccessibilityWord(sym.DeclaredAccessibility) };

        if (sym.IsStatic)
            tokens.Add("static");
        if (sym is IFieldSymbol { IsConst: true })
            tokens.Add("const");
        if (IsReadOnly(sym))
            tokens.Add("readonly");
        if (sym is IFieldSymbol { IsVolatile: true })
            tokens.Add("volatile");
        if (sym.IsVirtual)
            tokens.Add("virtual");
        if (sym.IsAbstract)
            tokens.Add("abstract");
        if (sym.IsSealed)
            tokens.Add("sealed");
        if (sym.IsOverride)
            tokens.Add("override");
        if (sym is IMethodSymbol { IsAsync: true })
            tokens.Add("async");
        if (sym.IsExtern)
            tokens.Add("extern");
        if (IsPartial(sym))
            tokens.Add("partial");

        return tokens;
    }

    private static bool IsReadOnly(ISymbol sym) => sym switch
    {
        IFieldSymbol { IsConst: false } field => field.IsReadOnly,
        IMethodSymbol method => method.IsReadOnly,
        // IPropertySymbol.IsReadOnly means "has no setter" (a get-only-property concept), not
        // "declared with the readonly keyword" — so a property's readonly-ness is read the same
        // way partial is, straight off the syntax token list.
        _ => HasModifierKeyword(sym, SyntaxKind.ReadOnlyKeyword),
    };

    // Neither "readonly" (for properties/types) nor "partial" is exposed as an ISymbol boolean, so both
    // are read straight off one declaring fragment's syntax token list. Every fragment of a partial
    // declaration carries the partial keyword, so checking the first fragment is sufficient for both.
    private static bool IsPartial(ISymbol sym) => HasModifierKeyword(sym, SyntaxKind.PartialKeyword);

    private static bool HasModifierKeyword(ISymbol sym, SyntaxKind keyword) =>
        sym.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is MemberDeclarationSyntax
        { Modifiers: var modifiers } && modifiers.Any(keyword);

    private static string AccessibilityWord(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Private => "private",
        Accessibility.ProtectedAndInternal => "private protected",
        Accessibility.Protected => "protected",
        Accessibility.Internal => "internal",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.Public => "public",
        _ => "private",
    };
}
