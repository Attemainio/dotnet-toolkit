using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// <c>PostToolUse</c> hook on <c>Write</c>: brings the syntax index and MSBuild workspace up to date
/// after a brand-new <c>.cs</c> file appears, and tells the agent what state they are in.
/// </summary>
/// <remarks>
/// Change detection is mtime-polling rather than a filesystem watcher, so a newly created file is
/// invisible to both tiers until a sweep runs. This hook does not merely remind the agent to call
/// <c>reload_workspace</c> — a reminder that depended on being followed, and was not reliably — it
/// triggers the work itself over the loopback control channel.
/// <para>
/// <c>rescan</c> is synchronous and cheap (syntax index only, no MSBuild), so the index already knows
/// about the file by the time this returns. <c>reload</c> only starts the MSBuild reload and returns
/// immediately, since that can outlast the hook's timeout; the injected message therefore tells the
/// agent to check <c>workspace_status</c> rather than assume the reload finished.
/// </para>
/// </remarks>
internal static class ReloadHint
{
    /// <summary>Refreshes the server's view of a newly written file and builds the agent's context note.</summary>
    /// <param name="payload">The parsed hook payload.</param>
    /// <param name="context">The repo root whose control channel to talk to.</param>
    /// <returns>
    /// An allow outcome carrying <c>additionalContext</c> hook JSON, or a plain allow when the written
    /// file is not C#. Falls back to reminder text when the control channel is unreachable.
    /// </returns>
    public static async Task<HookOutcome> EvaluateAsync(HookPayload payload, HookContext context)
    {
        var file = payload.FilePath;
        if (payload.ToolName != "Write"
            || string.IsNullOrEmpty(file)
            || !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return HookOutcome.Allow;
        }

        var rescan = await ControlClient.SendAsync(context.Root, "rescan");
        var reload = rescan is null ? null : await ControlClient.SendAsync(context.Root, "reload");

        var message = rescan is not null && reload is not null
            ? $"{file} is a new .cs file. Control channel: {rescan}; {reload} - call workspace_status before "
                + "validate_patch/get_symbol on this file to confirm the workspace reload has finished."
            : $"{file} is a new .cs file. The syntax index and MSBuild workspace do not know about it yet "
                + "(mtime-polling, not a filesystem watcher) - call reload_workspace(scope: \"all\") and wait for "
                + "workspace_status to report loaded before validate_patch or get_symbol on this file, or the call "
                + "will fail with invalid_edit: file is not part of the loaded solution.";

        var output = new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PostToolUse",
                ["additionalContext"] = message,
            },
        };

        return HookOutcome.AllowWith(output.ToJsonString(JsonSerializerOptions.Default));
    }
}
