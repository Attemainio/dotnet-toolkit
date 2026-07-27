namespace DotnetToolkit.McpServer.Contracts;

/// <summary>
/// A resolved <c>source</c> query: the base <see cref="SourceMode"/> plus whatever was subtracted from
/// it via <c>-tag</c> modifiers on the <c>include</c> string (e.g. <c>"source:full-remarks-attributes"</c>,
/// <c>"source:code-comments"</c>). There is no additive <c>+tag</c> form — a caller starts from a mode's
/// own default (everything, for <see cref="SourceMode.Full"/>; no doc-comment tags but attributes and
/// <c>//</c> comments still on, for <see cref="SourceMode.Code"/>) and only ever strips further.
/// </summary>
public sealed record SourceQuery(SourceMode Mode, IReadOnlySet<string> ExcludedTags, bool ExcludeAttributes, bool ExcludeComments)
{
    /// <summary><c>source:full</c> with no modifiers — today's unfiltered full-declaration text.</summary>
    public static readonly SourceQuery Full = new(SourceMode.Full, new HashSet<string>(), false, false);

    /// <summary><c>source:code</c> with no modifiers — today's doc-comment-stripped body text.</summary>
    public static readonly SourceQuery Code = new(SourceMode.Code, new HashSet<string>(), false, false);

