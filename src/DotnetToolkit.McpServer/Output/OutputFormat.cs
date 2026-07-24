using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// from the raw C# object graph, then <see cref="RenderToon"/> swaps any [{line, text}] source-line
    /// array for a raw block (see its own doc comment for why). Callers pass a PLAIN object graph (no
    /// manual columns/rows shaping) — ToonEncoder's own uniform-array detection tabulates a list of
    /// same-shaped objects natively, more cheaply than a hand-rolled {columns,rows} wrapper would
    /// (verified: the wrapper form costs more tokens once TOON-encoded, not fewer).
    /// <see cref="OutputFormat.Compact"/>/<see cref="OutputFormat.Json"/> serialize that same plain graph
    /// as ordinary JSON — without the old column-name deduplication, since TOON is now the token-optimized
    /// path and JSON is the fallback/debugging one — and keep [{line, text}] structured, since JSON has no
    /// literal-block equivalent and a caller asking for strict JSON needs it to stay parseable.
    /// </summary>
    public static string Render(object envelope) => Current switch
    {
        OutputFormat.Toon => RenderToon(envelope),
        OutputFormat.Json => JsonSerializer.Serialize(envelope, IndentedJsonOptions),
        _ => ToJson(envelope),
    };

    /// <summary>
    /// TOON's tabular array-of-objects encoding quotes/escapes any string containing a comma or an
    /// embedded quote to keep a delimited row unambiguous — which is nearly every real C# source line,
    /// so a symbol's own source read back as a wall of \n/\" noise. A prior fix tried a blanket
    /// find/replace unescape of the whole rendered TOON text; that also unescapes an embedded quote in
    /// any OTHER short string field still living inside a quoted TOON cell, corrupting it (an unescaped
    /// quote inside a still-quoted cell is not valid TOON) — presumably why it did not survive. This
    /// instead structurally detects the one shape that actually needs it — an array of <see
    /// cref="ContextTools.SourceLine"/>-equivalent {line, text} objects, however deeply nested (get_symbol's
    /// source, or each item's content under get_references' includeBodies) — swaps each one for a unique
    /// token before encoding, then splices in a raw, unescaped "line: text" block at that token's own
    /// indentation after encoding. Every other field goes through ToonEncoder completely unchanged.
    /// </summary>
    private static string RenderToon(object envelope)
    {
        var node = JsonNode.Parse(ToJson(envelope))!;
        var blocks = new List<(string Key, string Raw)>();
        ExtractSourceLineBlocks(node, blocks);
        var toon = ToonEncoder.Encode(node.Deserialize<JsonElement>());
        if (blocks.Count == 0)
            return toon;

        var lines = toon.Split('\n').ToList();
        for (var i = 0; i < blocks.Count; i++)
        {
            var idx = lines.FindIndex(l => l.Contains(SourceBlockToken(i)));
            if (idx < 0)
                continue;
            var indent = lines[idx][..(lines[idx].Length - lines[idx].TrimStart().Length)];
            var rawLines = blocks[i].Raw.Split('\n').Select(l => indent + "  " + l);
            lines.RemoveAt(idx);
            lines.InsertRange(idx, new[] { $"{indent}{blocks[i].Key}:" }.Concat(rawLines));
        }
        return string.Join('\n', lines);
    }

    private static string SourceBlockToken(int index) => $"__DOTNET_TOOLKIT_SRC_BLOCK_{index}__";

    private static void ExtractSourceLineBlocks(JsonNode? node, List<(string Key, string Raw)> blocks)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (TryRenderSourceLineArray(obj[key], out var raw))
                    {
                        obj[key] = SourceBlockToken(blocks.Count);
                        blocks.Add((key, raw));
                    }
                    else
                    {
                        ExtractSourceLineBlocks(obj[key], blocks);
                    }
                }
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    ExtractSourceLineBlocks(item, blocks);
                break;
        }
    }

    /// <summary>True (with the rendered block) only for an array whose every element is exactly a
    /// <c>{line: number, text: string}</c> pair — the shape <see cref="ContextTools.SourceLine"/>
    /// serializes to — never misfiring on an unrelated array that merely has a numeric and a string
    /// field under different names.</summary>
    private static bool TryRenderSourceLineArray(JsonNode? node, out string raw)
    {
        raw = "";
        if (node is not JsonArray arr || arr.Count == 0)
            return false;
        var sb = new StringBuilder();
        foreach (var item in arr)
        {
            if (item is not JsonObject o || o.Count != 2
                || o["line"] is not JsonValue lineVal || !lineVal.TryGetValue<int>(out var line)
                || o["text"] is not JsonValue textVal || textVal.GetValueKind() != JsonValueKind.String)
                return false;
            sb.Append(line).Append(": ").Append((string)textVal!).Append('\n');
        }
        if (sb.Length > 0)
            sb.Length--;
        raw = sb.ToString();
        return true;
    }
}
