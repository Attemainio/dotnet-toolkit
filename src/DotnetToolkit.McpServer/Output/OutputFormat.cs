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
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions IndentedJsonOptions = new(JsonOptions) { WriteIndented = true };

    /// <summary>
    /// The active output format for the rest of this server process. Seeded once at startup from
    /// <c>ToolkitConfig.DefaultFormat</c> (<c>Program.cs</c>); the <c>set_output_format</c> tool
    /// (<see cref="Tools.ServerTools"/>) is the only other writer, so a session can change it without a
    /// restart. Deliberately a single process-wide setting, not a per-call argument or per-repo-only
    /// config: this server serves one target repo per process, and every response an LLM ever reads goes
    /// through the same encoding choice.
    /// </summary>
    public static OutputFormat Current { get; set; } = OutputFormat.Toon;

    public static OutputFormat Parse(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "compact" => OutputFormat.Compact,
        "json" => OutputFormat.Json,
        _ => OutputFormat.Toon,
    };

    public static string ToJson(object value) => JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>
    /// Same encoding as <see cref="ToJson"/> but returns a JsonElement instead of a string — for building
    /// a larger envelope out of pieces without re-parsing JSON text back out of itself.
    /// </summary>
    public static JsonElement ToElement(object value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    /// <summary>
    /// The single point every tool's final response goes through, keyed on <see cref="Current"/> rather
    /// than a per-call argument. <see cref="OutputFormat.Toon"/> (the default) goes through the same
    /// <see cref="ToElement"/> used to build nested pieces elsewhere, so the TOON encoder sees exactly the
    /// JSON we already trust rather than re-deriving its own property-naming/null-handling conventions
    /// from the raw C# object graph. Callers pass a PLAIN object graph (no manual columns/rows shaping) —
    /// ToonEncoder's own uniform-array detection tabulates a list of same-shaped objects natively, more
    /// cheaply than a hand-rolled {columns,rows} wrapper would (verified: the wrapper form costs more
    /// tokens once TOON-encoded, not fewer). <see cref="OutputFormat.Compact"/>/<see cref="OutputFormat.Json"/>
    /// serialize that same plain graph as ordinary JSON — without the old column-name deduplication, since
    /// TOON is now the token-optimized path and JSON is the fallback/debugging one.
    /// </summary>
    public static string Render(object envelope) => Current switch
    {
        OutputFormat.Toon => ToonEncoder.Encode(ToElement(envelope)),
        OutputFormat.Json => JsonSerializer.Serialize(envelope, IndentedJsonOptions),
        _ => ToJson(envelope),
    };
}
