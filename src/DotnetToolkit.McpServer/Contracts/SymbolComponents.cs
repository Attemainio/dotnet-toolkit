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
/// The skeleton (kind, origin, containingType, declarationSites) is never optional — it is the symbol's
/// identity and costs almost nothing. displayString and modifiers sit one tier below: computed
/// unconditionally like the skeleton, but suppressed (null) when <see cref="Source"/> is also requested,
/// since a declaration's own signature line already states both as text. There is no separate
/// accessibility field — modifiers' literal keyword phrase already carries it ("public sealed" states
/// both), so a second field saying the same thing would be pure duplication.
/// </summary>
public readonly record struct SymbolComponents
{
    public const string Source = "source";
    public const string XmlDoc = "xmlDoc";
    public const string MechanicalFacts = "mechanicalFacts";
    public const string ReferenceCounts = "referenceCounts";
    public const string RecentLog = "recentLog";
    public const string Members = "members";
    public const string Attributes = "attributes";
    // Declaration-only facts (no semantic-model body walk): the direct base type/interfaces — type-only,
    // null for anything else, same as Members. modifiers is NOT here: it is unconditional (see the type
    // doc comment), not an opt-in include component.
    public const string BaseType = "baseType";
    public const string Interfaces = "interfaces";
    public const string Usings = "usings";

    public static readonly IReadOnlyList<string> All =
        [Source, XmlDoc, MechanicalFacts, ReferenceCounts, RecentLog, Members, Attributes,
         BaseType, Interfaces, Usings];

    private readonly HashSet<string> _set;

    private SymbolComponents(HashSet<string> set) => _set = set;

    public bool Has(string component) => _set is not null && _set.Contains(component);

    /// <summary>The resolved set, in canonical order — echoed back so the caller can see what it got.</summary>
    public IReadOnlyList<string> Resolved => [.. All.Where(Has)];

    /// <summary>
    /// The version layers this component set is derived from. This is what makes a partial fetch safe to
    /// lease: a token narrowed to the layers actually served cannot later be mistaken for evidence that a
    /// layer the caller never received is unchanged.
    /// </summary>
    public IReadOnlyList<string> RequiredLayers
    {
        get
        {
            // decl is unconditional: the skeleton, xmlDoc and the member list are declaration-derived.
            var layers = new List<string> { "decl" };
            // recentLog is NOT body-derived and deliberately absent here. Its current:true/false flag is
            // computed server-side against the live body, so the caller holding that layer is irrelevant
            // to whether the flag can be trusted.
            if (Has(Source) || Has(MechanicalFacts))
                layers.Add("body");
            if (Has(ReferenceCounts))
                layers.Add("refs");
            return layers;
        }
    }

    /// <summary>The default set: whichever components are meaningful on essentially every call.</summary>
    public static readonly IReadOnlyList<string> Standard = [XmlDoc, ReferenceCounts, RecentLog];

    /// <summary>
    /// Resolves <c>include</c> into an exact component set — <c>"standard"</c> (default, same as
    /// <c>null</c>/empty) for <see cref="Standard"/>, <c>"all"</c> for every component, or a comma list
    /// naming the set precisely. A comma list REPLACES the default rather than adding to it: it is a
    /// literal query of exactly the columns wanted, not a delta. Returns null and sets
    /// <paramref name="invalid"/> when a name is not a component — a typo silently ignored would leave
    /// the caller believing it dropped a field it is still paying for.
    /// </summary>
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
