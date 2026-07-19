using System.Text;

namespace DotnetToolkit.McpServer.Store;

/// <summary>
/// Turns a symbol name into the token soup FTS5 indexes, and a user query into the matching FTS
/// expression. Both sides must split the same way or a query cannot find what indexing stored.
/// </summary>
public static class SearchText
{
    /// <summary>
    /// Expands a fully-qualified name into every token a caller might plausibly search for:
    /// the dotted segments as written, plus their camel-case parts.
    /// <c>PandaAI.Core.Strategy.FIFOLedger.TryBuy</c> yields
    /// <c>PandaAI Core Strategy FIFOLedger TryBuy FIFO Ledger Try Buy</c>, so that a search for
    /// "Ledger" finds <c>FIFOLedger</c> — which a tokenizer that only splits on punctuation cannot do.
    /// </summary>
    public static string ForIndex(string fqName)
    {
        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in Segments(fqName))
        {
            if (seen.Add(segment))
                tokens.Add(segment);
            foreach (var part in CamelParts(segment))
            {
                if (seen.Add(part))
                    tokens.Add(part);
            }
        }
        return string.Join(' ', tokens);
    }

    /// <summary>
    /// Builds the FTS5 MATCH expression for a user query. Terms are OR-ed, not AND-ed: a caller
    /// asking for "Ledger TryBuy TrySell" wants the symbols matching any of those, and AND would
    /// return nothing because no single symbol carries all three. Ranking (bm25) then floats the
    /// rows matching more terms to the top, which is the behaviour AND was reaching for.
    /// Returns null when the query has no usable term.
    /// </summary>
    public static string? ForQuery(string query)
    {
        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in Segments(query))
        {
            foreach (var candidate in new[] { segment }.Concat(CamelParts(segment)))
            {
                if (candidate.Length < 2 || !seen.Add(candidate))
                    continue;
                // Prefix match, so "Ledg" still finds "Ledger". Quoted to keep FTS5 from reading a
                // term as one of its operators (NOT, OR, NEAR) or choking on a stray character.
                terms.Add($"\"{candidate.Replace("\"", "\"\"")}\"*");
            }
        }
        return terms.Count == 0 ? null : string.Join(" OR ", terms);
    }

    /// <summary>Splits on anything that is not a letter or digit: dots, parens, commas, angle brackets.</summary>
    private static IEnumerable<string> Segments(string input)
    {
        var current = new StringBuilder();
        foreach (var c in input)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }
        if (current.Length > 0)
            yield return current.ToString();
    }

    /// <summary>
    /// Camel-case parts of one segment. Handles acronym runs, so <c>FIFOLedger</c> splits as
    /// FIFO + Ledger rather than F + I + F + O + Ledger. Returns nothing when the segment has no
    /// internal boundary, since the whole segment is already indexed.
    /// </summary>
    private static IEnumerable<string> CamelParts(string segment)
    {
        if (segment.Length < 2)
            yield break;

        var start = 0;
        for (var i = 1; i < segment.Length; i++)
        {
            // Boundary when a lower/digit is followed by an upper (tryBuy -> try|Buy), or when an
            // acronym run ends because the next char is lower (FIFOLedger -> FIFO|Ledger).
            var endsWord = char.IsUpper(segment[i]) && !char.IsUpper(segment[i - 1]);
            var endsAcronym = char.IsUpper(segment[i]) && char.IsUpper(segment[i - 1])
                              && i + 1 < segment.Length && char.IsLower(segment[i + 1]);
            if (!endsWord && !endsAcronym)
                continue;
            if (i > start)
                yield return segment[start..i];
            start = i;
        }

        // Only worth emitting the tail if we actually split somewhere.
        if (start > 0 && start < segment.Length)
            yield return segment[start..];
    }
}
