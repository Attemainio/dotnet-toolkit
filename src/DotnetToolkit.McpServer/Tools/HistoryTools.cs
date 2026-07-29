using System.ComponentModel;
using DotnetToolkit.McpServer.Git;
using DotnetToolkit.McpServer.Identity;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Telemetry;
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
        + "Use this instead of reading a textual diff. Each of symbolsAdded/symbolsRemoved/symbolsChanged is "
        + "capped independently at limit entries; a capped list carries its own *Truncated:true flag alongside it, "
        + "while apiImpactSummary always reports the true added/removed/changed counts regardless of the cap. "
        + "A solution root that is not itself a repository but holds several (projects from separate repos "
        + "checked out side by side) is diffed one repository at a time — name it with repo.")]
    public static async Task<string> GetSemanticDiff(
        GitAnalyzer git,
        SemanticDiff diff,
        TelemetryRecorder telemetry,
        [Description("Base ref (branch, tag or sha). Default: HEAD~1.")] string fromRef = "HEAD~1",
        [Description("Target ref. Default: HEAD.")] string toRef = "HEAD",
        [Description("Max entries kept in each of symbolsAdded/symbolsRemoved/symbolsChanged, capped "
            + "independently (default 50, cap 200). apiImpactSummary's counts are never capped.")] int limit = 50,
        [Description("Which repository to diff, by directory name, when the solution root is not itself a "
            + "git repository. Omit when the root is one, or when exactly one repository sits beneath it.")] string? repo = null,
        [Description(ToolTelemetry.TaskIdParam)] string? taskId = null)

    {
        var sessionId = Ids.AmbientSession;
        var attributedTask = Ids.TaskId(taskId);
        var toolCallId = Ids.ToolCall();
        var requested = $"{fromRef}..{toRef}";

        string Fail(string kind, object payload) =>
            ToolTelemetry.Record(telemetry, toolCallId, sessionId, attributedTask, "get_semantic_diff",
                requested, Formats.Render(payload), errorKind: kind);

        // The root is not always the repository: projects from separate repositories are routinely checked
        // out side by side under a folder that was never one itself, and resolving git only from the root
        // reports not_a_git_repository for a solution whose every project is versioned.
        var repositories = git.Repositories;
        var selected = repositories.Count == 1 ? repositories[0] : null;
        if (repo is not null)
        {
            var wanted = repo.Trim().Trim('/', '\\');
            selected = repositories.FirstOrDefault(r =>
                string.Equals(Path.GetFileName(r), wanted, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                return Fail("unknown_repository", new
                {
                    error = "unknown_repository",
                    message = $"no repository '{repo}' under the solution root",
                    repositories = repositories.Select(Path.GetFileName),
                });
            }
        }
        else if (repositories.Count > 1)
        {
            // Each repository has its own history, so there is no single diff to report and guessing one
            // would answer a question the caller did not ask. Naming them costs less than guessing wrong.
            return Fail("ambiguous_repository", new
            {
                error = "ambiguous_repository",
                message = "the solution root holds several repositories; pass repo to pick one",
                repositories = repositories.Select(Path.GetFileName),
            });
        }

        var scoped = selected is null ? git : git.For(selected);
        if (!await scoped.IsRepositoryAsync())
            return Fail("not_a_git_repository", new { error = "not_a_git_repository" });

        var from = await scoped.ResolveRefAsync(fromRef);
        var to = await scoped.ResolveRefAsync(toRef);
        if (from is null || to is null)
        {
            var unresolved = Formats.Render(new
            {
                error = "unresolved_ref",
                message = from is null ? $"cannot resolve '{fromRef}'" : $"cannot resolve '{toRef}'",
            });
            return ToolTelemetry.Record(telemetry, toolCallId, sessionId, attributedTask, "get_semantic_diff",
                requested, unresolved, errorKind: "unresolved_ref");
        }

        limit = Math.Clamp(limit, 1, 200);
        var result = await diff.CompareAsync(from, to, scoped);
        var breaking = result.Changed.Count(c => c.ApiImpact.StartsWith("breaking", StringComparison.Ordinal));

        var addedTruncated = result.Added.Count > limit;
        var removedTruncated = result.Removed.Count > limit;
        var changedTruncated = result.Changed.Count > limit;

        var json = Formats.Render(new
        {
            range = new { from = fromRef, to = toRef, commits = result.Commits },
            symbolsAdded = result.Added.Take(limit),
            symbolsAddedTruncated = addedTruncated ? true : (bool?)null,
            symbolsRemoved = result.Removed.Take(limit),
            symbolsRemovedTruncated = removedTruncated ? true : (bool?)null,
            symbolsChanged = result.Changed.Take(limit).Select(c => new
            {
                displayString = c.DisplayString,
                layersChanged = c.LayersChanged,
                apiImpact = c.ApiImpact,
            }),
            symbolsChangedTruncated = changedTruncated ? true : (bool?)null,
            apiImpactSummary = new
            {
                breaking,
                nonBreaking = result.Changed.Count - breaking,
                added = result.Added.Count,
                removed = result.Removed.Count,
            },
        });

        return ToolTelemetry.Record(telemetry, toolCallId, sessionId, attributedTask, "get_semantic_diff",
            requested, json, returnedSymbols: result.Added.Count + result.Removed.Count + result.Changed.Count);
    }


    [McpServerTool(Name = "search_log")]
    [Description("Search the development log for WHY past changes were made — recorded intents, with the symbols "
        + "each change touched. Use before re-proposing a design, to avoid repeating a rejected approach. "
        + "Each entry carries logId, date, intent, and tags (a JSON array, present only when the patch that "
        + "created the entry actually supplied one — most entries carry none, since validate_patch's tags "
        + "argument is optional and rarely used). Matching is over intent only — there is no tag-based filter. "
        + "The query is split on whitespace and every term must appear somewhere in the intent, in ANY order "
        + "(AND, not search_index's ranked OR): \"task id telemetry\" matches an intent containing all three "
        + "words however they are arranged, and adding a term narrows the result rather than widening it.")]
    public static string SearchLog(
        FeatureLogStore featureLog,
        TelemetryRecorder telemetry,
        [Description("Whitespace-separated terms matched against recorded intents; every term must appear, in any order. Omit to list the most recent entries.")] string? query = null,
        [Description("Max entries (default 10).")] int limit = 10,
        [Description(ToolTelemetry.TaskIdParam)] string? taskId = null)
    {
        var sessionId = Ids.AmbientSession;
        var attributedTask = Ids.TaskId(taskId);
        var toolCallId = Ids.ToolCall();

        var entries = featureLog.SearchIntents(query, Math.Clamp(limit, 1, 50));
        var items = entries.Select(e => new
        {
            logId = e.LogId,
            date = e.CreatedAt.Length >= 10 ? e.CreatedAt[..10] : e.CreatedAt,
            intent = e.Intent,
            // Absent rather than an empty array when nothing was supplied — an empty TOON array renders as
            // a dangling "tags[0]:" header with no row beneath it, which reads as a malformed entry rather
            // than "no tags".
            tags = e.Tags.Count > 0 ? e.Tags : null,
        });
        return ToolTelemetry.Record(telemetry, toolCallId, sessionId, attributedTask, "search_log",
            query ?? "(recent)", Formats.Render(new { items }), returnedSymbols: entries.Count());
    }
}
