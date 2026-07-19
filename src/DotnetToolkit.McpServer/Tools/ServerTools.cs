using System.ComponentModel;
using System.Text;
using DotnetToolkit.McpServer.Devlog;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Workspace;
using ModelContextProtocol.Server;

namespace DotnetToolkit.McpServer.Tools;

[McpServerToolType]
public static class ServerTools
{
    [McpServerTool(Name = "ping")]
    [Description("Health check; returns pong and the server version.")]
    public static string Ping() => "pong dotnet-toolkit/0.1.0";

    [McpServerTool(Name = "workspace_status")]
    [Description("Status of the code index and the MSBuild workspace: target root, solution, load progress, and any load diagnostics. Call this when a semantic tool reports the workspace is not ready.")]
    public static string WorkspaceStatus(SolutionLocator locator, ProjectIndex index, WorkspaceHost workspace)
    {
        var sb = new StringBuilder();
        sb.Append("root: ").Append(locator.Root).Append('\n');

        // Ambiguity is a decision the server refuses to make, not a missing solution. Say so with the
        // candidates and the exact fix, and make the workspace line below point back here rather than
        // reporting a bare "nosolution" that reads as "this repo has none".
        var ambiguous = locator.WorkspaceEntry is null && locator.IsAmbiguous;
        if (locator.WorkspaceEntry is { } entry)
            sb.Append("solution: ").Append(locator.RelPath(entry)).Append('\n');
        else if (ambiguous)
            sb.Append("solution: AMBIGUOUS — ").Append(locator.Candidates.Count)
              .Append(" candidates found, so none was chosen. Pick one by writing "
                    + "{\"solution\": \"<path>\"} to .claude/dotnet-toolkit/config.json, then call "
                    + "reload_workspace. Candidates: ")
              .Append(string.Join("; ", locator.Candidates.Select(locator.RelPath))).Append('\n');
        else
            sb.Append("solution: none found (structure tools still work; semantic tools need one)\n");

        sb.Append("index: ").Append(index.State)
          .Append(' ').Append(index.FileCount).Append(" files, ").Append(index.TypeCount).Append(" types\n");

        var diags = workspace.LoadDiagnostics;
        sb.Append("workspace: ").Append(workspace.State.ToString().ToLowerInvariant());
        switch (workspace.State)
        {
            case WorkspaceState.Loading:
                sb.Append(" (").Append((int)workspace.LoadElapsed.TotalSeconds).Append("s elapsed)");
                break;

            case WorkspaceState.Loaded:
                sb.Append(' ').Append(workspace.ProjectCount).Append(" projects in ")
                  .Append(workspace.LoadElapsed.TotalSeconds.ToString("F1")).Append('s');
                // "loaded" alongside a load failure is technically true and useless: the caller cannot
                // tell which results to trust. Mark it degraded and name the projects either way.
                if (diags.Count > 0)
                    sb.Append(" — DEGRADED: ").Append(diags.Count)
                      .Append(diags.Count == 1 ? " project failed to load" : " projects failed to load")
                      .Append("; semantic results for those are incomplete");
                sb.Append("\n  loaded: ").Append(string.Join(", ", workspace.ProjectNames));
                break;

            case WorkspaceState.NoSolution when ambiguous:
                sb.Append(" — no solution was chosen (see the ambiguity above); "
                        + "semantic tools are unavailable until one is configured");
                break;
        }
        sb.Append('\n');

        if (diags.Count > 0)
        {
            sb.Append("load diagnostics (").Append(diags.Count).Append("):\n");
            foreach (var d in diags.Take(5))
                sb.Append("  ").Append(Output.CompactFormatter.Truncate(d, 200)).Append('\n');
            if (diags.Count > 5)
                sb.Append("  …+").Append(diags.Count - 5).Append(" more\n");
        }

        return sb.ToString().TrimEnd('\n');
    }

    [McpServerTool(Name = "reload_workspace")]
    [Description("Force a re-scan after large external changes (e.g. git checkout/pull). scope: 'index' re-scans the file index, 'workspace' re-opens the MSBuild solution, 'all' does both.")]
    public static async Task<string> ReloadWorkspace(
        ProjectIndex index,
        WorkspaceHost workspace,
        [Description("index | workspace | all")] string scope = "all")
    {
        var s = scope.Trim().ToLowerInvariant();
        var actions = new List<string>();
        if (s is "index" or "all")
        {
            await index.ForceRescanAsync();
            actions.Add($"index re-scanned ({index.FileCount} files, {index.TypeCount} types)");
        }
        if (s is "workspace" or "all")
        {
            workspace.TriggerReload();
            actions.Add("workspace reload started in background (check workspace_status)");
        }
        return actions.Count > 0 ? string.Join("; ", actions) : $"unknown scope: {scope} (use index|workspace|all)";
    }
}
