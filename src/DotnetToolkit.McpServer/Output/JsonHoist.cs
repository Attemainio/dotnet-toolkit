using System.Text.Json;

namespace DotnetToolkit.McpServer.Output;

/// <summary>
/// Finds which top-level properties are present, with a value, on EVERY element of a list of JSON objects,
/// and factors those out into their own columns — leaving each element's remaining properties untouched in
/// a per-row remainder. Generic and tool-agnostic by design: it does not know what a symbol or a hit is,
/// only that a batch of same-shaped JSON objects can widen a <see cref="CompactTable"/> further than
/// whatever columns the caller already declared as fixed.
///
/// The result is genuinely data-dependent — which properties turn out to be common depends on which items
/// are actually in THIS call, not on the tool's contract (e.g. a get_symbol batch of five methods commonly
/// has more hoistable fields than a batch mixing methods and types, since a type has no containingType).
/// That is a real difference from CompactTable's own fixed columns, and the reason it is safe here despite
/// CompactTable's doc comment warning against reflection-driven shapes: the hoisted set is reported back in
/// <c>columns</c> on every single call, so a caller never has to guess it — the response is self-describing
/// rather than silently different underneath unrelated code elsewhere.
/// </summary>
public static class JsonHoist
{
    /// <summary>
    /// Splits each element into the values of whatever properties are common to every element (in the
    /// first element's own property order, since HashSet intersection has none of its own) and a remainder
    /// holding what was not common. A null element (nothing to hoist from — e.g. a batch row that failed
    /// before producing this kind of content) hoists as null in every column and passes through unchanged.
    /// </summary>
    public static (IReadOnlyList<string> CommonKeys, IReadOnlyList<IReadOnlyList<object?>> HoistedValues,
        IReadOnlyList<JsonElement?> Remainder) Split(IReadOnlyList<JsonElement?> elements)
    {
        var objects = elements.Where(e => e is { ValueKind: JsonValueKind.Object }).Select(e => e!.Value).ToList();

        var common = objects.Count == 0
            ? []
            : objects
                .Select(o => o.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal))
                .Aggregate((a, b) => { a.IntersectWith(b); return a; });
        // First element's own declaration order, not alphabetical — an intersecting HashSet has no order
        // of its own, and the source objects' order (e.g. kind, displayString, accessibility, ...) reads
        // better than one imposed after the fact.
        var orderedCommon = objects.Count == 0
            ? []
            : objects[0].EnumerateObject().Select(p => p.Name).Where(common.Contains).ToList();

        var hoisted = new List<IReadOnlyList<object?>>(elements.Count);
        var remainder = new List<JsonElement?>(elements.Count);
        foreach (var element in elements)
        {
            if (element is not { ValueKind: JsonValueKind.Object } obj)
            {
                hoisted.Add(orderedCommon.Select(_ => (object?)null).ToList());
                remainder.Add(element);
                continue;
            }

            hoisted.Add(orderedCommon.Select(k => (object?)obj.GetProperty(k)).ToList());

            var rest = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var prop in obj.EnumerateObject())
                if (!common.Contains(prop.Name))
                    rest[prop.Name] = prop.Value;
            remainder.Add(rest.Count == 0 ? null : JsonSerializer.SerializeToElement(rest));
        }

        return (orderedCommon, hoisted, remainder);
    }
}
