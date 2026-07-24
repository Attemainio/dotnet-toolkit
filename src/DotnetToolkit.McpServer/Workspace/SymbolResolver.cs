using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace DotnetToolkit.McpServer.Workspace;

/// <summary>
/// Resolves a user-supplied symbol spec — a fully-qualified name, a unique suffix, or
/// either with a parameter list for overload disambiguation (e.g.
/// "Contoso.OrderService.PlaceOrder(OrderRequest)") — to source symbols in the solution.
/// </summary>
public static partial class SymbolResolver
{
    public sealed record Resolution(ISymbol? Symbol, IReadOnlyList<ISymbol> Candidates);

    public static async Task<Resolution> ResolveAsync(Solution solution, string spec, CancellationToken ct = default)
    {
        spec = spec.Trim();
        string? specParams = null;
        var paren = spec.IndexOf('(');
        if (paren >= 0)
        {
            specParams = ShortParams(spec[paren..]);
            spec = spec[..paren];
        }

        var name = spec[(spec.LastIndexOf('.') + 1)..];
        var lt = name.IndexOf('<');
        if (lt >= 0)
            name = name[..lt];
        if (name.Length == 0)
            return new Resolution(null, []);

        var declarations = await SymbolFinder.FindSourceDeclarationsAsync(solution, name, ignoreCase: true, ct);
        // FindSourceDeclarationsAsync matches by symbol.Name, and a constructor's Name is ".ctor" — never
        // the type name — so a "Type.Type(...)" spec can only ever land here as the type itself. Expand
        // each type hit into its own instance constructors too, so a constructor spec has something to
        // match against.
        var candidates = declarations.SelectMany(s => s is INamedTypeSymbol type
            ? type.InstanceConstructors.Cast<ISymbol>().Append(type)
            : [s]);
        var matches = candidates
            .Where(s => MatchesSpec(s, spec, specParams))
            .DistinctBy(s => s.ToDisplayString())
            .ToList();

        return new Resolution(matches.Count == 1 ? matches[0] : null, matches);
    }

    private static bool MatchesSpec(ISymbol symbol, string spec, string? specParams)
    {
        var display = symbol.ToDisplayString();
        string? displayParams = null;
        var paren = display.IndexOf('(');
        if (paren >= 0)
        {
            displayParams = ShortParams(display[paren..]);
            display = display[..paren];
        }

        var displayShort = StripGenericArgs(display);
        var specShort = StripGenericArgs(spec);
        if (!displayShort.Equals(specShort, StringComparison.OrdinalIgnoreCase)
            && !displayShort.EndsWith("." + specShort, StringComparison.OrdinalIgnoreCase))
            return false;

        return specParams is null
            || (displayParams is not null && displayParams.Equals(specParams, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A fully-qualified name with its parameter list reduced to short type names — the form search
    /// results are emitted in. The container path stays fully qualified because that is what makes the
    /// name unambiguous across namespaces; the parameter types do not need it, and repeating a namespace
    /// once per parameter is most of what a stored display name costs.
    ///
    /// This is deliberately the same reduction <see cref="MatchesSpec"/> applies to both sides before
    /// comparing, so a name emitted here is resolvable by construction rather than by coincidence.
    /// </summary>
    public static string CompactName(string fqName)
    {
        var paren = fqName.IndexOf('(');
        return paren < 0 ? fqName : fqName[..paren] + ShortParams(fqName[paren..]);
    }

    /// <summary>
    /// The name with any parameter list dropped entirely — the form the syntax index keys declarations
    /// by, so a stored name can be matched against it.
    /// </summary>
    public static string NameWithoutParameters(string fqName)
    {
        var paren = fqName.IndexOf('(');
        return paren < 0 ? fqName : fqName[..paren];
    }

    /// <summary>Reduces a parenthesized parameter list to short type names without whitespace.</summary>
    private static string ShortParams(string parenList) =>
        NamespacePrefixRegex().Replace(parenList, "").Replace(" ", "");

    private static string StripGenericArgs(string name) =>
        GenericArgsRegex().Replace(name, "");

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*\.")]
    private static partial Regex NamespacePrefixRegex();

    [GeneratedRegex(@"<[^<>]*>")]
    private static partial Regex GenericArgsRegex();
}
