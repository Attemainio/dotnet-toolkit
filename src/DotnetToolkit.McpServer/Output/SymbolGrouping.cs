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
    public sealed record Row(
        string SymbolId, string Kind, string LeafName, string File, string Namespace,
        int? Line, int? EndLine, bool? HasSummary, string? Summary);

    /// <summary>
    /// Builds the grouped envelope. <paramref name="primaryIsNamespace"/> selects namespace-first
    /// (default) vs. file-first nesting; the other axis always nests one level inside it.
    /// </summary>
    public static Dictionary<string, object?> Build(IReadOnlyList<Row> rows, bool primaryIsNamespace)
    {
        var primaryGroups = GroupInOrder(rows, primaryIsNamespace ? r => r.Namespace : r => r.File);
        if (primaryGroups.Count == 1)
        {
            var onlySecondary = GroupInOrder(
                primaryGroups[0].Rows, primaryIsNamespace ? r => r.File : r => r.Namespace);
            if (onlySecondary.Count == 1)
            {
                var flat = new Dictionary<string, object?>
                {
                    [primaryIsNamespace ? "namespace" : "file"] = primaryGroups[0].Key,
                    [primaryIsNamespace ? "file" : "namespace"] = onlySecondary[0].Key,
                };
                AddLeaf(flat, onlySecondary[0].Rows);
                return flat;
            }
        }

        var top = new Dictionary<string, object?> { ["groupedBy"] = primaryIsNamespace ? "namespace" : "file" };
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
        node["symbols"] = rows.Select(r => RowDict(r, includeKind: !uniformKind)).ToList();
    }

    private static Dictionary<string, object?> RowDict(Row r, bool includeKind)
    {
        var d = new Dictionary<string, object?> { ["symbolId"] = r.SymbolId };
        if (includeKind)
            d["kind"] = r.Kind;
        d["name"] = r.LeafName;
        d["line"] = r.Line;
        d["endLine"] = r.EndLine;
        if (r.HasSummary is not null)
            d["hasSummary"] = r.HasSummary;
        if (r.Summary is not null)
            d["summary"] = r.Summary;
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
