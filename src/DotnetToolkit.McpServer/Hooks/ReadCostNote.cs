using System.Globalization;

namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// The one sentence that turns the read guards' advisory from a policy into a number: how much the
/// whole-file read about to happen injects, and how much this session's whole-file reads have injected
/// so far.
/// </summary>
/// <remarks>
/// The 2026-09-20 performance run against FluidScript is why this exists. The full probe made thirteen
/// whole-file reads at an average of 2,227 response tokens each, saw the advisory on every one, and
/// overrode it every time without comment — the note restated a policy the agent had already decided
/// against, so a fourteenth copy changed nothing. A number the agent can weigh is a different message:
/// "this read costs N, the alternative costs a fraction of N, and you have paid M this session already"
/// is an argument, where "consider the skill" is a nag. The tally is what makes the second and later
/// notes stronger than the first rather than identical to it, which was the other candidate fix (fire
/// only from the second read on) folded into this one without losing the first read's note.
/// <para>
/// Tokens are the same <c>chars4</c> estimate the meter uses — <c>(bytes + 3) / 4</c> — so the number here
/// is comparable to what <c>get_retrieval_metrics</c> later reports for the read, not a second scale.
/// The tally lives in a per-session file under the OS temp directory, beside the write-checklist marker,
/// and every failure to read or write it is silent: the note degrades to the single-file figure, never
/// to an error, because a hook must never wedge a read.
/// </para>
/// </remarks>
internal static class ReadCostNote
{
    private const string MarkerDirectory = "dotnet-toolkit-hooks";

    /// <summary>Composes the cost sentence for a whole-file read, updating the session tally.</summary>
    /// <param name="absolutePath">The file about to be read in full.</param>
    /// <param name="sessionId">The harness session id, or null when the payload carried none.</param>
    /// <returns>
    /// One or two sentences: the file's size in lines and estimated tokens, and — from the second
    /// whole-file read of a session on — how many such reads the session has made and their total.
    /// Null when the file could not be measured, so the caller's message falls back to text alone.
    /// </returns>
    public static string? For(string absolutePath, string? sessionId)
    {
        int lines;
        long bytes;
        try
        {
            bytes = new FileInfo(absolutePath).Length;
            lines = CountLines(absolutePath);
        }
        catch (Exception)
        {
            return null;
        }

        var tokens = (bytes + 3) / 4;
        var name = Path.GetFileName(absolutePath);
        var note = $"This read injects all {N(lines)} lines of {name} (about {N(tokens)} tokens). A ranged Read "
            + "of one declaration's declarationSites span returns only those lines.";

        var tally = Record(sessionId, lines, tokens);
        if (tally is { Count: > 1 })
        {
            note += $" It is whole-file .cs read number {tally.Value.Count} this session: {N(tally.Value.Lines)} lines, "
                + $"about {N(tally.Value.Tokens)} tokens, have gone into context this way so far.";
        }
        return note;
    }

    /// <summary>Counts newline-terminated lines the way an editor numbers them.</summary>
    private static int CountLines(string path)
    {
        var count = 0;
        using var reader = new StreamReader(path);
        while (reader.ReadLine() is not null)
        {
            count++;
        }
        return count;
    }

    /// <summary>Adds this read to the session's tally and returns the running totals.</summary>
    /// <param name="sessionId">The session the tally belongs to; null records nothing.</param>
    /// <param name="lines">Lines this read injects.</param>
    /// <param name="tokens">Estimated tokens this read injects.</param>
    /// <returns>The totals including this read, or null when nothing could be recorded.</returns>
    private static (int Count, long Lines, long Tokens)? Record(string? sessionId, int lines, long tokens)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return null;
        }
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), MarkerDirectory);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, SanitizeFileName(sessionId));

            var count = 0;
            long totalLines = 0;
            long totalTokens = 0;
            if (File.Exists(path))
            {
                var parts = File.ReadAllText(path).Split(' ');
                if (parts.Length == 3
                    && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out count)
                    && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out totalLines)
                    && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out totalTokens))
                {
                    // parsed in place
                }
                else
                {
                    count = 0;
                    totalLines = 0;
                    totalTokens = 0;
                }
            }

            count++;
            totalLines += lines;
            totalTokens += tokens;
            File.WriteAllText(path, string.Create(CultureInfo.InvariantCulture, $"{count} {totalLines} {totalTokens}"));
            return (count, totalLines, totalTokens);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Reduces a session id to a safe file name, distinct from the checklist marker's.</summary>
    private static string SanitizeFileName(string sessionId)
    {
        const int MaxLength = 64;
        var safe = new string([.. sessionId
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            .Take(MaxLength)]);
        return $"{safe}.cs-reads";
    }
}
