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

    /// <summary>
    /// Directories a segment would search for <c>.cs</c> content <b>without naming a single file</b>.
    /// </summary>
    /// <param name="segment">One segment from <see cref="Segments"/>.</param>
    /// <param name="recursesByDefault">
    /// True for a command that walks a tree with no flag asking it to (<c>rg</c>, <c>ag</c>).
    /// </param>
    /// <returns>
    /// The candidate directories, or empty when the segment neither recurses nor carries a <c>.cs</c>
    /// glob. A recursing segment with no path operand yields <c>"."</c>, which is what it searches.
    /// </returns>
    /// <remarks>
    /// <see cref="FindCsArgument"/> answers only for a segment that names one existing file, which left
    /// the broader read unguarded: <c>grep -rn "x" --include=*.cs src/</c> reads every compiled file in the
    /// tree and names none of them. Its only <c>.cs</c> token sits inside an option flag, so the flag skip
    /// in <see cref="FindCsArgument"/> discarded it, and the bare operand was a directory. The unguarded
    /// form therefore read strictly MORE than the guarded one.
    /// <para>
    /// The first bare operand is the PATTERN for every command on the read blocklist that takes one, so
    /// paths start at the second. That is wrong for <c>grep -e PATTERN dir</c>, where the pattern is a flag
    /// value and the first bare operand is already a path — it over-collects a directory there rather than
    /// missing one, and the membership check that follows is what decides.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> CsScanRoots(string segment, bool recursesByDefault)
    {
        var recurses = recursesByDefault;
        var csGlob = false;
        var nonCsFilter = false;
        var operands = new List<string>();

        var tokens = Tokenize(segment).Skip(1).ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith('-'))
            {
                operands.Add(token);
                continue;
            }

            // A file filter carries its value either as --include=*.cs or as two tokens (rg -g '*.md').
            // Consuming the second form here is what keeps the pattern out of the path operands below,
            // where it would otherwise be mistaken for a directory to scan.
            var equals = token.IndexOf('=');
            string? name = null;
            string? value = null;
            if (equals > 0)
            {
                name = token[..equals];
                value = token[(equals + 1)..];
            }
            else if (IsFileFilterFlag(token) && i + 1 < tokens.Count)
            {
                name = token;
                value = tokens[++i];
            }

            if (value is not null)
            {
                if (value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    csGlob = true;
                else if (IsFileFilterFlag(name!))
                    nonCsFilter = true;
                continue;
            }

            if (token is "--recursive" or "--dereference-recursive")
                recurses = true;
            else if (!token.StartsWith("--", StringComparison.Ordinal) && token.Skip(1).Any(c => c is 'r' or 'R'))
                recurses = true;
        }

        var paths = operands.Count > 1 ? operands[1..] : [];
        foreach (var path in paths)
        {
            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && IsGlob(path))
            {
                csGlob = true;
            }
        }

        // A filename filter naming something other than *.cs means the walk cannot open a compiled
        // source file at all, however deep it recurses -- `grep -rn --include=*.md .` reads no C#.
        // Blocking it was a pure false positive, and one that reads as the guard malfunctioning rather
        // than protecting anything, since the command it rejected never touched the files it named.
        if (nonCsFilter && !csGlob)
        {
            return [];
        }

        if (!recurses && !csGlob)
        {
            return [];
        }

        var roots = new List<string>();
        foreach (var path in paths)
        {
            var directory = IsGlob(path) ? Path.GetDirectoryName(path) ?? "." : path;
            roots.Add(directory.Length == 0 ? "." : directory);
        }

        return roots.Count > 0 ? roots : ["."];
    }

    /// <summary>Whether a long flag selects WHICH FILES a tree search opens, e.g. grep's --include.</summary>
    /// <param name="flag">The flag name, without its value.</param>
    /// <returns>True for the include-style filters; exclude-style flags are deliberately not listed.</returns>
    /// <remarks>
    /// Only include-style filters count. An <c>--exclude=*.cs</c> would narrow the walk AWAY from C#, but
    /// reading it that way means trusting a flag to prove a negative; leaving it out keeps the guard
    /// erring toward blocking, which is the safe direction for a walk that would otherwise read source.
    /// </remarks>
    private static bool IsFileFilterFlag(string flag) =>
        flag is "--include" or "--glob" or "--iglob" or "-g";

    /// <summary>Whether a token carries a shell wildcard rather than being a literal path.</summary>
    /// <param name="token">One tokenized argument.</param>
    /// <returns>True when the token contains <c>*</c> or <c>?</c>.</returns>
    private static bool IsGlob(string token) => token.Contains('*') || token.Contains('?');

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
