namespace DotnetToolkit.McpServer.Output;

/// <summary>
/// Renders a symbol's retrieval shape — what its own surface is, what fetching it costs, and what that
/// fetch would contain — into the terse <c>P5 L16 O4 D9</c> column search_index puts beside a hit and
/// get_symbol puts on a member row.
/// </summary>
/// <remarks>
/// Every fact is reported at its measured value, and a letter is absent only when that fact is zero or
/// cannot apply to the symbol's kind. There is no threshold anywhere in here, deliberately: a shape is a
/// DESCRIPTION of the symbol, not an alarm that did or did not fire, and a caller cannot reason about a
/// column whose blank means "small" on one letter and "none" on the next. The earlier design gated
/// lines, members and comments while leaving docs ungated, so an absent <c>L</c> was unreadable without
/// first knowing which of the two policies governed it.
///
/// Which letters a kind can show is decided where the facts are gathered, not here — see
/// <see cref="ShapeFacts"/>. This type only orders and renders them, so there is no kind-to-letters
/// table to drift out of step with the gatherers.
///
/// The order is what-it-is, how-big, what-is-inside, what-is-attached: <c>P</c>/<c>M</c>/<c>N</c> name
/// the symbol's own surface, <c>L</c>/<c>O</c> say what fetching it costs, and <c>D</c>/<c>C</c>/<c>A</c>
/// say what that fetch would contain.
/// </remarks>
public static class SymbolShape
{
    /// <summary>Legend for the shape column, emitted once per response rather than repeated per row.</summary>
    /// <remarks>
    /// Names every letter <see cref="For"/> can emit, in the order it emits them, and states the one
    /// absence rule that replaced the old split between gated and ungated facts. A test asserts the
    /// legend and the renderer stay in step.
    /// </remarks>
    public const string Legend =
        "P=params M=members N=nested L=lines O=outline D=doclines C=commentlines A=attributes; absent=none";

    /// <summary>
    /// Builds the shape string for one symbol, or null when it has nothing at all to report — every fact
    /// either zero or inapplicable, which in practice means a hit whose location never resolved.
    /// </summary>
    /// <param name="facts">The counted facts to render; a null count is elided as inapplicable.</param>
    /// <returns><c>"P5 L16 O4 D9"</c>, any subset of it, or null when every part was elided.</returns>
    public static string? For(in ShapeFacts facts)
    {
        var parts = new List<string>(8);
        Add(parts, 'P', facts.ParameterCount);
        Add(parts, 'M', facts.MemberCount);
        Add(parts, 'N', facts.NestedCount);
        Add(parts, 'L', facts.LineCount);
        Add(parts, 'O', facts.LandmarkCount);
        Add(parts, 'D', facts.DocLines);
        Add(parts, 'C', facts.CommentLines);
        Add(parts, 'A', facts.AttributeCount);

        return parts.Count == 0 ? null : string.Join(' ', parts);

        static void Add(List<string> parts, char letter, int? count)
        {
            if (count is > 0)
                parts.Add($"{letter}{count}");
        }
    }
}
