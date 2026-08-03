using System.Text;

namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// Splits a Bash command line into candidate invocations and pulls the file each one reads.
/// </summary>
/// <remarks>
/// Deliberately not a shell parser. It splits on pipeline/statement separators (<c>|</c>, <c>;</c>,
/// <c>&amp;</c>, and therefore <c>&amp;&amp;</c>/<c>||</c>) <b>that are not inside quotes or
/// backslash-escaped</b>, takes each segment's first word as the invoked command, and looks for a
/// <c>.cs</c>-suffixed argument among that segment's tokens.
/// <para>
/// The quote awareness is the whole point. A naive split on every separator character broke the most
/// common grep idiom in this repo into a silent bypass:
/// <c>grep -n "Alpha\|Beta\|Gamma" src/Foo.cs | head</c> split into segments where the one carrying
/// the <c>.cs</c> path started with <c>Gamma"</c> — not a read utility — while the segment starting
/// with <c>grep</c> carried no <c>.cs</c> token. Neither half of the check fired, so every multi-term
/// grep over compiled C# went unguarded.
/// </para>
/// <para>
/// A path with embedded whitespace inside its quotes is still not reconstructable by
/// whitespace-based tokenization and stays unrecognized. That narrower under-detection is deliberate:
/// this is a workflow guard with a fail-open posture, not a security boundary that has to be airtight.
/// </para>
/// </remarks>
internal static class BashCommandScanner
{
    /// <summary>Splits a command line into one candidate invocation per separator-delimited segment.</summary>
    /// <param name="command">The raw command line from <c>tool_input.command</c>.</param>
    /// <returns>The non-empty segments, in order.</returns>
    public static IReadOnlyList<string> Segments(string command)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var escaped = false;

        foreach (var c in command)
        {
            if (escaped)
            {
                current.Append(c);
                escaped = false;
                continue;
            }

            if (quote == '\'')
            {
                // Backslash is an escape everywhere except inside single quotes, matching the shell.
                if (c == '\'')
                {
                    quote = null;
                }

                current.Append(c);
                continue;
            }

            if (c == '\\')
            {
                current.Append(c);
                escaped = true;
                continue;
            }

            if (quote == '"')
            {
                if (c == '"')
                {
                    quote = null;
                }

                current.Append(c);
                continue;
            }

            switch (c)
            {
                case '"':
                case '\'':
                    quote = c;
                    current.Append(c);
                    break;
                case '|':
                case ';':
                case '&':
                    Flush(segments, current);
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        Flush(segments, current);
        return segments;
    }

    /// <summary>The bare name of the command a segment invokes, with any path and <c>.exe</c> removed.</summary>
    /// <param name="segment">One segment from <see cref="Segments"/>.</param>
    /// <returns>
    /// The command name — <c>cat</c> for both <c>/usr/bin/cat</c> and <c>C:\tools\cat.exe</c> — or null
    /// when the segment has no first word.
    /// </returns>
    public static string? CommandName(string segment)
    {
        var first = Tokenize(segment).FirstOrDefault();
        if (string.IsNullOrEmpty(first))
        {
            return null;
        }

        var name = first.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>The last <c>.cs</c>-suffixed argument in a segment, ignoring option flags.</summary>
    /// <param name="segment">One segment from <see cref="Segments"/>.</param>
    /// <returns>The path token as written, or null when the segment names no <c>.cs</c> file.</returns>
    public static string? FindCsArgument(string segment)
    {
        string? candidate = null;
        foreach (var token in Tokenize(segment))
        {
            if (token.StartsWith('-'))
            {
                continue;
            }

            if (token.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                candidate = token;
            }
        }

        return candidate;
    }

    /// <summary>Splits a segment on whitespace and strips one surrounding pair of quotes per token.</summary>
    /// <param name="segment">One segment from <see cref="Segments"/>.</param>
    /// <returns>The segment's tokens, unquoted.</returns>
    /// <remarks>
    /// Tokens arrive already expanded, so a path written as <c>"path/Foo.cs"</c> still carries its
    /// quote characters; left on, the trailing <c>"</c> makes the <c>.cs</c> suffix test fail and the
    /// read slips through.
    /// </remarks>
    private static IEnumerable<string> Tokenize(string segment)
    {
        foreach (var raw in segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw;
            if (token.Length >= 2
                && (token[0] == '"' || token[0] == '\'')
                && token[^1] == token[0])
            {
                token = token[1..^1];
            }

            yield return token;
        }
    }

    private static void Flush(List<string> segments, StringBuilder current)
    {
        var segment = current.ToString().Trim();
        if (segment.Length > 0)
        {
            segments.Add(segment);
        }

        current.Clear();
    }
}
