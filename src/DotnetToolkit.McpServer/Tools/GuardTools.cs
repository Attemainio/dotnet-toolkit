using System.ComponentModel;
using DotnetToolkit.McpServer.Hooks;
using DotnetToolkit.McpServer.Workspace;
using ModelContextProtocol.Server;

namespace DotnetToolkit.McpServer.Tools;

/// <summary>Turns this repo's C# guard hooks off for a bounded period, and back on.</summary>
/// <remarks>
/// Separate from <see cref="ServerTools"/> because it is the one tool whose effect is to make the rest of
/// the plugin optional. Keeping it in its own file means the guards' off-switch is never edited as a side
/// effect of touching an unrelated server tool.
/// </remarks>
[McpServerToolType]
public static class GuardTools
{
    [McpServerTool(Name = "set_hook_guards")]
    [Description("Let raw Read/Edit/Write and shell reads (cat/grep/sed) through on .cs files for a while, or "
        + "restore the guards that block them. USE ONLY WHEN THE UNGUARDED PATH IS ITSELF THE POINT: measuring "
        + "these tools against grep/Read, or reproducing what a repo without this plugin does. It is not the way "
        + "past a guard that is in your way — a guard names the skill covering what you were doing, and that "
        + "route is both cheaper and recorded. Edits made while suspended reach disk WITHOUT compiling, without "
        + "a dependent-compile check, and without a development-log entry, so the reasoning is gone when the "
        + "session ends. A suspension expires on its own (default 30 minutes, cap 4 hours) and cannot be made "
        + "indefinite from here, so nothing has to remember to undo it; workspace_status reports the time "
        + "remaining while one is in force. Scoped to your own Claude Code session once a hook has reported "
        + "that session's id, so a different session pointed at this repo keeps its own guards; until one has, "
        + "it covers every session on this repo rather than failing closed. state: suspend | restore.")]
    public static string SetHookGuards(
        SolutionLocator locator,
        [Description("suspend | restore")] string state,
        [Description("How long to suspend for, in minutes. Default 30, capped at 240. Ignored when restoring.")]
        int? minutes = null)
    {
        var normalized = state.Trim().ToLowerInvariant();
        if (normalized is not ("suspend" or "restore"))
        {
            return $"unknown state: {state} (use suspend|restore)";
        }

        var sessionId = GuardSuspension.CurrentSessionId();
        var confirmed = GuardSuspension.SessionIdIsConfirmed;

        if (normalized == "restore")
        {
            // Resume clears the scoped file AND the unscoped one, so a suspension taken before any hook had
            // reported the live id is lifted by the same call. Resume reports false when the environment
            // variable is what is holding the guards open, because clearing a state file it does not depend
            // on would otherwise read as a restore that happened.
            return GuardSuspension.Resume(locator.Root, sessionId)
                ? "hook guards restored"
                : $"state file cleared, but the guards stay open: {GuardSuspension.DisableVariable} is set in "
                    + "the server's own environment, and only restarting the server without it closes them";
        }

        var requested = minutes is { } value
            ? TimeSpan.FromMinutes(value)
            : GuardSuspension.DefaultDuration;
        var now = DateTimeOffset.UtcNow;
        var until = GuardSuspension.Suspend(locator.Root, requested, now, sessionId);

        // A suspension only lifts a guard if the hook process can FIND the file this wrote, and a hook looks
        // under the session id it sees itself. That match is guaranteed only once a hook has reported one;
        // until then this process's inherited id may be stale, and a scoped-only write fails closed and
        // silently - the exact failure that made this tool unusable on any resumed session. Write the
        // unscoped file hooks fall back to as well, and say so, rather than claim a scope never verified.
        if (!confirmed)
        {
            GuardSuspension.Suspend(locator.Root, requested, now);
        }

        var scope = confirmed
            ? "for this Claude Code session only"
            : "for every session pointed at this repo (no hook has reported a live session id yet, so scoping "
                + "to this one could not be verified)";

        return $"hook guards suspended for {(until - now).TotalMinutes:F0} min, until {until:u}, {scope}. "
            + "Raw .cs reads and edits now pass unchecked, and any edit made through them is absent from the "
            + "development log. Call set_hook_guards(state: \"restore\") as soon as the unguarded work is done.";
    }
}
