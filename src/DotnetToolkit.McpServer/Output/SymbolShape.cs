namespace DotnetToolkit.McpServer.Output;

/// <summary>
/// Renders a search-index hit's retrieval shape — how long it is, how many members it declares, and how
/// many lines of docs and comments it carries — into the terse <c>L1822 M64 D6 C214</c> column
/// search_index puts beside the hit.
/// </summary>
/// <remarks>
/// The four facts are reported under two different policies, for one reason.
///
/// <c>L</c> and <c>M</c> are THRESHOLD-GATED, emitted only once the symbol is big enough that fetching
/// it whole is the wrong next call. <c>L</c> is recoverable by arithmetic on the line/endLine the hit
/// already carries, so printing it always would restate a subtraction the caller can do; printing it on
/// the few rows where that subtraction changes the next call is what earns its place. Their thresholds
/// are retrieval decisions, not style preferences: above them, <c>get_symbol</c> is cheaper called with
/// <c>include:"members"</c> or a <c>source:code@from-to</c> range than with the default whole fetch.
///
/// <c>D</c> and <c>C</c> are UNCONDITIONAL, elided only at zero. Neither is derivable from anything else
/// in the response, so gating them would leave "undocumented" and "not measured" indistinguishable — the
/// ambiguity <c>L</c> does not pay because arithmetic recovers it. They are data, not alarms: they say
/// what a fetch would contain, so a caller can reach for <c>source:code-comments</c> or
/// <c>source:code</c> on evidence rather than on a guess.
/// </remarks>
public static class SymbolShape
{
    /// <summary>Line count at or above which fetching the symbol whole is worth reconsidering.</summary>
    public const int LineThreshold = 150;

    /// <summary>Declared-member count at or above which the member list is the better way in.</summary>
    public const int MemberThreshold = 20;

    /// <summary>Legend for the shape column, emitted once per response rather than per row.</summary>
    /// <remarks>
    /// States the two policies, because a blank means different things under each: no <c>L</c>/<c>M</c>
    /// is "below the threshold", while no <c>D</c>/<c>C</c> is a measured zero. The parenthesized numbers
    /// mirror <see cref="LineThreshold"/> and <see cref="MemberThreshold"/>, which const interpolation
    /// cannot express over an int — a test asserts they stay in step.
    /// </remarks>
    public const string Legend = "L=lines(150+) M=members(20+) D=doclines C=commentlines; D/C absent = zero";

    /// <summary>
    /// Builds the shape string for one hit, or null when it has nothing to report: a symbol under both
    /// thresholds that carries neither docs nor comments.
    /// </summary>
    /// <param name="line">The declaration's first line, or null for a hit whose site did not resolve.</param>
    /// <param name="endLine">The declaration's last line, or null as for <paramref name="line"/>.</param>
    /// <param name="memberCount">Members the symbol declares; null for anything but a type.</param>
    /// <param name="docLines">Lines of XML doc comment on the declaration; 0 for none.</param>
    /// <param name="commentLines">Lines of non-doc comment; on a type, the transitive total.</param>
    /// <returns><c>"L1822 M64 D6 C214"</c>, any subset of it, or null when every part was elided.</returns>
    public static string? For(int? line, int? endLine, int? memberCount, int docLines, int commentLines)
    {
        var lines = line is { } start && endLine is { } end && end >= start ? end - start + 1 : (int?)null;

        var parts = new List<string>(4);
        if (lines >= LineThreshold)
            parts.Add($"L{lines}");
        if (memberCount >= MemberThreshold)
            parts.Add($"M{memberCount}");
        if (docLines > 0)
            parts.Add($"D{docLines}");
        if (commentLines > 0)
            parts.Add($"C{commentLines}");

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }
}
