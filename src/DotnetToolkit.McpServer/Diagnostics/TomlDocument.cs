using System.Text;

namespace DotnetToolkit.McpServer.Diagnostics;

/// <summary>One value read out of a TOML file: either a single value or an array of them.</summary>
/// <param name="Scalar">The value's text, unquoted and unescaped; null when this is an array.</param>
/// <param name="Items">The array's elements, unquoted and unescaped; null when this is a scalar.</param>
internal sealed record TomlValue(string? Scalar, IReadOnlyList<string>? Items)
{
    /// <value>Every element, whether the value was written as an array or as a single item.</value>
    /// <remarks>
    /// Lets a caller treat <c>args = "x"</c> and <c>args = ["x"]</c> alike, which matters because a
    /// diagnostic wants to inspect each path either spelling produced, not to police the spelling.
    /// </remarks>
    public IReadOnlyList<string> AsList => Items ?? (Scalar is null ? [] : [Scalar]);
}

/// <summary>
/// A TOML file read far enough to inspect an agent's stored configuration — table headers, string keys,
/// string arrays, and inline tables.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deliberately a subset, not a TOML implementation.</strong> This reads configuration the
/// plugin does not own and must never rewrite; it needs to answer "what path did you store, and could
/// this process open it", and every TOML feature past strings and arrays of strings — datetimes,
/// multi-line strings, arrays of tables, numeric types — is irrelevant to that question. Taking a TOML
/// package to answer it would add a dependency to the published <c>dist/</c> that no other code path
/// uses, on the *diagnostic* rather than on the server it diagnoses.
/// </para>
/// <para>
/// The parser is line-oriented and forgiving: an unrecognized construct is skipped rather than thrown
/// over, because a diagnostic that refuses to report anything when one line of a user's config is
/// exotic is less useful than one that reports what it did understand. What it cannot do is invent —
/// a key it did not parse is simply absent, and callers report absence as unknown.
/// </para>
/// </remarks>
internal sealed class TomlDocument
{
    private readonly Dictionary<string, Dictionary<string, TomlValue>> _tables;

    private TomlDocument(Dictionary<string, Dictionary<string, TomlValue>> tables) => _tables = tables;

    /// <summary>Reads a TOML file's tables, keys and string values.</summary>
    /// <param name="text">The file's full text.</param>
    /// <returns>A document exposing every table and key the subset above covers.</returns>
    public static TomlDocument Parse(string text)
    {
        var tables = new Dictionary<string, Dictionary<string, TomlValue>>(StringComparer.Ordinal);
        var current = new Dictionary<string, TomlValue>(StringComparer.Ordinal);
        tables[string.Empty] = current;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                var end = line.LastIndexOf(']');
                if (end <= 0)
                {
                    continue;
                }

                // An array-of-tables header ([[x]]) names the same table path; the diagnostic reads keys,
                // not element ordering, so the extra brackets are simply trimmed away.
                var header = line[1..end].Trim('[', ']').Trim();
                current = new Dictionary<string, TomlValue>(StringComparer.Ordinal);
                tables[NormalizeKey(header)] = current;
                continue;
            }

            var separator = IndexOfUnquoted(line, '=');
            if (separator <= 0)
            {
                continue;
            }

            var key = NormalizeKey(line[..separator].Trim());
            var value = line[(separator + 1)..].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            if (value.StartsWith('{'))
            {
                foreach (var (inlineKey, inlineValue) in ParseInlineTable(value))
                {
                    current[$"{key}.{inlineKey}"] = inlineValue;
                }

                continue;
            }

