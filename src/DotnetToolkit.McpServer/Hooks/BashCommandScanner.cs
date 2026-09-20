using System.Text;
using System.Text.RegularExpressions;

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
internal static partial class BashCommandScanner
{
    /// <summary>Interpreters that reach a file from inside an inline program rather than through an argument.</summary>
    /// <remarks>
    /// These never name the file as an argument, so every argument-shaped check in this scanner misses
    /// them entirely: <c>python3 -c "open('Foo.cs').read()"</c> tokenizes into one blob that does not end
    /// in <c>.cs</c>. They are handled by looking inside the program text instead, which is a heuristic
    /// and is used as one — advisory on the read side, and on the write side only alongside an explicit
    /// write mode.
    /// </remarks>
    private static readonly HashSet<string> ScriptInterpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "python", "python3", "py", "perl", "ruby", "node", "deno", "pwsh", "powershell", "php",
    };

    /// <summary>Text that means an inline program is opening its file for writing rather than reading.</summary>
    /// <remarks>
    /// Required before the write guard denies a script one-liner. Without it, a read-only
    /// <c>python3 -c "print(open('Foo.cs').read())"</c> would be denied as a write — a false block on the
    /// exact command the read guard is meant to merely advise on.
    /// </remarks>
    private static readonly string[] ScriptWriteMarkers =
    [
        "'w'", "\"w\"", "'a'", "\"a\"", "'w+'", "\"w+\"", "'a+'", "\"a+\"", "'wb'", "\"wb\"",
        "write_text", "writetext", "writealltext", "writelines", "writefilesync", "set-content",
        "out-file", "add-content",
    ];

    /// <summary>Matches a <c>.cs</c> path appearing anywhere in a segment, including inside quotes.</summary>
    [GeneratedRegex(@"(?<path>[\w.+/\\-]+\.cs)\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CsLiteral();

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

    /// <summary>The directory a <c>cd</c> segment moves to, or null when the segment is not a <c>cd</c>.</summary>
    /// <param name="segment">One segment from <see cref="Segments"/>.</param>
    /// <returns>The target as written, or null when the segment is not a cd or names no target.</returns>
    /// <remarks>
    /// Segments are scanned independently, so without this a later segment's relative path resolves against
    /// the hook process's own directory rather than the one the command actually runs in -- which is how a
    /// search of an unrelated repository got attributed to this one.
    /// </remarks>
    public static string? CdTarget(string segment)
    {
        if (!string.Equals(CommandName(segment), "cd", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var token in Tokenize(segment).Skip(1))
        {
            if (!token.StartsWith('-'))
            {
                return token;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a segment reads a bounded line range of a file rather than the whole of it: <c>sed -n</c>
    /// with a numeric address (<c>12,40p</c>, <c>12p</c>), <c>head</c> with a count (<c>-n 40</c>,
    /// <c>-40</c>), or <c>tail</c> with one (<c>-n +12</c>).
    /// </summary>
    /// <param name="segment">One segment from <see cref="Segments"/>.</param>
    /// <returns>True for a ranged read; false for anything else, including a bare <c>head</c> or a <c>sed</c> without <c>-n</c>.</returns>
    /// <remarks>
    /// A ranged read of a located symbol is the intended way to fetch its text, the same as a
    /// <c>Read</c> with <c>offset</c>/<c>limit</c>, so the read guard lets it through silently. A bare
    /// <c>head</c> defaults to ten lines and is left to the advisory: it is almost always "show me the
    /// start of this file", which is the whole-file question the note exists for.
    /// </remarks>
    public static bool IsRangedRead(string segment)
    {
        var name = CommandName(segment);
        var tokens = TokenizeQuoteAware(segment).Skip(1).Select(t => t.Value).ToList();
        switch (name?.ToLowerInvariant())
        {
            case "sed":
                return tokens.Any(t => t == "-n" || (t.StartsWith('-') && !t.StartsWith("--", StringComparison.Ordinal) && t.Contains('n')))
                    && tokens.Any(t => SedRange().IsMatch(t));
            case "head":
            case "tail":
                for (var i = 0; i < tokens.Count; i++)
                {
                    if (tokens[i] is "-n" or "-c" && i + 1 < tokens.Count && LineCount().IsMatch(tokens[i + 1]))
                        return true;
                    if (LineCountFlag().IsMatch(tokens[i]))
                        return true;
                }
                return false;
            default:
                return false;
        }
    }

    // One numeric address (12p, 12,40p) or several joined by ';' (85,97p;150,167p) -- a multi-range read
    // is still bounded by its addresses, and the advisory once fired on that form while the meter, which
    // shares nothing with this regex, correctly did not count it (FluidScript, 2026-09-20).
    [GeneratedRegex(@"^\d+(,\d+)?p(;\d+(,\d+)?p)*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SedRange();

    [GeneratedRegex(@"^\+?\d+$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LineCount();

    [GeneratedRegex(@"^-(n\+?\d+|\+?\d+)$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LineCountFlag();

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

    /// <summary>The <c>.cs</c> path an inline program names, when the segment runs one.</summary>
    /// <param name="segment">One segment from <see cref="Segments"/>.</param>
    /// <returns>
    /// The first <c>.cs</c> path found in the program text, or null when the segment is not a script
    /// interpreter or its program names none.
    /// </returns>
    /// <remarks>
    /// Gated on the command being an interpreter rather than applied to every segment, because a bare
    /// <c>.cs</c>-shaped substring is far too weak a signal on its own — a commit message or a grep
    /// pattern mentioning a filename would match. Inside <c>python3 -c "..."</c> it is a strong one,
    /// since a file path in an inline program is overwhelmingly a file the program opens.
    /// </remarks>
    public static string? ScriptCsLiteral(string segment)
    {
        if (CommandName(segment) is not { } name || !ScriptInterpreters.Contains(name))
        {
            return null;
        }

        var match = CsLiteral().Match(segment);
        return match.Success ? match.Groups["path"].Value : null;
    }

    /// <summary>Whether an inline program opens its file for writing rather than reading.</summary>
    /// <param name="segment">One segment from <see cref="Segments"/>.</param>
    /// <returns>True when the program text carries an explicit write mode or a write cmdlet.</returns>
    public static bool ScriptWritesFile(string segment) =>
        ScriptWriteMarkers.Any(marker => segment.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>The <c>.cs</c> file a segment reads, including through arguments the suffix test misses.</summary>
    /// <param name="segment">One segment from <see cref="Segments"/>.</param>
    /// <returns>The path as written, or null when the segment reads no named <c>.cs</c> file.</returns>
    /// <remarks>
    /// <see cref="FindCsArgument"/> answers for a bare path operand, which leaves two shapes reading a
    /// compiled file with no advisory at all: <c>dd if=Foo.cs</c>, whose path is welded to a key, and an
    /// interpreter one-liner, whose path is inside the program. Both dump the same bytes <c>cat</c> would.
    /// </remarks>
    public static string? FindReadTargetCsArgument(string segment)
    {
        if (string.Equals(CommandName(segment), "dd", StringComparison.OrdinalIgnoreCase))
        {
            var input = Tokenize(segment).FirstOrDefault(t => t.StartsWith("if=", StringComparison.Ordinal));
            if (input is { Length: > 3 } && input[3..].EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return input[3..];
            }
        }

        return FindCsArgument(segment) ?? ScriptCsLiteral(segment);
    }

    /// <summary>The reader a tree-walking command runs over what it finds, and the roots it walks.</summary>
    /// <param name="segment">One segment from <see cref="Segments"/>.</param>
    /// <returns>
    /// The utility name and the walk's roots, or null when the segment is not a walker running one.
    /// </returns>
    /// <remarks>
    /// <c>find . -name '*.cs' -exec cat {} \;</c> reads every compiled file in the tree while being, by
    /// its first word, a <c>find</c> — a command the read blocklist deliberately does not carry, since
    /// <c>find</c> alone only lists names. The read is real and belongs to the utility <c>-exec</c> names,
    /// so that is what this returns for the blocklist to judge.
    /// </remarks>
    public static (string Utility, IReadOnlyList<string> Roots)? ExecutedUtility(string segment)
    {
        if (CommandName(segment) is not ("find" or "fd"))
        {
            return null;
        }

        var tokens = Tokenize(segment).Skip(1).ToList();
        var roots = new List<string>();
        string? utility = null;
        var sawFlag = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token is "-exec" or "-execdir" or "-ok" or "-okdir" or "--exec" or "-x" or "-X")
            {
                sawFlag = true;
                if (i + 1 < tokens.Count)
                {
                    utility = CommandName(tokens[++i]);
                }

                continue;
            }

            if (token.StartsWith('-'))
            {
                sawFlag = true;
                continue;
            }

            // Path operands precede the first predicate; anything after one belongs to that predicate
            // or to the -exec template ({} and ;), never to the set of directories being walked.
            if (!sawFlag)
            {
                roots.Add(token);
            }
        }

        return utility is null ? null : (utility, roots.Count > 0 ? roots : (IReadOnlyList<string>)["."]);
    }

    /// <summary>
    /// The <c>.cs</c> file a write-shaped segment would overwrite: output redirection (<c>&gt;</c>/
    /// <c>&gt;&gt;</c>, optionally file-descriptor-prefixed), an in-place editor (<c>sed -i</c>,
    /// <c>perl -i</c>), <c>tee</c>, a <c>cp</c>/<c>mv</c>/<c>install</c> destination, or <c>dd</c>'s
    /// <c>of=</c>.
    /// </summary>
    /// <param name="segment">One segment from <see cref="Segments"/>.</param>
    /// <returns>The destination path token as written, or null when the segment does not write one.</returns>
    /// <remarks>
    /// Not a shell parser, same posture as the rest of this scanner: a redirection operator with no space
    /// before its target (<c>cat a.cs&gt;&gt;b.cs</c>) is one token and is not recognized, matching the
    /// documented <see cref="Tokenize"/> limitation for quoted, spaced paths.
    /// </remarks>
    public static string? FindWriteTargetCsArgument(string segment)
    {
        var tokens = SplitRedirects(TokenizeQuoteAware(segment)).ToList();
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (RedirectOperators.Contains(tokens[i])
                && tokens[i + 1].EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return tokens[i + 1];
            }
        }

        var name = CommandName(segment);
        if (name is null)
        {
            return null;
        }

        if (InPlaceEditCommands.Contains(name) && tokens.Any(IsInPlaceFlag))
        {
            return FindCsArgument(segment);
        }

        if (string.Equals(name, "tee", StringComparison.OrdinalIgnoreCase))
        {
            return FindCsArgument(segment);
        }

        if (DestinationCommands.Contains(name))
        {
            var destination = tokens.Skip(1).LastOrDefault(t => !t.StartsWith('-'));
            return destination is not null && destination.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                ? destination
                : null;
        }

        if (string.Equals(name, "dd", StringComparison.OrdinalIgnoreCase))
        {
            var of = tokens.FirstOrDefault(t => t.StartsWith("of=", StringComparison.Ordinal));
            if (of is { Length: > 3 } && of[3..].EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return of[3..];
            }
        }

        // An inline program writing a file is a write no argument-shaped check above can see. The explicit
        // write mode is what separates it from the read-only one-liner the read guard merely advises on.
        if (ScriptWritesFile(segment) && ScriptCsLiteral(segment) is { } scripted)
        {
            return scripted;
        }

        return null;
    }

    /// <summary>Separates a redirection operator welded to the words on either side of it.</summary>
    /// <param name="tokens">The segment's tokens, as <see cref="Tokenize"/> produced them.</param>
    /// <returns>The same tokens, with any unquoted redirection split into its own token.</returns>
    /// <remarks>
    /// Whitespace tokenization keeps <c>cat a.cs&gt;&gt;b.cs</c> as one token, so the operator scan that
    /// looks for a redirect followed by a path never fires and the write goes unguarded — while the spaced
    /// spelling of the identical command is denied. A guard whose enforcement depends on whether the user
    /// typed a space teaches the space away.
    /// <para>
    /// Tokens carrying a quote character are left alone: the <c>&gt;</c> in <c>"a&gt;b.cs"</c> is part of a
    /// filename, not an operator, and splitting it would invent a redirection the command never had.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> SplitRedirects(IEnumerable<(string Value, bool Quoted)> tokens)
    {
        foreach (var (token, quoted) in tokens)
        {
            if (quoted || !token.Contains('>'))
            {
                yield return token;
                continue;
            }

            var start = 0;
            for (var i = 0; i < token.Length; i++)
            {
                if (token[i] != '>')
                {
                    continue;
                }

                // A file-descriptor prefix (1>, 2>) belongs to the operator token, so the piece before it
                // stops one character earlier; a doubled or decorated operator is consumed whole.
                var operatorStart = i > 0 && char.IsAsciiDigit(token[i - 1]) ? i - 1 : i;
                var operatorEnd = i;
                while (operatorEnd + 1 < token.Length && token[operatorEnd + 1] is '>' or '|' or '&')
                {
                    operatorEnd++;
                }

                if (operatorStart > start)
                {
                    yield return token[start..operatorStart];
                }

                yield return token[operatorStart..(operatorEnd + 1)];
                start = operatorEnd + 1;
                i = operatorEnd;
            }

            if (start < token.Length)
            {
                yield return token[start..];
            }
        }
    }

    private static bool IsInPlaceFlag(string token) =>
        token is "-i" or "--in-place"
        || (token.StartsWith("-i", StringComparison.Ordinal) && !token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
        || token.StartsWith("--in-place=", StringComparison.Ordinal);

    private static readonly HashSet<string> RedirectOperators = new(StringComparer.Ordinal)
    {
        ">", ">>", "1>", "1>>", "2>", "2>>", "&>", "&>>", ">|",
    };

    private static readonly HashSet<string> InPlaceEditCommands =
        new(StringComparer.OrdinalIgnoreCase) { "sed", "perl" };

    private static readonly HashSet<string> DestinationCommands =
        new(StringComparer.OrdinalIgnoreCase) { "cp", "mv", "install" };

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
    private static IEnumerable<string> Tokenize(string segment) =>
        TokenizeQuoteAware(segment).Select(token => token.Value);

    /// <summary>Tokenizes a segment while remembering which tokens arrived quoted.</summary>
    /// <param name="segment">One segment from <see cref="Segments"/>.</param>
    /// <returns>Each token, unquoted, paired with whether quotes were removed from it.</returns>
    /// <remarks>
    /// <see cref="Tokenize"/> strips the quotes, which is right for every suffix test but destroys the one
    /// fact <see cref="SplitRedirects"/> needs: a <c>&gt;</c> inside quotes is part of a filename, not an
    /// operator. Splitting an already-unquoted <c>"a&gt;b.cs"</c> invented a redirection the command never
    /// had and reported <c>b.cs</c> as a write target — a false denial, which is the expensive direction
    /// for a guard to be wrong in.
    /// </remarks>
    private static IEnumerable<(string Value, bool Quoted)> TokenizeQuoteAware(string segment)
    {
        foreach (var raw in segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length >= 2 && (raw[0] == '"' || raw[0] == '\'') && raw[^1] == raw[0])
            {
                yield return (raw[1..^1], true);
                continue;
            }

            yield return (raw, false);
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
