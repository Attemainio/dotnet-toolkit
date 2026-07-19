namespace DotnetToolkit.McpServer.Contracts;

/// <summary>
/// Which parts of a symbol response the caller asked for. <c>resolution</c> stays the coarse default —
/// one word covers the common case — and <c>include</c>/<c>exclude</c> adjust it for the targeted case,
/// e.g. "full but without the source" or "signature plus members".
///
/// Component names are exactly the response field names they control, so there is no second vocabulary
/// to learn: what you ask for is what appears in the JSON.
///
/// The skeleton (kind, displayString, accessibility, containingType, declarationSites) is never optional.
/// It is the symbol's identity and costs almost nothing; making it removable would buy no tokens worth
/// having and would let a response come back that no consumer could interpret.
/// </summary>
public readonly record struct SymbolComponents
{
    public const string Source = "source";
    public const string XmlDoc = "xmlDoc";
    public const string MechanicalFacts = "mechanicalFacts";
    public const string ReferenceCounts = "referenceCounts";
    public const string RecentLog = "recentLog";
    public const string Members = "members";

    public static readonly IReadOnlyList<string> All =
        [Source, XmlDoc, MechanicalFacts, ReferenceCounts, RecentLog, Members];

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

    /// <summary>
    /// Resolves <c>resolution</c> into a base set, then applies include/exclude. Returns null and sets
    /// <paramref name="invalid"/> when a name is not a component — a typo silently ignored would leave
    /// the caller believing it dropped a field it is still paying for.
    /// </summary>
    public static SymbolComponents? Resolve(
        string resolution, string? include, string? exclude, bool isType, out string? invalid)
    {
        invalid = null;
        var res = resolution.Trim().ToLowerInvariant();

        // Preset contents match what each resolution has always returned, so an existing caller that
        // passes no include/exclude sees no change — with one exception: outline used to return early
        // and drop containingType and recentLog, which was an inconsistency rather than a decision.
        var set = res switch
        {
            "full" => new HashSet<string>(StringComparer.Ordinal)
                { Source, XmlDoc, MechanicalFacts, ReferenceCounts, RecentLog },
            "outline" when isType => new HashSet<string>(StringComparer.Ordinal)
                { XmlDoc, ReferenceCounts, RecentLog, Members },
            // outline on a non-type has no member list to give, so it degrades to signature rather
            // than pretending; the caller still gets a usable answer.
            _ => new HashSet<string>(StringComparer.Ordinal) { XmlDoc, ReferenceCounts, RecentLog },
        };

        if (!Apply(include, set, add: true, ref invalid) || !Apply(exclude, set, add: false, ref invalid))
            return null;

        return new SymbolComponents(set);
    }

    private static bool Apply(string? names, HashSet<string> set, bool add, ref string? invalid)
    {
        if (string.IsNullOrWhiteSpace(names))
            return true;

        foreach (var raw in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = All.FirstOrDefault(c => string.Equals(c, raw, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                invalid = raw;
                return false;
            }
            if (add)
                set.Add(match);
            else
                set.Remove(match);
        }
        return true;
    }
}
