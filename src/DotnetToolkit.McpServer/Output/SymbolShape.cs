namespace DotnetToolkit.McpServer.Output;

/// <summary>
/// Renders a search-index hit's retrieval shape — how long it is, how many members it declares, and how
/// many lines of docs and comments it carries — into the terse <c>L1822 M64 D6 C214</c> column
/// search_index puts beside the hit.
/// </summary>
/// <remarks>
/// The four facts are reported under two different policies, for one reason.
///
/// <c>L</c>, <c>M</c> and <c>C</c> are THRESHOLD-GATED, emitted only once the symbol is big enough that
/// the label changes the next call. <c>L</c> is recoverable by arithmetic on the line/endLine the hit
/// already carries, so printing it always would restate a subtraction the caller can do; printing it on
/// the few rows where that subtraction changes the next call is what earns its place. The thresholds are
/// retrieval decisions, not style preferences: above them, <c>get_symbol</c> is cheaper called with
/// <c>include:"members"</c>, a <c>source:code@from-to</c> range or <c>source:code-comments</c> than with
/// the default whole fetch. <c>C</c>'s threshold is the highest bar of the three because acting on it
/// COSTS the caller something — the comments themselves — so a rounding error is not enough; measured
/// against a real repository it fired on a quarter of all hits to save 1.19x, which is not that trade.
///
/// <c>D</c> alone is UNCONDITIONAL, elided only at zero. It is not derivable from anything else in the
/// response, and it is the label that most reliably pays: a modest 1.6x across the great majority of
/// hits, with nothing lost, since <c>source:code</c> drops only a doc comment <c>xmlDoc</c> serves more
/// cheaply anyway. So a blank <c>D</c> is a measured zero, while a blank <c>L</c>/<c>M</c>/<c>C</c> only
/// ever means "below the threshold".
/// </remarks>
public static class SymbolShape
{
    /// <summary>Line count at or above which fetching the symbol whole is worth reconsidering.</summary>
    public const int LineThreshold = 150;

    /// <summary>Declared-member count at or above which the member list is the better way in.</summary>
    public const int MemberThreshold = 20;

    /// <summary>Comment-line count at or above which dropping the comments is worth what it loses.</summary>
    public const int CommentThreshold = 10;

    /// <summary>Legend for the shape column, emitted once per response rather than per row.</summary>
    /// <remarks>
    /// States both policies, because a blank means different things under each: no
    /// <c>L</c>/<c>M</c>/<c>C</c> is "below the threshold", while no <c>D</c> is a measured zero. The
    /// parenthesized numbers mirror <see cref="LineThreshold"/>, <see cref="MemberThreshold"/> and
    /// <see cref="CommentThreshold"/>, which const interpolation cannot express over an int — a test
    /// asserts they stay in step.
    /// </remarks>
    public const string Legend = "L=lines(150+) M=members(20+) D=doclines C=commentlines(10+); D absent = zero";

    /// <summary>
    /// Builds the shape string for one hit, or null when it has nothing to report: a symbol under every
    /// threshold that carries no docs.
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
        if (commentLines >= CommentThreshold)
            parts.Add($"C{commentLines}");

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }
}
