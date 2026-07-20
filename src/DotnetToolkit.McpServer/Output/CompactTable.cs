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
/// </summary>
public sealed record CompactTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows)
{
    public static CompactTable Of<T>(
        IReadOnlyList<string> columns, IEnumerable<T> items, Func<T, IReadOnlyList<object?>> toRow) =>
        new(columns, items.Select(toRow).ToList());
}