            current[key] = ParseValue(value);
        }

        return new TomlDocument(tables);
    }

    /// <summary>Looks up one table by its dotted name.</summary>
    /// <param name="name">The table path, e.g. <c>mcp_servers.dotnet</c>; empty for the root table.</param>
    /// <returns>The table's keys, or null when the file declared no such table.</returns>
    public IReadOnlyDictionary<string, TomlValue>? Table(string name) =>
        _tables.TryGetValue(name, out var table) ? table : null;

    /// <summary>Finds every table declared directly under a dotted prefix.</summary>
    /// <param name="prefix">The parent table path, without a trailing dot.</param>
    /// <returns>
    /// The child name of each matching table — <c>dotnet</c> for <c>mcp_servers.dotnet</c> — excluding
    /// grandchildren such as <c>mcp_servers.dotnet.env</c>, whose keys belong to their own table.
    /// </returns>
    public IEnumerable<string> ChildrenOf(string prefix) =>
        _tables.Keys
            .Where(name => name.StartsWith(prefix + ".", StringComparison.Ordinal))
            .Select(name => name[(prefix.Length + 1)..])
            .Where(child => !child.Contains('.', StringComparison.Ordinal))
            .OrderBy(child => child, StringComparer.Ordinal);

    /// <summary>Reads one string out of a table, if both the table and the key are present.</summary>
    /// <param name="table">The dotted table path.</param>
    /// <param name="key">The key within it.</param>
    /// <returns>The scalar's text, or null when either is absent or the value is an array.</returns>
    public string? Scalar(string table, string key) =>
        Table(table)?.TryGetValue(key, out var value) == true ? value.Scalar : null;

    /// <summary>Strips the quoting a dotted key or table header may carry on each segment.</summary>
    /// <param name="key">The raw key or header text.</param>
    /// <returns>The key with each segment unquoted, rejoined with dots.</returns>
    private static string NormalizeKey(string key) =>
        string.Join('.', key.Split('.').Select(segment => segment.Trim().Trim('"', '\'')));

    /// <summary>Reads one value's text into a scalar or an array.</summary>
    /// <param name="value">The text to the right of the <c>=</c>, already trimmed.</param>
    /// <returns>The parsed value; an unterminated array is kept as a scalar so nothing is silently lost.</returns>
    private static TomlValue ParseValue(string value)
    {
        if (!value.StartsWith('['))
        {
            return new TomlValue(Unquote(StripComment(value)), null);
        }

        var end = value.LastIndexOf(']');
        if (end < 0)
        {
            // A multi-line array: the elements are on lines this reader never associates with the key.
            // Keeping the opening text as a scalar is what stops it being reported as an empty array,
            // which would read as "you configured no arguments" — a different and wrong diagnosis.
            return new TomlValue(value, null);
        }

        var items = SplitTopLevel(value[1..end], ',')
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Select(Unquote)
            .ToList();

        return new TomlValue(null, items);
    }

    /// <summary>Reads the key/value pairs of an inline table.</summary>
    /// <param name="value">The inline table's text, including its braces.</param>
    /// <returns>Each pair, with values parsed as scalars or arrays.</returns>
    private static IEnumerable<KeyValuePair<string, TomlValue>> ParseInlineTable(string value)
    {
        var end = value.LastIndexOf('}');
        var body = end < 0 ? value[1..] : value[1..end];

        foreach (var entry in SplitTopLevel(body, ','))
        {
            var separator = IndexOfUnquoted(entry, '=');
            if (separator <= 0)
            {
                continue;
            }

            yield return new KeyValuePair<string, TomlValue>(
                NormalizeKey(entry[..separator].Trim()),
                ParseValue(entry[(separator + 1)..].Trim()));
        }
    }

    /// <summary>Removes a trailing <c>#</c> comment from a value's text.</summary>
    /// <param name="value">The value text, which may end in a comment.</param>
    /// <returns>The value with any unquoted comment removed and trailing space trimmed.</returns>
    private static string StripComment(string value)
    {
        var hash = IndexOfUnquoted(value, '#');
        return (hash < 0 ? value : value[..hash]).TrimEnd();
    }

    /// <summary>Removes TOML string quoting, honoring the difference between the two string forms.</summary>
    /// <param name="value">One value's text, possibly quoted.</param>
    /// <returns>
    /// The text inside the quotes, with escapes processed for a basic (<c>"</c>) string and left
    /// untouched for a literal (<c>'</c>) one; an unquoted value is returned as written.
    /// </returns>
    /// <remarks>
    /// The distinction is load-bearing here rather than pedantic: a Windows path is written as a literal
    /// string precisely so its backslashes survive, and unescaping one anyway would turn
    /// <c>'C:\temp'</c> into a path with a tab in it — a diagnostic reporting a path the user never
    /// wrote.
    /// </remarks>
    private static string Unquote(string value)
    {
        var text = StripComment(value.Trim());

        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
        {
            return text[1..^1];
        }

        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
        {
            return text;
        }

        var inner = text[1..^1];
        var result = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != '\\' || i + 1 >= inner.Length)
            {
                result.Append(inner[i]);
                continue;
            }

            // An unrecognized escape keeps both characters. TOML would reject the file, but a config
            // holding `"C:\Users\x"` is exactly the malformed-but-intended case worth reporting back to
            // the user verbatim rather than mangling into something they cannot recognize.
            i++;
            result.Append(inner[i] switch
            {
                'n' => "\n",
                't' => "\t",
                'r' => "\r",
                '"' => "\"",
                '\\' => "\\",
                _ => $"\\{inner[i]}",
            });
        }

        return result.ToString();
    }

    /// <summary>Finds a character that is not inside a quoted string.</summary>
    /// <param name="text">The text to scan.</param>
    /// <param name="target">The character to find.</param>
    /// <returns>The index of the first unquoted occurrence, or -1 when there is none.</returns>
    private static int IndexOfUnquoted(string text, char target)
    {
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                // Only a basic string processes escapes, so a backslash inside a literal string is an
                // ordinary character and must not be allowed to hide the closing quote.
                if (c == '\\' && quote == '"')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == target)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Splits text on a separator, ignoring separators inside strings, arrays or inline tables.</summary>
    /// <param name="text">The text to split.</param>
    /// <param name="separator">The separator character.</param>
    /// <returns>Each segment, in order, with the separators removed.</returns>
    private static List<string> SplitTopLevel(string text, char separator)
    {
        var segments = new List<string>();
        var start = 0;
        var depth = 0;
        var quote = '\0';

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c is '[' or '{')
            {
                depth++;
            }
            else if (c is ']' or '}')
            {
                depth--;
            }
            else if (c == separator && depth == 0)
            {
                segments.Add(text[start..i]);
                start = i + 1;
            }
        }

        segments.Add(text[start..]);
        return segments;
    }
}
