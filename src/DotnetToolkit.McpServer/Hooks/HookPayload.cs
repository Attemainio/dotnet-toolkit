using System.Text.Json;

namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// The fields these hooks read out of the JSON object Claude Code writes to a hook's stdin.
/// </summary>
/// <remarks>
/// The shell versions shelled out to <c>node</c>, then <c>python3</c>, then <c>jq</c> to do this,
/// because none of the three is guaranteed present on a consumer machine. That chain also had a
/// silent failure mode: Windows' Microsoft Store <c>python3</c> alias stub resolves on
/// <c>PATH</c>, exits 0, and prints nothing, so extraction "succeeded" with empty fields and every
/// guard fell through to its allow branch. <c>System.Text.Json</c> ships with the runtime the server
/// already requires, so there is no interpreter to probe for and no stub to be fooled by.
/// </remarks>
/// <param name="ToolName">The <c>tool_name</c> field — which tool is about to run, or just ran.</param>
/// <param name="FilePath">
/// <c>tool_input.file_path</c>, present for <c>Read</c>/<c>Edit</c>/<c>Write</c>. Null when the tool
/// does not take one.
/// </param>
/// <param name="Command">
/// <c>tool_input.command</c>, present for <c>Bash</c>. Null when the tool does not take one.
/// </param>
internal sealed record HookPayload(string ToolName, string? FilePath, string? Command)
{
    /// <summary>
    /// The <c>session_id</c> field, identifying the conversation this hook fired in. Null when the
    /// harness did not supply one, which a once-per-session hook must treat as "cannot tell" rather
    /// than as a new session.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// <c>tool_input</c> as raw JSON — the arguments the model had to generate to make this call, which
    /// are therefore OUTPUT tokens and the dearer half of what a tool costs. Null when the payload
    /// carried no object there.
    /// </summary>
    public string? ToolInputRaw { get; init; }

    /// <summary>
    /// <c>tool_response</c> as raw JSON, present on <c>PostToolUse</c> only — what the call loaded into
    /// the model's context, and therefore INPUT tokens. Kept raw because every tool returns a different
    /// shape and only its size is ever measured.
    /// </summary>
    public string? ToolResponseRaw { get; init; }

    /// <summary>
    /// <c>tool_use_id</c>, the harness's own identifier for this call. It is what lets a metered row be
    /// reconciled against the transcript, and what makes recording idempotent when a hook is delivered
    /// twice.
    /// </summary>
    public string? ToolUseId { get; init; }

    /// <summary><c>agent_id</c> — present only when the call was made from inside a subagent.</summary>
    public string? AgentId { get; init; }

    /// <summary>
    /// <c>agent_type</c>, e.g. <c>dotnet-perf-raw-probe</c>. This is what attributes a metered call to one
    /// side of a benchmark without the probes having to label themselves — which they cannot be trusted to
    /// do, since a self-reported call log is precisely what this measurement replaces.
    /// </summary>
    public string? AgentType { get; init; }

    /// <summary>Parses a hook stdin payload, tolerating any shape that is not the expected object.</summary>
    /// <param name="json">The raw stdin text.</param>
    /// <returns>
    /// The parsed payload, or null when the text is not JSON or carries no <c>tool_name</c> — both of
    /// which mean "allow", per these hooks' fail-open posture.
    /// </returns>
    public static HookPayload? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var toolName = ReadString(root, "tool_name");
            if (toolName is null)
            {
                return null;
            }

                string? filePath = null;
                string? command = null;
                string? toolInput = null;
                if (root.TryGetProperty("tool_input", out var input) && input.ValueKind == JsonValueKind.Object)
                {
                    filePath = ReadString(input, "file_path");
                    command = ReadString(input, "command");

                    // Raw rather than parsed: the meter needs only its size, and every tool takes a
                    // different input shape, so there is nothing general to parse it into.
                    toolInput = input.GetRawText();
                }

                return new HookPayload(toolName, filePath, command)
                {
                    SessionId = ReadString(root, "session_id"),
                    ToolInputRaw = toolInput,
                    ToolResponseRaw = root.TryGetProperty("tool_response", out var response)
                        ? response.GetRawText()
                        : null,
                    ToolUseId = ReadString(root, "tool_use_id"),
                    AgentId = ReadString(root, "agent_id"),
                    AgentType = ReadString(root, "agent_type"),
                };
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
