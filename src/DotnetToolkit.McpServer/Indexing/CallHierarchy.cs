using DotnetToolkit.McpServer.Store;

namespace DotnetToolkit.McpServer.Indexing;

/// <summary>
/// Open-ended multi-level call tree from one root symbol (spec: fills the gap <see cref="CallSlice"/>
/// cannot answer — "who eventually calls this, up to the entry points", Visual Studio's View Call
/// Hierarchy). Walks the same cached edge table <see cref="CallSlice"/> uses via
/// <see cref="SymbolStore.CallTargets"/>/<see cref="SymbolStore.Callers"/>, one direction only, to a
/// bounded depth.
///
/// Unlike <see cref="CallSlice"/>'s single shortest path, this returns every branch, so a symbol reached
/// through two different call paths (a diamond) legitimately appears twice in the tree — deduping that
/// would hide a real second route in. The cycle guard below is per-branch (the root-to-node path), not
/// global, precisely so a diamond is not mistaken for a cycle; true recursion (a symbol reappearing on
/// its own path) is what stops expansion, marked on that node rather than looped forever.
/// </summary>
public sealed class CallHierarchy
{
    // Safety net against pathological fan-out (e.g. a hub method with hundreds of callers at every
    // level) blowing the response up combinatorially even under a modest per-node cap. Not a caller-
    // tunable parameter — it only ever prevents runaway output, never shapes a normal answer.
    private const int HardNodeCap = 3000;

    private readonly SymbolStore _symbols;

    public CallHierarchy(SymbolStore symbols) => _symbols = symbols;

    public sealed record Node(
        string SymbolId,
        IReadOnlyList<Node>? Children,
        bool Recursive,
        bool Truncated,
        int? OmittedChildren);

    public sealed record Result(
        Node Root,
        int TotalUniqueNodes,
        IReadOnlyList<int> PerDepth,
        bool DepthCapped);

    public Result Build(string rootId, bool walkCallers, int maxDepth, int maxChildrenPerNode)
    {
        var depthSets = new List<HashSet<string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var depthCapped = false;

        void Track(string id, int depth)
        {
            while (depthSets.Count <= depth)
                depthSets.Add(new HashSet<string>(StringComparer.Ordinal));
            depthSets[depth].Add(id);
            seen.Add(id);
        }

        Node Walk(string id, HashSet<string> pathAncestors, int depth)
        {
            Track(id, depth);

            var neighbors = walkCallers ? _symbols.Callers(id) : _symbols.CallTargets(id);

            if (depth >= maxDepth)
            {
                if (neighbors.Count > 0)
                    depthCapped = true;
                return new Node(id, null, false, false, null);
            }
            if (neighbors.Count == 0)
                return new Node(id, [], false, false, null);

            if (seen.Count >= HardNodeCap)
                return new Node(id, [], false, true, neighbors.Count);

            var kept = neighbors.Take(Math.Max(1, maxChildrenPerNode)).ToList();
            var omitted = neighbors.Count - kept.Count;

            var children = new List<Node>(kept.Count);
            foreach (var neighbor in kept)
            {
                if (pathAncestors.Contains(neighbor))
                {
                    children.Add(new Node(neighbor, null, true, false, null));
                    continue;
                }
                var nextPath = new HashSet<string>(pathAncestors, StringComparer.Ordinal) { neighbor };
                children.Add(Walk(neighbor, nextPath, depth + 1));
            }

            return new Node(id, children, false, omitted > 0, omitted > 0 ? omitted : null);
        }

        var root = Walk(rootId, new HashSet<string>(StringComparer.Ordinal) { rootId }, 0);
        return new Result(root, seen.Count, [.. depthSets.Select(s => s.Count)], depthCapped);
    }
}
