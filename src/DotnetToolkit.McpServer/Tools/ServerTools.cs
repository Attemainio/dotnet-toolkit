using System.ComponentModel;
using System.Text;

using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Workspace;
using ModelContextProtocol.Server;

namespace DotnetToolkit.McpServer.Tools;

/// <summary>Process-level tools: health check, output-format selection, and workspace/index lifecycle.</summary>
[McpServerToolType]
public static class ServerTools
{
    // Read off the assembly rather than written here, so it cannot drift from the version the build
    // stamps in. A hardcoded literal already did: it still said 0.1.0 after the manifest reached 1.0.0.
    private static readonly string PluginVersion =
        typeof(ServerTools).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    [McpServerTool(Name = "ping")]
    [Description("Is the server alive — health check for this MCP server; returns pong and the server version. Use it "
        + "when calls are failing, hanging or timing out and you need to know whether the server is responding "
        + "at all before diagnosing anything else.")]
    public static string Ping() => $"pong dotnet-toolkit/{PluginVersion}";

    [McpServerTool(Name = "set_output_format")]
    [Description("Change how tool responses are encoded for the rest of this session — switch the output format to "
        + "plain JSON instead of the default TOON, or back. json (pretty-printed), compact (minified JSON), or "
        + "toon (Token-Oriented Object Notation, the default — same data and the same field names, far fewer "
        + "tokens). Persists until changed again or the server restarts.")]
    public static string SetOutputFormat([Description("json | compact | toon")] string format)
    {
        var normalized = format.Trim().ToLowerInvariant();
        if (normalized is not ("json" or "compact" or "toon"))
            return $"unknown format: {format} (use json|compact|toon)";
        Formats.Current = Formats.Parse(normalized);
        return $"output format set to {Formats.Current.ToString().ToLowerInvariant()}";
    }

    [McpServerTool(Name = "workspace_status")]
    [Description("Is the workspace ready, is indexing done, is the solution loaded — status of the code index and the "
        + "MSBuild workspace: target root, solution, load progress, which projects loaded or failed, and any "
        + "load diagnostics. Call it FIRST in a session, whenever a semantic tool reports the workspace is not "
        + "ready or returns limitedBy index_only/stale/degraded, and before trusting a zero-hit result as a "
        + "real absence. Also returns pluginRoot, the plugin's installation directory - join it with "
        + "standards/<name>.md or docs/tools/<tool>.md to reach the files that ship with the plugin, which "
        + "nothing else can name because ${CLAUDE_PLUGIN_ROOT} is not expanded inside a rule or an agent "
        + "definition.")]
    public static string WorkspaceStatus(SolutionLocator locator, ProjectIndex index, WorkspaceHost workspace)
    {
        var sb = new StringBuilder();
        sb.Append("root: ").Append(locator.Root).Append('\n');

        // The session cannot name this itself: ${CLAUDE_PLUGIN_ROOT} is substituted into .mcp.json args,
        // hook commands and skill content, but never into a rule file or an agent definition. Reporting
        // it here is what lets an always-loaded rule cite standards/ and docs/tools/ by bare filename.
        sb.Append("pluginRoot: ").Append(PluginLocation.Resolve(locator.Root)).Append('\n');

        // Reported here rather than only from set_hook_guards, because this is the call every skill makes
        // first. A session that inherits a suspension someone else started would otherwise look entirely
        // normal while raw Read/Edit passed unchecked, and the edits it made would be missing from the log
        // with nothing anywhere saying why. Absent when the guards are active, so it costs a line only when
        // it is telling you something.
        var guards = Hooks.GuardSuspension.Current(locator.Root, DateTimeOffset.UtcNow, Hooks.GuardSuspension.CurrentSessionId());
        if (guards.Suspended)
        {
            sb.Append("hookGuards: SUSPENDED");
            if (guards.Until is { } until)
                sb.Append(" for another ").Append((int)(until - DateTimeOffset.UtcNow).TotalMinutes).Append(" min");
            else
                sb.Append(" by ").Append(Hooks.GuardSuspension.DisableVariable);
            sb.Append(" - raw .cs reads and edits pass unchecked, and edits made through them are absent ")
                .Append("from the development log\n");
        }

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
                // Naming a project in the loaded list AND in a failure leaves the caller unable to tell
                // which one is broken. Mark the ones a diagnostic mentions. This is a name-containment
                // check, not a parse of MSBuild's message format: a miss simply leaves the name
                // unmarked, so a format change degrades to the previous behaviour rather than lying.
                sb.Append("\n  loaded: ").Append(string.Join(", ", workspace.ProjectNames.Select(name =>
                    diags.Any(d => d.Contains(name, StringComparison.OrdinalIgnoreCase))
                        ? name + " (FAILED — results incomplete)"
                        : name)));
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
    [Description("Refresh, reload and re-index the workspace after external changes — after a git pull, checkout, "
        + "branch switch, merge or rebase, or after a .cs file was added, deleted, moved or renamed. Call it "
        + "when results look stale, when a newly created file is not found, or when a response reports "
        + "limitedBy: stale. scope: 'index' re-scans the file index, 'workspace' re-opens the MSBuild solution "
        + "and rebuilds the SQLite symbol index, 'all' does both — and adding or removing a file needs both.")]
    public static async Task<string> ReloadWorkspace(
        ProjectIndex index,
        WorkspaceHost workspace,
        SymbolIndexBuilder indexBuilder,
        [Description("index (re-scan the file index only) | workspace (re-open the MSBuild solution and rebuild the symbol index) | all (both - what a git pull, or an added or deleted .cs file, needs).")] string scope = "all")
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
            indexBuilder.Start();
            actions.Add("workspace reload started in background, symbol index rebuild queued (check workspace_status)");
        }
        return actions.Count > 0 ? string.Join("; ", actions) : $"unknown scope: {scope} (use index|workspace|all)";
    }
}
