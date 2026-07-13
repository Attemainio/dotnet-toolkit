using System.ComponentModel;
using DotnetToolkit.McpServer.Devlog;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Workspace;
using ModelContextProtocol.Server;

namespace DotnetToolkit.McpServer.Tools;

[McpServerToolType]
public static class DevlogTools
{
    [McpServerTool(Name = "devlog_add")]
    [Description("Record a development log entry after finishing (or abandoning) a change. Entries land in the weekly markdown file under docs/devlog/. Never edit those files by hand; always use this tool.")]
    public static string DevlogAdd(
        DevlogStore store,
        [Description("Short entry title, e.g. 'Fixed decimal rounding in price calculation'.")] string title,
        [Description("WHAT was done (or attempted).")] string what,
        [Description("WHY it was done / why not completed.")] string why,
        [Description("OBSERVATIONS: what was tried, what worked, what didn't, and why alternatives were rejected.")] string? observations = null,
        [Description("FIX: the concrete change and how it was validated.")] string? fix = null,
        [Description("done | not-done | partial (default done).")] string status = "done",
        [Description("Affected class names, e.g. ['PriceCalculator','OrderService'].")] string[]? affected_classes = null,
        [Description("Affected ensemble/module/folder, e.g. 'Ordering'.")] string? ensemble = null,
        [Description("Free-form tags, e.g. ['bug','rounding'].")] string[]? tags = null)
    {
        var (id, file) = store.Add(title, what, why, observations, fix,
            status.Trim().ToLowerInvariant(), affected_classes ?? [], ensemble, tags ?? []);
        return $"added {id} -> {file}";
    }

    [McpServerTool(Name = "devlog_search")]
    [Description("Search the development log before starting work on an area: free-text query plus filters. Returns compact hit rows only; fetch a full entry with devlog_get. Never read the devlog markdown files directly.")]
    public static string DevlogSearch(
        DevlogStore store,
        SolutionLocator locator,
        [Description("Free-text query over titles and bodies; omit to list the most recent entries.")] string? query = null,
        [Description("Filter: entries touching this class name.")] string? affected_class = null,
        [Description("Filter: entries in this ensemble/module.")] string? ensemble = null,
        [Description("Filter: entries with this tag.")] string? tag = null,
        [Description("Filter: done | not-done | partial.")] string? status = null,
        [Description("Filter: entries on/after this date (yyyy-MM-dd).")] string? from = null,
        [Description("Filter: entries on/before this date (yyyy-MM-dd).")] string? to = null,
        [Description("Max results (default 10).")] int limit = 10,
        [Description("compact | json")] string? format = null)
    {
        DateOnly? fromDate = DateOnly.TryParse(from, out var f) ? f : null;
        DateOnly? toDate = DateOnly.TryParse(to, out var t) ? t : null;
        limit = Math.Clamp(limit, 1, 50);

        var hits = store.Search(query, affected_class, ensemble, tag, status, fromDate, toDate, limit);
        var total = store.TotalMatching(query, affected_class, ensemble, tag, status, fromDate, toDate);

        if (Formats.Parse(format ?? locator.Config.DefaultFormat) == OutputFormat.Json)
            return Formats.ToJson(new
            {
                total,
                hits = hits.Select(h => new
                {
                    id = h.Entry.Id,
                    date = h.Entry.Ts.ToString("yyyy-MM-dd"),
                    title = h.Entry.Title,
                    status = h.Entry.Status,
                    classes = h.Entry.Classes,
                    ensemble = h.Entry.Ensemble,
                    score = h.Score,
                }),
            });

        if (hits.Count == 0)
            return "no devlog entries match";
        var rows = hits.Select(h => new[]
        {
            h.Entry.Id,
            h.Entry.Ts.ToString("yyyy-MM-dd"),
            CompactFormatter.Truncate(h.Entry.Title, 80),
            h.Entry.Status,
            string.Join(",", h.Entry.Classes),
            h.Entry.Ensemble ?? "",
            h.Score.ToString("0.#"),
        }).ToList();
        return CompactFormatter.Table("devlog", ["id", "date", "title", "status", "classes", "ensemble", "score"], rows, total);
    }

    [McpServerTool(Name = "devlog_get")]
    [Description("Full markdown of one devlog entry by id (from devlog_search results).")]
    public static string DevlogGet(
        DevlogStore store,
        [Description("Entry id, e.g. '20260712-1403-a3f2'.")] string id)
    {
        return store.Get(id) ?? $"not found: {id}";
    }
}
