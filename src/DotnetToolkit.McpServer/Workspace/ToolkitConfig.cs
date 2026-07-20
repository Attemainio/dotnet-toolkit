using System.Text.Json.Serialization;

namespace DotnetToolkit.McpServer.Workspace;

/// <summary>Per-repo settings loaded from .claude/dotnet-toolkit/config.json (all optional).</summary>
public sealed class ToolkitConfig
{
    [JsonPropertyName("solution")]
    public string? Solution { get; set; }

    [JsonPropertyName("devlogDir")]
    public string DevlogDir { get; set; } = "devlog";

    [JsonPropertyName("excludeGlobs")]
    public string[] ExcludeGlobs { get; set; } = [];

    [JsonPropertyName("defaultFormat")]
    public string DefaultFormat { get; set; } = "toon";
}
