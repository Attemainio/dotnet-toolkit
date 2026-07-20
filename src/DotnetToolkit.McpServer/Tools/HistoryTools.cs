using System.ComponentModel;
using DotnetToolkit.McpServer.Git;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Workspace;
using ModelContextProtocol.Server;

namespace DotnetToolkit.McpServer.Tools;

/// <summary>
/// History surface (spec §14): what changed semantically between two refs, and the recorded rationale
/// behind past changes.
/// </summary>
[McpServerToolType]
public static class HistoryTools
{
    [McpServerTool(Name = "get_semantic_diff")]
    [Description("What changed SEMANTICALLY between two git refs — symbols added, removed, and changed with "
        + "which version layers moved and the API impact. Formatting- and comment-only commits report no change. "
        + "Use this instead of reading a textual diff.")]
    public static async Task<string> GetSemanticDiff(
        GitAnalyzer git,
        SolutionLocator locator,
        SemanticDiff diff,
        [Description("Base ref (branch, tag or sha). Default: HEAD~1.")] string fromRef = "HEAD~1",
        [Description("Target ref. Default: HEAD.")] string toRef = "HEAD")
    {
        var format = Formats.Parse(locator.Config.DefaultFormat);
        if (!await git.IsRepositoryAsync())
            return Formats.Render(new { error = "not_a_git_repository" }, format);

        var from = await git.ResolveRefAsync(fromRef);
        var to = await git.ResolveRefAsync(toRef);
        if (from is null || to is null)
            return Formats.Render(new
            {
                error = "unresolved_ref",
                message = from is null ? $"cannot resolve '{fromRef}'" : $"cannot resolve '{toRef}'",
            }, format);

        var result = await diff.CompareAsync(from, to);
        var breaking = result.Changed.Count(c => c.ApiImpact.StartsWith("breaking", StringComparison.Ordinal));

        return Formats.Render(new
        {
            range = new { from = fromRef, to = toRef, commits = result.Commits },
            symbolsAdded = result.Added,
            symbolsRemoved = result.Removed,
            symbolsChanged = result.Changed.Select(c => new
            {
                displayString = c.DisplayString,
                layersChanged = c.LayersChanged,
                apiImpact = c.ApiImpact,
            }),
            apiImpactSummary = new
            {
                breaking,
                nonBreaking = result.Changed.Count - breaking,
                added = result.Added.Count,
                removed = result.Removed.Count,
            },
        }, format);
    }

[McpServerTool(Name = "search_log")]
    [Description("Search the development log for WHY past changes were made — recorded intents, with the symbols "
        + "each change touched. Use before re-proposing a design, to avoid repeating a rejected approach. "
        + "Response shape: {items:{columns,rows}}, a TABLE with fixed columns [\"logId\",\"date\",\"intent\",\"tags\"] "
        + "— read rows[i][3] for tags (a real JSON array), not rows[i].tags.")]
    public static string SearchLog(
        FeatureLogStore featureLog,
        SolutionLocator locator,
        [Description("Free-text query over recorded intents; omit to list the most recent entries.")] string? query = null,
        [Description("Max entries (default 10).")] int limit = 10)
    {
        var format = Formats.Parse(locator.Config.DefaultFormat);
        var entries = featureLog.SearchIntents(query, Math.Clamp(limit, 1, 50));
        var items = CompactTable.Of(
            ["logId", "date", "intent", "tags"],
            entries,
            e => (IReadOnlyList<object?>)
            [
                e.LogId,
                e.CreatedAt.Length >= 10 ? e.CreatedAt[..10] : e.CreatedAt,
                e.Intent,
                e.Tags,
            ]);
        return Formats.Render(new { items }, format);
    }
}
