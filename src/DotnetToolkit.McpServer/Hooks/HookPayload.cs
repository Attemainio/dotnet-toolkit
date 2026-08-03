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
            if (root.TryGetProperty("tool_input", out var input) && input.ValueKind == JsonValueKind.Object)
            {
                filePath = ReadString(input, "file_path");
                command = ReadString(input, "command");
            }

            return new HookPayload(toolName, filePath, command);
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
