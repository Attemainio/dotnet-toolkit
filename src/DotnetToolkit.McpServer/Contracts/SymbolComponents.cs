namespace DotnetToolkit.McpServer.Contracts;

/// <summary>
/// Which parts of a symbol response the caller asked for, via the single <c>include</c> argument:
/// <c>"standard"</c> (default) for <see cref="Standard"/>, <c>"all"</c> for every component, or a comma
/// list of component names that IS the requested set — a literal query of exactly the columns wanted,
/// not an adjustment to a default.
///
/// Component names are exactly the response field names they control, so there is no second vocabulary
/// to learn: what you ask for is what appears in the JSON. <see cref="Source"/> additionally accepts a
/// <c>:code</c> suffix (<c>"source:code"</c>) to request <see cref="SourceMode.Code"/> instead of the
/// default <see cref="SourceMode.Full"/> — the same declaration span minus its leading doc comment — and
/// an <c>@</c> line selector (<c>"source@46-76"</c>, <c>"source:code@46-76;79-83"</c>) narrowing the
/// result to those absolute file lines.
///
/// The skeleton (kind, origin, containingType, declarationSites) is never optional — it is the symbol's
/// identity and costs almost nothing. displayString and modifiers sit one tier below: computed
/// unconditionally like the skeleton, but suppressed (null) when <see cref="Source"/> is also requested,
/// since a declaration's own signature line already states both as text. A line-sliced source is the
/// exception — a slice usually does not contain the signature line, so both are restored rather than
/// leaving the caller a fragment that never says what member it belongs to. There is no separate
/// accessibility field — modifiers' literal keyword phrase already carries it ("public sealed" states
/// both), so a second field saying the same thing would be pure duplication.
/// </summary>
public readonly record struct SymbolComponents
{
    public const string Source = "source";
    public const string XmlDoc = "xmlDoc";
    public const string MechanicalFacts = "mechanicalFacts";
    public const string BodyOutline = "bodyOutline";
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
        [Source, XmlDoc, MechanicalFacts, BodyOutline, ReferenceCounts, RecentLog, Members, Attributes,
         BaseType, Interfaces, Usings];

    private readonly HashSet<string> _set;

    private SymbolComponents(HashSet<string> set, SourceQuery? sourceQuery = null, bool isAll = false)
    {
        _set = set;
        SourceQuery = sourceQuery ?? SourceQuery.Full;
        IsAll = isAll;
    }

    // Distinguishes "all" from a component list that happens to include Members: only under "all" is a
    // member row's contentVersion worth its tokens, since a read-only members-only call never uses it and
    // an about-to-edit call re-fetches the member itself before patching anyway (see the write skill).
    public bool IsAll { get; init; }

    public bool Has(string component) => _set is not null && _set.Contains(component);


    /// <summary>
    /// The resolved <see cref="Contracts.SourceQuery"/> to render <see cref="Source"/> with — meaningless
    /// unless <see cref="Has"/> is true for <see cref="Source"/>.
    /// </summary>
    public SourceQuery SourceQuery { get; }

    /// <summary>
    /// Whether <see cref="Source"/> was requested narrowed to specific lines rather than whole.
    /// </summary>
    /// <value>
    /// True only when both <see cref="Source"/> is present and its query carries an <c>@</c> selection.
    /// This is what decides that displayString/modifiers survive alongside source (see the type doc
    /// comment) and that a <c>sourceLines</c> span is worth reporting.
    /// </value>
    public bool HasSlicedSource => Has(Source) && SourceQuery.Lines.Count > 0;

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
            // A line-sliced source still takes the body layer: the slice is cut from the same body text,
            // so the token must still narrow when that text moves.
            if (Has(Source) || Has(MechanicalFacts) || Has(BodyOutline))
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
    /// literal query of exactly the columns wanted, not a delta. <see cref="Source"/> alone may carry a
    /// suffix parsed by <see cref="Contracts.SourceQuery.Parse"/> (<c>"source:full-remarks-attributes"</c>,
    /// <c>"source:code-comments"</c>, <c>"source@46-76"</c>, ...); a bare <c>"source"</c> is
    /// <see cref="Contracts.SourceQuery.Full"/>. Returns null and sets <paramref name="invalid"/> when a
    /// name is not a component, or a <c>source</c> suffix does not parse — a typo silently ignored would
    /// leave the caller believing it dropped a field, or got a query, it did not actually get.
    /// </summary>
    public static SymbolComponents? Resolve(string? include, out string? invalid)
    {
        invalid = null;
        var trimmed = include?.Trim();

        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "standard", StringComparison.OrdinalIgnoreCase))
            return new SymbolComponents(new HashSet<string>(Standard, StringComparer.Ordinal));

        if (string.Equals(trimmed, "all", StringComparison.OrdinalIgnoreCase))
            return new SymbolComponents(new HashSet<string>(All, StringComparer.Ordinal), isAll: true);


        var set = new HashSet<string>(StringComparer.Ordinal);
        SourceQuery? sourceQuery = null;
        foreach (var raw in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // ':' introduces a source mode and '@' a mode-less line selection; either one ends the
            // component name, so the split point is whichever appears first.
            var separator = raw.IndexOfAny([':', '@']);
            var name = separator < 0 ? raw : raw[..separator];
            var match = All.FirstOrDefault(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                invalid = raw;
                return null;
            }
            set.Add(match);

            if (separator < 0)
                continue;

            if (match != Source)
            {
                invalid = raw;
                return null;
            }
            // ':' is consumed as the mode separator; '@' is kept, since SourceQuery.Parse reads a
            // leading '@' as "no mode written, so full".
            var suffix = raw[separator] == ':' ? raw[(separator + 1)..] : raw[separator..];
            var parsed = SourceQuery.Parse(suffix);
            if (parsed is null)
            {
                invalid = raw;
                return null;
            }
            sourceQuery = parsed;
        }
        return new SymbolComponents(set, sourceQuery);
    }
}

/// <summary>
/// How much of a declaration's source text <see cref="SymbolComponents.Source"/> renders, selected via
/// an <c>include</c> suffix (<c>"source:code"</c>).
/// </summary>
public enum SourceMode
{
    /// <summary>The declaration's full span, including its own leading <c>///</c> doc comment.</summary>
    Full,

    /// <summary>
    /// The same span minus the leading doc comment — attributes and the body are unchanged. Meant for a
    /// caller that only needs enough to modify the code, when the doc comment is redundant with a
    /// separately fetched <see cref="SymbolComponents.XmlDoc"/>.
    /// </summary>
    Code,
}