    /// <summary>
    /// Modifier name (as written in <c>include</c>, matching <c>xmlDoc</c>'s own field names so there is
    /// no second vocabulary to learn) to the doc-comment element's actual XML local tag name.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> DocTagLocalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["summary"] = "summary",
        ["remarks"] = "remarks",
        ["returns"] = "returns",
        ["value"] = "value",
        ["inheritdoc"] = "inheritdoc",
        ["params"] = "param",
        ["typeParams"] = "typeparam",
        ["exceptions"] = "exception",
    };

    /// <summary>Every recognized modifier name, doc tags first — echoed in error detail text.</summary>
    public static readonly IReadOnlyList<string> ModifierNames = [.. DocTagLocalNames.Keys, "attributes", "comments"];

    /// <summary>
    /// The absolute file line ranges the caller narrowed <c>source</c> to via an <c>@</c> selector
    /// (<c>"source:code@46-76;79-83"</c>); empty when the whole declaration was asked for.
    /// </summary>
    /// <remarks>
    /// Applied as a pure filter over the same 1-based absolute line numbers <c>declarationSites</c>
    /// reports, so it commutes with the <c>-modifier</c> exclusions above and never renumbers a line —
    /// a span taken from any earlier response stays directly usable.
    /// </remarks>
    public IReadOnlyList<LineRange> Lines { get; init; } = [];

    /// <summary>
    /// Parses the text after <c>source:</c> (e.g. <c>"full-remarks-attributes"</c>, <c>"code@46-76"</c>)
    /// into a query, or null on anything unrecognized: an unknown mode, an unknown modifier name, a
    /// doc-tag modifier under <see cref="SourceMode.Code"/> — always redundant there since code already
    /// excludes every tag by default, so it is rejected rather than silently accepted as a no-op — or a
    /// malformed line range.
    /// </summary>
    /// <remarks>
    /// A suffix opening with <c>@</c> is the mode-less form a bare <c>"source@46-76"</c> produces, and
    /// means <see cref="SourceMode.Full"/> with that selection.
    /// </remarks>
    public static SourceQuery? Parse(string suffix)
    {
        var at = suffix.IndexOf('@');
        var head = at < 0 ? suffix : suffix[..at];

        SourceMode mode;
        if (head.Length == 0)
        {
            // Legal only as the mode-less "@ranges" form; a bare "source:" still names nothing at all.
            if (at < 0)
                return null;
            mode = SourceMode.Full;
        }
        else if (head.StartsWith("full", StringComparison.OrdinalIgnoreCase))
            mode = SourceMode.Full;
        else if (head.StartsWith("code", StringComparison.OrdinalIgnoreCase))
            mode = SourceMode.Code;
        else
            return null;

        IReadOnlyList<LineRange> lines = [];
        if (at >= 0)
        {
            if (ParseRanges(suffix[(at + 1)..]) is not { } parsed)
                return null;
            lines = parsed;
        }

        var rest = head.Length == 0 ? "" : head[4..];
        if (rest.Length == 0)
        {
            var bare = mode == SourceMode.Full ? Full : Code;
            return lines.Count == 0 ? bare : bare with { Lines = lines };
        }
        if (rest[0] != '-')
            return null;

        var excludedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludeAttributes = false;
        var excludeComments = false;
        foreach (var token in rest.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token, "attributes", StringComparison.OrdinalIgnoreCase))
            {
                excludeAttributes = true;
                continue;
            }
            if (string.Equals(token, "comments", StringComparison.OrdinalIgnoreCase))
            {
                excludeComments = true;
                continue;
            }

            var match = DocTagLocalNames.Keys.FirstOrDefault(k => string.Equals(k, token, StringComparison.OrdinalIgnoreCase));
            if (match is null || mode == SourceMode.Code)
                return null;
            excludedTags.Add(DocTagLocalNames[match]);
        }

        return new SourceQuery(mode, excludedTags, excludeAttributes, excludeComments) { Lines = lines };
    }

    /// <summary>
    /// Parses the text after <c>@</c> into line ranges, or null if any token is malformed or the whole
    /// selector is empty.
    /// </summary>
    /// <remarks>
    /// Ranges are separated by <c>;</c> rather than <c>,</c> because <c>include</c> is itself
    /// comma-split into component names before this ever runs, so a comma here would never survive to
    /// be parsed. A token is <c>N</c>, <c>N-M</c>, <c>N-</c> (to the declaration's last line) or
    /// <c>-M</c> (from its first).
    /// </remarks>
    private static IReadOnlyList<LineRange>? ParseRanges(string spec)
    {
        var ranges = new List<LineRange>();
        foreach (var token in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = token.IndexOf('-');
            if (dash < 0)
            {
                if (!TryParseLine(token, out var single))
                    return null;
                ranges.Add(new LineRange(single, single));
                continue;
            }

            int? start = null;
            int? end = null;
            if (dash > 0)
            {
                if (!TryParseLine(token[..dash], out var from))
                    return null;
                start = from;
            }
            if (dash < token.Length - 1)
            {
                if (!TryParseLine(token[(dash + 1)..], out var to))
                    return null;
                end = to;
            }

            // A bare "-" bounds nothing, which is the whole declaration written the long way — rejected
            // rather than accepted, since it is far likelier to be a typo than a deliberate no-op.
            if (start is null && end is null)
                return null;
            if (start is { } lower && end is { } upper && upper < lower)
                return null;
            ranges.Add(new LineRange(start, end));
        }

        return ranges.Count == 0 ? null : ranges;
    }

    private static bool TryParseLine(string text, out int line) => int.TryParse(text, out line) && line >= 1;
}

/// <summary>
/// One inclusive line range of a <c>source</c> selection, in 1-based absolute file lines.
/// </summary>
/// <remarks>
/// A null bound means the declaration's own bound rather than an unbounded one: <c>60-</c> runs to its
/// last line, <c>-50</c> from its first. Both null is never constructed — see
/// <see cref="SourceQuery.Parse"/>.
/// </remarks>
/// <param name="Start">First line to keep, or null for the declaration's first line.</param>
/// <param name="End">Last line to keep, or null for the declaration's last line.</param>
public sealed record LineRange(int? Start, int? End)
{
    /// <summary>Whether <paramref name="line"/> falls inside this range.</summary>
    /// <param name="line">A 1-based absolute file line number.</param>
    /// <returns>True when the line is at or after <see cref="Start"/> and at or before <see cref="End"/>, treating a null bound as unbounded on that side.</returns>
    public bool Contains(int line) => (Start is null || line >= Start) && (End is null || line <= End);
}
