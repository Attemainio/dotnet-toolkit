using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotnetToolkit.McpServer.Devlog;

/// <summary>
/// Parses weekly devlog markdown files. Tolerates hand-edited or merged files:
/// entries without a metadata comment get a stable fallback id derived from the file
/// and heading position, and the date is taken from the heading.
/// </summary>
public static partial class DevlogParser
{
    public static List<DevlogEntry> ParseFile(string absPath, string relPath)
    {
        var text = File.ReadAllText(absPath);
        var entries = new List<DevlogEntry>();
        var matches = HeadingRegex().Matches(text);

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var block = text[start..end].TrimEnd();

            var headingLine = block[..block.IndexOfAny(['\r', '\n'])].TrimStart('#', ' ');
            var (datePart, title) = SplitHeading(headingLine);

            DevlogMeta? meta = null;
            var metaMatch = MetaRegex().Match(block);
            if (metaMatch.Success)
            {
                try
                {
                    meta = JsonSerializer.Deserialize<DevlogMeta>(metaMatch.Groups[1].Value);
                }
                catch (JsonException)
                {
                    // Malformed hand-edited metadata: index by heading instead.
                }
            }

            var ts = meta?.Ts
                ?? (DateTimeOffset.TryParse(datePart, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
                    ? parsed
                    : new DateTimeOffset(File.GetLastWriteTimeUtc(absPath), TimeSpan.Zero));
            var id = meta?.Id ?? $"{Path.GetFileNameWithoutExtension(relPath)}#{i + 1}";

            entries.Add(new DevlogEntry(
                id, ts, title,
                meta?.Status ?? "done",
                meta?.Classes ?? [],
                meta?.Ensemble,
                meta?.Tags ?? [],
                relPath,
                block));
        }

        return entries;
    }

    private static (string DatePart, string Title) SplitHeading(string heading)
    {
        foreach (var separator in new[] { " — ", " - " })
        {
            var idx = heading.IndexOf(separator, StringComparison.Ordinal);
            if (idx > 0)
                return (heading[..idx].Trim(), heading[(idx + separator.Length)..].Trim());
        }
        return ("", heading.Trim());
    }

    /// <summary>Entry body without the metadata comment (used for search terms).</summary>
    public static string StripMeta(string markdown) => MetaRegex().Replace(markdown, "");

    [GeneratedRegex(@"^##\s", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"<!--\s*devlog\s*(\{.*?\})\s*-->")]
    private static partial Regex MetaRegex();
}
