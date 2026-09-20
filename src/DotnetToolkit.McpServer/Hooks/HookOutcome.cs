namespace DotnetToolkit.McpServer.Hooks;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// What a hook handler decided: the process exit code, plus any text to emit on stderr or stdout.
/// </summary>
/// <remarks>
/// Handlers return this instead of writing to the console themselves so the decision is a pure
/// function of the payload and the context, and can be asserted in tests. <see cref="HookCli"/> owns
/// the actual I/O. The shell scripts this replaced could only be tested by running them.
/// </remarks>
/// <param name="ExitCode">
/// 0 to allow (Claude Code proceeds), 2 to deny a <c>PreToolUse</c> call and feed
/// <paramref name="Stderr"/> back to the agent.
/// </param>
/// <param name="Stderr">Text for stderr — the denial explanation the agent reads. Null when allowing.</param>
/// <param name="Stdout">
/// Text for stdout — hook JSON such as a <c>PostToolUse</c> <c>additionalContext</c> payload. Null
/// when the hook has nothing to inject.
/// </param>
internal readonly record struct HookOutcome(int ExitCode, string? Stderr = null, string? Stdout = null)
{
    /// <summary>Allow the tool call, saying nothing.</summary>
    public static HookOutcome Allow { get; } = new(0);

    /// <summary>Deny the tool call, feeding <paramref name="explanation"/> back to the agent.</summary>
    /// <param name="explanation">Why the call was blocked, and what to do instead.</param>
    /// <returns>An outcome with exit code 2 and the explanation on stderr.</returns>
    public static HookOutcome Deny(string explanation) => new(2, Stderr: explanation);

    /// <summary>Allow the tool call while injecting <paramref name="json"/> as hook output.</summary>
    /// <param name="json">A serialized Claude Code hook-output object.</param>
    /// <returns>An outcome with exit code 0 and the JSON on stdout.</returns>
    public static HookOutcome AllowWith(string json) => new(0, Stdout: json);

    /// <summary>Allow a <c>PreToolUse</c> call while surfacing non-blocking advice to the agent.</summary>
    /// <param name="reason">Why a different tool would likely serve this call better, without denying it.</param>
    /// <returns>An outcome with exit code 0 and a <c>PreToolUse</c> <c>additionalContext</c> payload on stdout.</returns>
    /// <remarks>
    /// The advice travels as <c>additionalContext</c>, the one field of a PreToolUse allow that the model
    /// reads. Through 2026-09-20 it was sent as <c>permissionDecision: "allow"</c> plus a
    /// <c>permissionDecisionReason</c>, and a run against PandaAI that day established that the harness
    /// shows that reason to the user and never to the model — every "advised and ignored" finding before
    /// it was an advisory nobody had received. The decision field is dropped as well as replaced: an
    /// explicit allow from a hook also waives the user's own permission prompt for the call, which for
    /// a <c>Bash</c> command whose first segment happens to read a compiled file meant auto-approving
    /// whatever came after the semicolon. Advice must never widen what a call may do.
    /// </remarks>
    public static HookOutcome AllowAdvising(string reason)
    {
        var json = new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["additionalContext"] = reason,
            },
        };
        return new HookOutcome(0, Stdout: json.ToJsonString(JsonSerializerOptions.Default));
    }
}
