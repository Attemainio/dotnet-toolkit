using System.Text.Encodings.Web;
using System.Text.Json;
using Cysharp.AI;

namespace DotnetToolkit.McpServer.Output;

public enum OutputFormat
{
    Toon,
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

    private static readonly JsonSerializerOptions IndentedJsonOptions = new(JsonOptions) { WriteIndented = true };

    public static OutputFormat Parse(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "compact" => OutputFormat.Compact,
        "json" => OutputFormat.Json,
        _ => OutputFormat.Toon,
    };

    public static string ToJson(object value) => JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>
    /// Same encoding as <see cref="ToJson"/> but returns a JsonElement instead of a string — for building
    /// a larger envelope out of pieces (e.g. JsonHoist over a list of items) without re-parsing JSON text
    /// back out of itself.
    /// </summary>
    public static JsonElement ToElement(object value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    /// <summary>
    /// The single point every tool's final response goes through. All three <see cref="OutputFormat"/>
    /// values encode the identical envelope object a tool already built — <see cref="Output.CompactTable"/>
    /// and <see cref="JsonHoist"/> shapes included — so switching formats never changes what a caller can
    /// read out of a response, only how many tokens it costs to say it. <see cref="OutputFormat.Toon"/>
    /// (the default) goes through the same <see cref="ToElement"/> used to build nested pieces elsewhere,
    /// so the TOON encoder sees exactly the JSON we already trust rather than re-deriving its own
    /// property-naming/null-handling conventions from the raw C# object graph.
    /// </summary>
    public static string Render(object envelope, OutputFormat format) => format switch
    {
        OutputFormat.Toon => ToonEncoder.Encode(ToElement(envelope)),
        OutputFormat.Json => JsonSerializer.Serialize(envelope, IndentedJsonOptions),
        _ => ToJson(envelope),
    };
}
