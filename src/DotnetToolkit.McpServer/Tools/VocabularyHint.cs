using DotnetToolkit.McpServer.Store;

namespace DotnetToolkit.McpServer.Tools;

/// <summary>
/// Suggests the nearest token in a small, closed, literal vocabulary (an enum-like string parameter
/// such as "callers"/"callees") for an unrecognized value. The parameter-vocabulary analogue of
/// <see cref="SymbolStore.NearNames"/>, sized for a handful of known tokens rather than a live symbol
/// index, so it never touches the database.
/// </summary>
internal static class VocabularyHint
{
    /// <summary>
    /// Returns the single vocabulary token nearest to <paramref name="given"/>, or null when nothing
    /// is close enough to guess -- including a tie between two equally-close tokens, since a silent
    /// miss is safer than a wrong guess in either direction.
    /// </summary>
    internal static string? NearestToken(string? given, IReadOnlyList<string> vocabulary)
    {
        if (string.IsNullOrWhiteSpace(given))
            return null;

        var normalized = given.Trim().ToLowerInvariant();
        string? best = null;
        var bestDistance = int.MaxValue;
        var tie = false;

        foreach (var token in vocabulary)
        {
            var lowered = token.ToLowerInvariant();
            if (normalized == lowered)
                return null; // exact match belongs to the caller's already-unrecognized branch, not this one

            var distance = Distance(normalized, lowered);
            if (distance < bestDistance)
            {
                best = token;
                bestDistance = distance;
                tie = false;
            }
            else if (distance == bestDistance)
            {
                tie = true;
            }
        }

        if (best is null || tie)
            return null;

        var tolerance = Math.Clamp(best.Length / 4, 1, 3);
        return bestDistance <= tolerance ? best : null;
    }

    /// <summary>
    /// Zero for the two typo shapes actually observed against this server's own vocabulary params
    /// (a missing/extra separator, or a singular/plural mismatch) before falling back to edit distance
    /// -- catches "solutionvalidate" against "solution_validate" and "methods" against "method" without
    /// spending the tolerance budget those would otherwise cost.
    /// </summary>
    private static int Distance(string normalized, string lowered)
    {
        if (normalized.Replace("_", "") == lowered.Replace("_", ""))
            return 0;
        if (normalized.Length > 1 && lowered.Length > 1 && normalized.TrimEnd('s') == lowered.TrimEnd('s'))
            return 0;
        return SymbolStore.EditDistance(normalized, lowered, 3);
    }
}
