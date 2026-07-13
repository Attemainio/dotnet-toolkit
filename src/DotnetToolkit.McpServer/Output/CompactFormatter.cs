using System.Text;

namespace DotnetToolkit.McpServer.Output;

/// <summary>Renders tool results in the token-minimal compact text format.</summary>
public static class CompactFormatter
{
    /// <summary>
    /// Pipe-delimited table: "label(shown/total) col1|col2" header, one row per line,
    /// and a truncation marker when total exceeds the shown rows.
    /// </summary>
    public static string Table(string label, string[] header, IReadOnlyList<string[]> rows, int total)
    {
        var sb = new StringBuilder();
        sb.Append(label).Append('(').Append(rows.Count).Append('/').Append(total).Append(") ");
        sb.Append(string.Join('|', header)).Append('\n');
        foreach (var row in rows)
            sb.Append(string.Join('|', row)).Append('\n');
        if (total > rows.Count)
            sb.Append("…+").Append(total - rows.Count).Append(" more (raise limit)\n");
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>First sentence of a doc summary, truncated to maxLen.</summary>
    public static string? FirstSentence(string? text, int maxLen = 120)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var t = text.Trim();
        var cut = t.IndexOf(". ", StringComparison.Ordinal);
        if (cut > 0)
            t = t[..(cut + 1)];
        return Truncate(t, maxLen);
    }

    public static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..(maxLen - 1)] + "…";
}
