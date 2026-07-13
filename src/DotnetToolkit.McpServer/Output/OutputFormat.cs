using System.Text.Json;

namespace DotnetToolkit.McpServer.Output;

public enum OutputFormat
{
    Compact,
    Json,
}

public static class Formats
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static OutputFormat Parse(string? format) =>
        format?.Trim().ToLowerInvariant() == "json" ? OutputFormat.Json : OutputFormat.Compact;

    public static string ToJson(object value) => JsonSerializer.Serialize(value, JsonOptions);
}
