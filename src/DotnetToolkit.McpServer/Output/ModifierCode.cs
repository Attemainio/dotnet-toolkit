using System.Text;

namespace DotnetToolkit.McpServer.Output;

/// <summary>
/// Renders a symbol's modifiers into the terse <c>ps</c> column that sits beside a hit's
/// <see cref="SymbolShape"/> and <see cref="RefCode"/> — "is this public, is it static, can it be overridden".
/// </summary>
/// <remarks>
/// Free at query time: the tags come from the <c>modifiers</c> column the symbol index already stores for
/// search_index's modifiers FILTER, so rendering them costs no extra lookup and no Roslyn call. This type only
/// maps that stored text to letters.
///
/// <para>
/// Lowercase, and no digits, which is what separates this column from <see cref="SymbolShape"/> and
/// <see cref="RefCode"/> at a glance. That case split is deliberate: it lets each column keep the natural
/// first-letter mnemonic for its own vocabulary instead of surrendering it to whichever column claimed the
/// letter first. <c>p</c> here is public; <c>P</c> in a shape column is a parameter count, and the two can
/// never be confused because one carries a digit and the other never does.
/// </para>
///
/// <para>
/// Not every C# modifier earns a letter. The set below is what changes how a hit is READ — its visibility, its
/// dispatch, and whether calling it needs an await. <c>volatile</c>, <c>extern</c>, <c>indexer</c> and friends
/// stay filterable through the modifiers argument without spending a byte on every row that lacks them.
/// </para>
/// </remarks>
public static class ModifierCode
{
    /// <summary>Legend for the modifiers column, emitted once per response rather than repeated per row.</summary>
    public const string Legend =
        "p=public i=internal t=protected x=private s=static a=abstract v=virtual o=override y=async l=sealed g=partial c=const d=readonly e=extension";

    /// <summary>
    /// Tag-to-letter map, in the order letters are emitted: accessibility first (a symbol has exactly one),
    /// then dispatch, then the rest. Ordering here rather than at the call site is what keeps two symbols with
    /// the same modifiers from rendering as two different strings.
    /// </summary>
    private static readonly (string Tag, char Letter)[] Map =
    [
        ("public", 'p'), ("internal", 'i'), ("protected", 't'), ("private", 'x'),
        ("static", 's'), ("abstract", 'a'), ("virtual", 'v'), ("override", 'o'),
        ("async", 'y'), ("sealed", 'l'), ("partial", 'g'), ("const", 'c'),
        ("readonly", 'd'), ("extension", 'e'),
    ];

    /// <summary>Builds the modifier code for one symbol, or null when none of the mapped tags apply.</summary>
    /// <param name="tags">
    /// The space-joined tag text stored on the symbol row (<c>ModifierText.Tags</c>), already lowercased.
    /// </param>
    /// <returns><c>"ps"</c>, <c>"xsy"</c>, any subset, or null when the symbol carries none of them.</returns>
    public static string? For(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
            return null;

        var present = tags.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var code = new StringBuilder(4);
        foreach (var (tag, letter) in Map)
        {
            if (present.Contains(tag))
                code.Append(letter);
        }

        return code.Length == 0 ? null : code.ToString();
    }
}
