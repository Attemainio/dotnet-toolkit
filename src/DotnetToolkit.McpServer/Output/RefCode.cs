using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Workspace;

namespace DotnetToolkit.McpServer.Output;

/// <summary>
/// Renders a symbol's reference counts into the terse <c>R7-E3-V1</c> column that sits beside a hit's
/// <see cref="SymbolShape"/>, answering "is anything using this, and does anything dispatch to it".
/// </summary>
/// <remarks>
/// Two absence rules operate here and they mean opposite things, which is the whole reason this type exists
/// rather than a <c>string.Join</c> at the call site:
///
/// <list type="bullet">
/// <item>The WHOLE code absent means nothing was measured — no reference index, or a project the edge cache
/// never covered. It is never a zero. <see cref="SymbolStore.ReferenceCountsFor"/> enforces the same
/// distinction by omitting an id rather than returning zeroes for it.</item>
/// <item>A single letter absent inside a present code means that fact cannot apply to this symbol's kind, or
/// is zero and not worth the three bytes. Which is which is decided by the rule below.</item>
/// </list>
///
/// <para>
/// Only the two counts whose ZERO is itself an answer are emitted at zero: <c>R</c> on a member ("nothing
/// calls this" is the dead-code verdict the column exists to give) and <c>I</c> on a named type ("nothing
/// implements this"). The rest appear only above zero, because <c>E0</c>/<c>V0</c>/<c>T0</c> restate what the
/// symbol's kind already said and would cost three bytes on every row to do it.
/// </para>
///
/// <para>
/// Letters are uppercase and carry digits; <see cref="ModifierCode"/>'s are lowercase and carry none, so the
/// two columns stay tellable apart at a glance without re-reading the legend. That case split is also what
/// lets each column reuse a letter the other has taken — <c>shape</c> already owns <c>O</c> for outline, and
/// forcing global uniqueness across all three would have cost every column its natural mnemonics.
/// </para>
/// </remarks>
public static class RefCode
{
    /// <summary>Legend for the refs column, emitted once per response rather than repeated per row.</summary>
    public const string Legend =
        "R=callers E=callees I=implementations(direct only) V=overrides T=tests; 0 shown where it is the answer, absent=not measured";

    /// <summary>
    /// Builds the refs code for one symbol, or null when it has nothing to report.
    /// </summary>
    /// <param name="counts">The measured counts. Reaching here at all means they WERE measured.</param>
    /// <param name="kind">The symbol's <see cref="SymbolKey.KindOf"/> word, which decides the letter set.</param>
    /// <returns><c>"R7-E3-V1"</c>, <c>"I0"</c>, any subset, or null when every part was elided.</returns>
    public static string? For(SymbolStore.RefCounts counts, string kind)
    {
        var parts = new List<string>(5);

        if (SymbolKey.IsNamedTypeKind(kind))
        {
            // A named type has no call sites of its own -- call edges bind to members -- so R/E/T would be
            // structurally zero rather than measured. Implementations is the relationship a type actually has.
            parts.Add($"I{counts.Implementations}");
        }
        else
        {
            parts.Add($"R{counts.Callers}");
            Add(parts, 'E', counts.Callees);
            Add(parts, 'I', counts.Implementations);
            Add(parts, 'V', counts.Overrides);
            Add(parts, 'T', counts.Tests);
        }

        return parts.Count == 0 ? null : string.Join('-', parts);

        static void Add(List<string> parts, char letter, int count)
        {
            if (count > 0)
                parts.Add($"{letter}{count}");
        }
    }
}
