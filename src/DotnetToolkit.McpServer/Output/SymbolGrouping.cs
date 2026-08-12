using DotnetToolkit.McpServer.Store;

namespace DotnetToolkit.McpServer.Output;

/// <summary>
/// Groups a flat list of search-index hits into a namespace- or file-first tree so a caller sees one
/// shared header per namespace/file instead of the same value repeated on every row. Collapses to flat
/// header fields plus one symbols table when the whole result set shares a single namespace and file,
/// and hoists a leaf's kind column when every hit in that leaf shares one kind.
/// </summary>
public static class SymbolGrouping
{
    /// <summary>One search hit, already resolved to its namespace/file and reduced to a member-local name.</summary>
    /// <remarks>
    /// <paramref name="Shape"/> is <see cref="SymbolShape"/>'s terse retrieval hint, null on every symbol
    /// small enough that the default get_symbol fetch is already the right next call.
    /// <paramref name="Read"/> is <see cref="ReadAdvice"/>'s answer to what that shape implies — the
    /// include to pass next — and is null under the same condition, for the same reason.
    /// <paramref name="Generated"/> says the declaration is source-generator output, which is the reason
    /// its file and lines are unresolved rather than an indexing failure. <paramref name="Lines"/> is the
    /// <c>@from-to</c> read selector, not a patch span. <paramref name="Refs"/> and
    /// <paramref name="Modifiers"/> are <see cref="RefCode"/>'s and <see cref="ModifierCode"/>'s codes, each
    /// null when the caller left that column out of include -- and, for Refs, also when nothing was measured.
    /// </remarks>
    public sealed record Row(
        string SymbolId, string Kind, string LeafName, string File, string Namespace,
        string? Lines, bool? HasSummary, string? Summary, string? Shape = null,
        DeclarationPlacement Placement = DeclarationPlacement.InTree, string? Read = null,
        string? Refs = null, string? Modifiers = null);

    /// <summary>
    /// Builds the grouped envelope. <paramref name="primaryIsNamespace"/> selects namespace-first
    /// (default) vs. file-first nesting; the other axis always nests one level inside it.
    /// </summary>
    /// <remarks>
    /// Each legend is emitted once at the top, and only when some row actually carries that column — a
    /// result of ordinary small symbols renders exactly as it did before either column existed.
    /// </remarks>
    public static Dictionary<string, object?> Build(IReadOnlyList<Row> rows, bool primaryIsNamespace)
    {
        var shapeLegend = rows.Any(r => r.Shape is not null) ? SymbolShape.Legend : null;
        var readLegend = rows.Any(r => r.Read is not null) ? ReadAdvice.Legend : null;
        var refsLegend = rows.Any(r => r.Refs is not null) ? RefCode.Legend : null;
        var modifiersLegend = rows.Any(r => r.Modifiers is not null) ? ModifierCode.Legend : null;
        var primaryGroups = GroupInOrder(rows, primaryIsNamespace ? r => r.Namespace : r => r.File);
        if (primaryGroups.Count == 1)
        {
            var onlySecondary = GroupInOrder(
                primaryGroups[0].Rows, primaryIsNamespace ? r => r.File : r => r.Namespace);
            if (onlySecondary.Count == 1)
            {
                var flat = new Dictionary<string, object?>();
                if (shapeLegend is not null)
                    flat["shape"] = shapeLegend;
                if (readLegend is not null)
                    flat["read"] = readLegend;
                if (refsLegend is not null)
                    flat["refs"] = refsLegend;
                if (modifiersLegend is not null)
                    flat["modifiers"] = modifiersLegend;
                flat[primaryIsNamespace ? "namespace" : "file"] = primaryGroups[0].Key;
                flat[primaryIsNamespace ? "file" : "namespace"] = onlySecondary[0].Key;
                AddLeaf(flat, onlySecondary[0].Rows);
                return flat;
            }
        }

        var top = new Dictionary<string, object?>();
        if (shapeLegend is not null)
            top["shape"] = shapeLegend;
        if (readLegend is not null)
            top["read"] = readLegend;
        if (refsLegend is not null)
            top["refs"] = refsLegend;
        if (modifiersLegend is not null)
            top["modifiers"] = modifiersLegend;
        top["groupedBy"] = primaryIsNamespace ? "namespace" : "file";
        top[primaryIsNamespace ? "namespaces" : "files"] = primaryGroups.Select(g =>
        {
            var node = new Dictionary<string, object?> { [primaryIsNamespace ? "name" : "path"] = g.Key };
            node[primaryIsNamespace ? "files" : "namespaces"] = GroupInOrder(
                    g.Rows, primaryIsNamespace ? r => r.File : r => r.Namespace)
                .Select(sg =>
                {
                    var leaf = new Dictionary<string, object?> { [primaryIsNamespace ? "path" : "name"] = sg.Key };
                    AddLeaf(leaf, sg.Rows);
                    return leaf;
                })
                .ToList();
            return node;
        }).ToList();
        return top;
    }

    private static void AddLeaf(Dictionary<string, object?> node, IReadOnlyList<Row> rows)
    {
        var kinds = rows.Select(r => r.Kind).Distinct().ToList();
        var uniformKind = kinds.Count == 1;
        if (uniformKind)
            node["kind"] = kinds[0];
        // BCL/NuGet (origin: external) hits, and any leaf whose sites are all unresolved, carry no line
        // info by definition -- omit both columns from every row instead of repeating two constant nulls.
        var anyLine = rows.Any(r => r.Lines is not null);
        node["symbols"] = rows.Select(r => RowDict(r, includeKind: !uniformKind, includeLines: anyLine)).ToList();
    }

    private static Dictionary<string, object?> RowDict(Row r, bool includeKind, bool includeLines)
    {
        var d = new Dictionary<string, object?> { ["symbolId"] = r.SymbolId };
        if (includeKind)
            d["kind"] = r.Kind;
        d["name"] = r.LeafName;
        // Emitted only when it applies, and only ever alongside an unresolved file/line: it is the row's
        // own explanation for those blanks, not a fact worth a column on every other row.
        if (r.Placement is DeclarationPlacement.Generated)
            d["generated"] = true;
        else if (r.Placement is DeclarationPlacement.OutsideRoot)
            d["outsideRoot"] = true;
        if (includeLines)
            d["lines"] = r.Lines;
        if (r.Shape is not null)
            d["shape"] = r.Shape;
        if (r.Read is not null)
            d["read"] = r.Read;
        if (r.HasSummary is not null)
            d["hasSummary"] = r.HasSummary;
        if (r.Summary is not null)
            d["summary"] = r.Summary;
        // RefCode already decided which letters this row can carry, including the zeroes worth stating; a null
        // here means the column was not asked for, or nothing was measured for this symbol.
        if (r.Refs is not null)
            d["refs"] = r.Refs;
        if (r.Modifiers is not null)
            d["modifiers"] = r.Modifiers;
        return d;
    }

    private static List<(string Key, List<Row> Rows)> GroupInOrder(IReadOnlyList<Row> rows, Func<Row, string> key)
    {
        var order = new List<string>();
        var byKey = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var k = key(row);
            if (!byKey.TryGetValue(k, out var list))
            {
                list = [];
                byKey[k] = list;
                order.Add(k);
            }
            list.Add(row);
        }
        return order.Select(k => (k, byKey[k])).ToList();
    }
}
