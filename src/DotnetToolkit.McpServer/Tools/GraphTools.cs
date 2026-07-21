using System.ComponentModel;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace DotnetToolkit.McpServer.Tools;

/// <summary>
/// Solution-structure tools — the project reference graph, and cycles within it. Both walk
/// <see cref="Solution.Projects"/>/<see cref="Project.ProjectReferences"/> live, with no caching or new
/// indexing: a solution's project count is always small (tens, not thousands), unlike the symbol graph
/// <see cref="FlowTools"/> walks.
/// </summary>
[McpServerToolType]
public static class GraphTools
{
    [McpServerTool(Name = "get_project_graph")]
    [Description("The solution's project reference graph: which .csproj references which, and the reverse "
        + "(referencedBy). Computed live from the loaded solution on every call, no caching. Pass project to "
        + "scope the result to one project's direct references and dependents instead of the whole graph.")]
    public static async Task<string> GetProjectGraph(
        WorkspaceHost workspace,
        [Description("Optional project name to scope to one project's direct references + dependents. Omit for the full graph.")] string? project = null)
    {
        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
            return Formats.Render(new { error = "workspace_loading" });

        var (refs, referencedBy) = BuildAdjacency(solution);

        if (project is not null && !refs.ContainsKey(project))
            return Formats.Render(new { error = "project_not_found", project });

        var diags = workspace.LoadDiagnostics;
        IEnumerable<string> names = project is null
            ? refs.Keys.OrderBy(n => n, StringComparer.Ordinal)
            : new[] { project };

        var projects = names.Select(name => new
        {
            name,
            references = refs.GetValueOrDefault(name) ?? [],
            referencedBy = referencedBy.GetValueOrDefault(name) ?? [],
            degraded = diags.Any(d => d.Contains(name, StringComparison.OrdinalIgnoreCase)) ? true : (bool?)null,
        });

        return Formats.Render(new
        {
            projects,
            totalProjects = refs.Count,
            limitedBy = workspace.IsDegraded ? "degraded" : null,
        });
    }

    [McpServerTool(Name = "detect_circular_dependencies")]
    [Description("Cycles in the solution's project reference graph — a real dependency loop, not just deep "
        + "nesting. scope:\"project\" (default, and for now the only supported value) reports one "
        + "representative cycle per strongly-connected component found. scope:\"type\" is NOT yet "
        + "implemented — it would need collapsing member-level call edges up to their containing type, which "
        + "this server does not do today — and returns error:\"unsupported_scope\" rather than a partial "
        + "answer.")]
    public static async Task<string> DetectCircularDependencies(
        WorkspaceHost workspace,
        [Description("project (default) | type (not yet supported, returns unsupported_scope).")] string scope = "project")
    {
        var normalized = scope.Trim().ToLowerInvariant();
        if (normalized != "project")
            return Formats.Render(new
            {
                error = "unsupported_scope",
                message = "type-level cycle detection is not yet implemented; use scope: \"project\"",
            });

        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
            return Formats.Render(new { error = "workspace_loading" });

        var (refs, _) = BuildAdjacency(solution);
        var cycles = ProjectCycleDetector.FindCycles(refs);

        return Formats.Render(new
        {
            scope = "project",
            cycles = cycles.Select(c => new { projects = c, length = c.Count - 1 }),
            totalCycles = cycles.Count,
            limitedBy = workspace.IsDegraded ? "degraded" : null,
        });
    }

    /// <summary>
    /// Every project's direct references and their transpose (referencedBy), built in one extra pass over
    /// the same data — no graph library needed at this node count. Shared by both tools above so the
    /// walk over <see cref="Solution.Projects"/> happens once, not twice.
    /// </summary>
    private static (Dictionary<string, List<string>> References, Dictionary<string, List<string>> ReferencedBy) BuildAdjacency(Solution solution)
    {
        var refs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var referencedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var proj in solution.Projects)
        {
            refs[proj.Name] = [];
            referencedBy.TryAdd(proj.Name, []);
        }

        foreach (var proj in solution.Projects)
        {
            foreach (var pr in proj.ProjectReferences)
            {
                var target = solution.GetProject(pr.ProjectId);
                if (target is null)
                    continue;
                refs[proj.Name].Add(target.Name);
                referencedBy.TryAdd(target.Name, []);
                referencedBy[target.Name].Add(proj.Name);
            }
        }

        return (refs, referencedBy);
    }
}
