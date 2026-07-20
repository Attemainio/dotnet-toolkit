namespace DotnetToolkit.McpServer.Output;

/// <summary>
/// A list of same-shaped rows, serialized as a header once plus one array per row instead of repeating
/// every field name on every element. Still plain JSON — <c>columns</c> is a string array, each row is a
/// JSON array in that column order — so a JSON-aware reader gets real arrays back with no delimiter or
/// escaping rules of its own to apply; the token saving comes only from not re-stating field names per row.
///
/// Column order is whatever the caller passes to <see cref="Of"/>; there is no reflection over a DTO
/// shape, so a column can be added or reordered without this type silently changing behaviour underneath
/// an unrelated edit.
///
/// Reach for this only when a response is many rows of the SAME flat, mostly-scalar shape — search_index
/// hits are the case it was built for: five short fields, always present, repeated up to 50 times, where
/// the field names really were a meaningful fraction of the payload. It is the wrong fit for something
/// like a get_symbol batch, where each entry is dominated by a big free-text field (source, xmlDoc) or a
/// nested object whose own shape varies per entry (referenceCounts has different keys for a type than for
/// a member) — forcing that into columns either pads every row with nulls for fields that entry does not
/// have, contradicting get_symbol's own convention that absent means "does not apply" rather than null, or
/// flattens nested paths into column names that break the moment that nested shape changes. In that case
/// the field names are a rounding error next to the content, and a plain array of full objects is both
/// cheaper to get right and cheaper to read.
/// </summary>
public sealed record CompactTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows)
{
    public static CompactTable Of<T>(
        IReadOnlyList<string> columns, IEnumerable<T> items, Func<T, IReadOnlyList<object?>> toRow) =>
        new(columns, items.Select(toRow).ToList());
}
