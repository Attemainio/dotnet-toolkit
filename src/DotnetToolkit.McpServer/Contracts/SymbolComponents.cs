namespace DotnetToolkit.McpServer.Contracts;

/// <summary>
/// Which parts of a symbol response the caller asked for, via the single <c>include</c> argument:
/// <c>"standard"</c> (default) for <see cref="Standard"/>, <c>"all"</c> for every component, or a comma
/// list of component names that IS the requested set — a literal query of exactly the columns wanted,
/// not an adjustment to a default.
///
/// Component names are exactly the response field names they control, so there is no second vocabulary
/// to learn: what you ask for is what appears in the JSON.
///
/// The skeleton (kind, origin, containingType, declarationSites, displayString, modifiers) is never
/// optional — it is the symbol's identity and its location, and costs almost nothing. There is no
/// separate accessibility field — modifiers' literal keyword phrase already carries it ("public sealed"
/// states both), so a second field saying the same thing would be pure duplication. There is no source
/// component either: get_symbol locates, and a Read over declarationSites fetches the text.
/// </summary>
public readonly record struct SymbolComponents
{
    public const string XmlDoc = "xmlDoc";
    public const string MechanicalFacts = "mechanicalFacts";
    public const string BodyOutline = "bodyOutline";
    public const string ReferenceCounts = "referenceCounts";
    public const string Members = "members";
    public const string Attributes = "attributes";
    // Declaration-only facts (no semantic-model body walk): the direct base type/interfaces — type-only,
    // null for anything else, same as Members. modifiers is NOT here: it is unconditional (see the type
    // doc comment), not an opt-in include component.
    public const string BaseType = "baseType";
    public const string Interfaces = "interfaces";
    public const string Usings = "usings";

    public static readonly IReadOnlyList<string> All =
        [XmlDoc, MechanicalFacts, BodyOutline, ReferenceCounts, Members, Attributes,
         BaseType, Interfaces, Usings];

    private readonly HashSet<string> _set;

    private SymbolComponents(HashSet<string> set) => _set = set;

    public bool Has(string component) => _set is not null && _set.Contains(component);

    /// <summary>The resolved set, in canonical order — echoed back so the caller can see what it got.</summary>
    public IReadOnlyList<string> Resolved => [.. All.Where(Has)];

    /// <summary>The default set: whichever components are meaningful on essentially every call.</summary>
    public static readonly IReadOnlyList<string> Standard = [XmlDoc, ReferenceCounts];

    /// <summary>
    /// Resolves <c>include</c> into an exact component set. <c>include</c> is a plain comma list of
    /// component names — <c>"standard"</c> (default, same as <c>null</c>/empty), <c>"all"</c>, or a
    /// literal list — and it REPLACES the default rather than adding to it.
    /// </summary>
    /// <returns>
    /// Null, with <paramref name="invalid"/> set, when a name is not a component. A typo silently ignored
    /// would leave the caller believing it dropped a field that it did not actually get.
    /// </returns>
    public static SymbolComponents? Resolve(string? include, out string? invalid)
    {
        invalid = null;
        var trimmed = include?.Trim();

        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "standard", StringComparison.OrdinalIgnoreCase))
            return new SymbolComponents(new HashSet<string>(Standard, StringComparer.Ordinal));

        if (string.Equals(trimmed, "all", StringComparison.OrdinalIgnoreCase))
            return new SymbolComponents(new HashSet<string>(All, StringComparer.Ordinal));

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = All.FirstOrDefault(c => string.Equals(c, raw, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                invalid = raw;
                return null;
            }
            set.Add(match);
        }
        return new SymbolComponents(set);
    }
}
