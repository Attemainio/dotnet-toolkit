using System.Text.RegularExpressions;

namespace DotnetToolkit.McpServer.Devlog;

/// <summary>Tokenization and TF scoring for devlog search (deliberately no external search library).</summary>
public static partial class DevlogSearch
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "of", "to", "in", "for", "on", "with",
        "is", "are", "was", "were", "it", "this", "that", "we", "not", "by",
        "as", "at", "be", "from", "but", "into", "than", "then", "so", "no",
        "what", "why", "how", "when", "all", "its", "our", "have", "has",
    };

    public static Dictionary<string, int> BuildTerms(string title, string body)
    {
        var terms = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in Tokenize(title + " " + body))
            terms[token] = terms.GetValueOrDefault(token) + 1;
        return terms;
    }

    public static IEnumerable<string> Tokenize(string text) =>
        TokenRegex().Matches(text)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(t => t.Length >= 2 && !Stopwords.Contains(t));

    public static double Score(DevlogIndexEntry entry, IReadOnlyList<string> queryTokens)
    {
        double score = 0;
        foreach (var token in queryTokens)
        {
            score += entry.Terms.GetValueOrDefault(token);
            if (entry.Title.Contains(token, StringComparison.OrdinalIgnoreCase))
                score += 3;
            if (entry.Classes.Any(c => c.Equals(token, StringComparison.OrdinalIgnoreCase)))
                score += 5;
            if (entry.Tags.Any(t => t.Equals(token, StringComparison.OrdinalIgnoreCase)))
                score += 4;
        }
        return score;
    }

    [GeneratedRegex(@"[A-Za-z0-9_]+")]
    private static partial Regex TokenRegex();
}
