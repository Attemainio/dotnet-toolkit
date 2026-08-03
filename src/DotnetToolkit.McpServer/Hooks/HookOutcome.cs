namespace DotnetToolkit.McpServer.Hooks;

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
}
