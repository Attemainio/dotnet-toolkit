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

        // A bare "SymbolStore" is a suffix of the type's own display name AND of its constructor's
        // ("Ns.SymbolStore.SymbolStore"), so the expansion above made every class that declares a constructor
        // report ambiguous_symbol against itself -- two calls where one answers, on most non-static classes.
        // A spec naming no parameter list is asking for the type, so drop the constructors that type already
        // contains. "SymbolStore.SymbolStore" still resolves to the constructor, since the type's own display
        // name does not end in that suffix; a spec WITH a parameter list never matches the type at all.
        if (specParams is null && matches.Count > 1)
        {
            var matchedTypes = matches.Where(s => s is INamedTypeSymbol)
                .Select(s => s.ToDisplayString())
                .ToHashSet(StringComparer.Ordinal);
            matches = matches
                .Where(s => s is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor
                    || !matchedTypes.Contains(ctor.ContainingType.ToDisplayString()))
                .ToList();
        }

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
    /// </summary>
    /// <remarks>
    /// Whitespace inside the parameter list survives, unlike in <see cref="ShortParams"/>: stripping it
    /// rendered <c>List&lt;(DateTime time, decimal amount)&gt;</c> as
    /// <c>List&lt;(DateTimetime,decimalamount)&gt;</c> and <c>out T</c> as <c>outT</c>, which read as type
    /// names that do not exist. Resolvability is unaffected — <see cref="MatchesSpec"/> compares both
    /// sides whitespace-blind, so a name emitted here still resolves by construction.
    /// </remarks>
    public static string CompactName(string fqName)
    {
        var paren = fqName.IndexOf('(');
        return paren < 0 ? fqName : fqName[..paren] + NamespacePrefixRegex().Replace(fqName[paren..], "");
    }

    /// <summary>
    /// A member name reduced to its containing type and member — <c>ContextTools.GetSymbol</c> — by
    /// dropping the namespace path in front of it.
    /// </summary>
    /// <remarks>
    /// The string form of what <c>ContextTools.CompactDisplay</c> renders from a live <c>ISymbol</c>, for
    /// the tools that only hold a stored name. It keeps the last two dot-separated segments, so a member of
    /// a NESTED type keeps the inner type and loses the outer one; the accompanying symbolId is the exact
    /// identity either way.
    /// </remarks>
    /// <param name="fqName">A fully-qualified member name, with or without a parameter list.</param>
    /// <returns>The containing type and member name, or the whole name when it has no namespace path.</returns>
    public static string MemberWithContainingType(string fqName)
    {
        var depth = 0;
        var lastDot = -1;
        var previousDot = -1;
        for (var i = 0; i < fqName.Length; i++)
        {
            switch (fqName[i])
            {
                case '<' or '[' or '{':
                    depth++;
                    break;
                case '>' or ']' or '}':
                    depth--;
                    break;
                case '(':
                    // The parameter list is not part of the name being segmented, and its own types carry
                    // dots of their own.
                    i = fqName.Length;
                    break;
                case '.' when depth == 0:
                    previousDot = lastDot;
                    lastDot = i;
                    break;
            }
        }

        return previousDot < 0 ? fqName : fqName[(previousDot + 1)..];
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

    /// <summary>
    /// How many parameters a name's parameter list declares, or <c>-1</c> when it carries no parameter
    /// list at all — what tells the members of an overload set apart once their parameter list has been
    /// dropped to match the syntax index's bare-name keys.
    /// </summary>
    /// <remarks>
    /// Counts top-level commas only, so a generic argument, tuple element or default value does not
    /// inflate the count. Accepts both the symbol store's <c>Name(int,string)</c> form and the syntax
    /// index's <c>Name(int a, string b = "x") -&gt; int</c> signature form.
    /// </remarks>
    public static int ParameterArity(string nameWithParameters)
    {
        var open = nameWithParameters.IndexOf('(');
        if (open < 0)
            return -1;

        var depth = 0;
        var commas = 0;
        var sawParameter = false;
        for (var i = open; i < nameWithParameters.Length; i++)
        {
            var c = nameWithParameters[i];
            switch (c)
            {
                case '(' or '<' or '[' or '{':
                    depth++;
                    break;
                case ')' or '>' or ']' or '}':
                    depth--;
                    if (depth == 0)
                        return sawParameter ? commas + 1 : 0;
                    break;
                case ',' when depth == 1:
                    commas++;
                    sawParameter = true;
                    break;
                default:
                    if (!char.IsWhiteSpace(c))
                        sawParameter = true;
                    break;
            }
        }

        return sawParameter ? commas + 1 : 0;
    }

    /// <summary>
    /// The parameter TYPE list of a store-form name — <c>Name(int, System.SortOrder)</c> — normalized to
    /// short type names without whitespace, or <c>null</c> when the name carries no parameter list.
    /// </summary>
    /// <remarks>
    /// This is what tells two SAME-ARITY overloads apart once their bare names have collapsed onto one
    /// key, which parameter count alone cannot do. Compare it against
    /// <see cref="SignatureParameterTypeKey"/> for the syntax index's side of the same member; the two
    /// forms differ only in that the index's carries parameter names and defaults.
    /// </remarks>
    /// <param name="nameWithParameters">A name whose parameter list states types only.</param>
    /// <returns>The normalized parenthesized type list, or null when there is no parameter list.</returns>
    public static string? ParameterTypeKey(string nameWithParameters)
    {
        var parameters = ParameterListOf(nameWithParameters);
        return parameters is null ? null : ShortParams(parameters);
    }

    /// <summary>
    /// The same key as <see cref="ParameterTypeKey"/>, computed from the syntax index's signature form
    /// (<c>Name(int a, string b = "x") -&gt; int</c>) by dropping each parameter's name and default value.
    /// </summary>
    /// <param name="signature">A member signature as the syntax index stores it.</param>
    /// <returns>The normalized parenthesized type list, or null when there is no parameter list.</returns>
    public static string? SignatureParameterTypeKey(string signature)
    {
        var parameters = ParameterListOf(signature);
        if (parameters is null)
            return null;

        var types = SplitTopLevel(parameters).Select(TypeOfParameter).Where(t => t.Length > 0);
        return $"({string.Join(',', types)})";
    }

    /// <summary>Reduces a parenthesized parameter list to short type names without whitespace.</summary>
    private static string ShortParams(string parenList) =>
        NamespacePrefixRegex().Replace(parenList, "").Replace(" ", "");

    /// <summary>The parenthesized parameter list of a name, parentheses included, or null when it has none.</summary>
    private static string? ParameterListOf(string nameWithParameters)
    {
        var open = nameWithParameters.IndexOf('(');
        if (open < 0)
            return null;

        var depth = 0;
        for (var i = open; i < nameWithParameters.Length; i++)
        {
            switch (nameWithParameters[i])
            {
                case '(' or '<' or '[' or '{':
                    depth++;
                    break;
                case ')' or '>' or ']' or '}':
                    depth--;
                    if (depth == 0)
                        return nameWithParameters[open..(i + 1)];
                    break;
            }
        }

        // Unbalanced text is not this method's problem to diagnose: hand back what there is, so a
        // truncated name degrades to a key that simply fails to match rather than throwing.
        return nameWithParameters[open..];
    }

    /// <summary>Splits a parenthesized parameter list into its top-level parameters, parentheses removed.</summary>
    private static List<string> SplitTopLevel(string parenList)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 1;
        for (var i = 0; i < parenList.Length; i++)
        {
            switch (parenList[i])
            {
                case '(' or '<' or '[' or '{':
                    depth++;
                    break;
                case ')' or '>' or ']' or '}':
                    depth--;
                    if (depth == 0)
                    {
                        parts.Add(parenList[start..i]);
                        return parts;
                    }
                    break;
                case ',' when depth == 1:
                    parts.Add(parenList[start..i]);
                    start = i + 1;
                    break;
            }
        }

        parts.Add(parenList[start..]);
        return parts;
    }

    /// <summary>
    /// One indexed parameter reduced to its type: default value dropped, then the declared name, which is
    /// the last whitespace-separated token — so any <c>out</c>/<c>ref</c>/<c>params</c> modifier stays
    /// attached to the type, exactly as the store's own type-only form carries it. A leading <c>this</c>
    /// (an extension method's own receiver parameter) is dropped instead: Roslyn's display format never
    /// includes it, so the two names would otherwise never agree and the overload would never locate.
    /// </summary>
    private static string TypeOfParameter(string parameter)
    {
        var declared = parameter;
        var defaultValue = declared.IndexOf('=');
        if (defaultValue >= 0)
            declared = declared[..defaultValue];

        var tokens = declared.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 0 && tokens[0] == "this")
            tokens = tokens[1..];
        return ShortParams(string.Join(' ', tokens.Length > 1 ? tokens[..^1] : tokens));
    }

    private static string StripGenericArgs(string name) =>
        GenericArgsRegex().Replace(name, "");

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*\.")]
    private static partial Regex NamespacePrefixRegex();

    [GeneratedRegex(@"<[^<>]*>")]
    private static partial Regex GenericArgsRegex();
}
