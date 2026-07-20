using System.Text.Encodings.Web;
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
        // Responses go to an MCP stdio pipe, never into HTML. The default encoder is HTML-injection-safe
        // and escapes < > & ' + and ALL non-ASCII as \uXXXX — six characters, three-to-four tokens, for
        // one. That tax lands hardest on exactly what this server returns: every generic argument
        // (IReadOnlyList<T>), every lambda arrow, every quote inside a returned source body, and
        // every em dash in a doc comment. Measured over this repo's own src tree it is +7.4% characters
        // for zero safety gain on a non-HTML sink.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static OutputFormat Parse(string? format) =>
        format?.Trim().ToLowerInvariant() == "json" ? OutputFormat.Json : OutputFormat.Compact;

    public static string ToJson(object value) => JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>
    /// Same encoding as <see cref="ToJson"/> but returns a JsonElement instead of a string — for building
    /// a larger envelope out of pieces (e.g. JsonHoist over a list of items) without re-parsing JSON text
    /// back out of itself.
    /// </summary>
    public static JsonElement ToElement(object value) => JsonSerializer.SerializeToElement(value, JsonOptions);
}
