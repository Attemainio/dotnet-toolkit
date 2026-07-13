using System.Text.Json.Serialization;

namespace DotnetToolkit.McpServer.Devlog;

/// <summary>Machine-readable metadata embedded per entry as an HTML comment in the weekly .md file.</summary>
public sealed class DevlogMeta
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("ts")] public DateTimeOffset Ts { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "done";
    [JsonPropertyName("classes")] public string[] Classes { get; set; } = [];
    [JsonPropertyName("ensemble")] public string? Ensemble { get; set; }
    [JsonPropertyName("tags")] public string[] Tags { get; set; } = [];
}

/// <summary>One parsed entry of a weekly devlog file.</summary>
public sealed record DevlogEntry(
    string Id,
    DateTimeOffset Ts,
    string Title,
    string Status,
    string[] Classes,
    string? Ensemble,
    string[] Tags,
    string File,
    string Markdown);

public sealed class DevlogIndexEntry
{
    public string Id { get; set; } = "";
    public DateTimeOffset Ts { get; set; }
    public string File { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "done";
    public string[] Classes { get; set; } = [];
    public string? Ensemble { get; set; }
    public string[] Tags { get; set; } = [];
    public Dictionary<string, int> Terms { get; set; } = new(StringComparer.Ordinal);
}

public sealed class DevlogIndexDoc
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public Dictionary<string, long> Files { get; set; } = new(StringComparer.Ordinal);
    public List<DevlogIndexEntry> Entries { get; set; } = [];
}
