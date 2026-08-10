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

    /// <summary>One node of the walked tree: a symbol, and whatever branch was expanded below it.</summary>
    /// <param name="SymbolId">The symbol this node stands for.</param>
    /// <param name="Children">The expanded children — empty when the symbol has no neighbours, null when this node was not expanded at all.</param>
    /// <param name="Recursive">True when the walk stopped here because this symbol is already on the path from the root.</param>
    /// <param name="Truncated">True when the per-node cap left some of this node's neighbours unexpanded.</param>
    /// <param name="OmittedChildren">How many neighbours that cap left out, or null when it left none.</param>
    /// <param name="Repeated">True when this symbol's subtree was already rendered elsewhere in the tree, so this node points back by <see cref="SymbolId"/> rather than expanding the same branch a second time.</param>
    public sealed record Node(
        string SymbolId,
        IReadOnlyList<Node>? Children,
        bool Recursive,
        bool Truncated,
        int? OmittedChildren,
        bool Repeated = false);

    /// <summary>
    /// The walk's outcome: the tree itself, plus the blast-radius counters describing the whole graph
    /// reached from the root — not just the part the tree rendered.
    /// </summary>
    /// <param name="Root">The root node, with its children as far as the caps allowed expansion.</param>
    /// <param name="TotalUniqueNodes">Distinct symbols reached, counting neighbours a render cap left out.</param>
    /// <param name="PerDepth">Distinct symbols reached at each depth, index 0 being the root itself.</param>
    /// <param name="DepthCapped">True when maxDepth stopped a node that still had neighbours.</param>
    /// <param name="OmittedChildren">Total children left unexpanded by the per-node cap, summed over the tree.</param>
    public sealed record Result(
        Node Root,
        int TotalUniqueNodes,
        IReadOnlyList<int> PerDepth,
        bool DepthCapped,
        int OmittedChildren);

    /// <summary>Walks the tree from one root, to a bounded depth and per-node width.</summary>
    /// <param name="rootId">The symbol to walk from.</param>
    /// <param name="walkCallers">True to walk upward toward entry points, false to walk into callees.</param>
    /// <param name="maxDepth">Maximum tree depth.</param>
    /// <param name="maxChildrenPerNode">Maximum children expanded per node.</param>
    /// <param name="rootNeighbors">
    /// Depth-1 neighbours to use INSTEAD of the root's own edges, for a root the edge table cannot answer
    /// for. A named type has no call edges at all, so the caller supplies the members that reference it and
    /// every depth below is walked from the edge table as usual. Null uses the root's own edges.
    /// </param>
    /// <returns>The tree and the blast-radius counters for everything reached from the root.</returns>
    public Result Build(
        string rootId,
        bool walkCallers,
        int maxDepth,
        int maxChildrenPerNode,
        IReadOnlyList<string>? rootNeighbors = null)
    {
        var depthSets = new List<HashSet<string>>();
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var rendered = new HashSet<string>(StringComparer.Ordinal);
        var depthCapped = false;
        var omittedTotal = 0;

        void Track(string id, int depth)
        {
            while (depthSets.Count <= depth)
                depthSets.Add(new HashSet<string>(StringComparer.Ordinal));
            depthSets[depth].Add(id);
            reached.Add(id);
        }

        // A neighbour the per-node cap left out is still part of the blast radius: it was reached, it just
        // was not rendered. Counting only what the tree shows made includeTree:false -- the shape whose
        // whole purpose is answering "how much does changing this ripple" -- report 26 for a symbol with
        // 103 callers, with no truncation marker in that shape to betray it.
        //
        // Such a neighbour is also never EXPANDED, so every depth past it is unexplored in exactly the way
        // the depths past maxDepth are. depthCapped used to be set only for the latter, so a walk that hid
        // whole subtrees behind maxChildrenPerNode still answered depthCapped:false -- read as "complete
        // to maxDepth", which is the one thing it was not. Both causes stop the walk short, so both set it.
        void TrackOmitted(IEnumerable<string> neighbors, int depth)
        {
            var any = false;
            foreach (var neighbor in neighbors)
            {
                Track(neighbor, depth);
                omittedTotal++;
                any = true;
            }

            if (any && depth < maxDepth)
                depthCapped = true;
        }

        // A symbol reached through two branches (a diamond) has ONE subtree, not two, and rendering it
        // twice charged the caller twice for a shape that had not changed between the branches. The first
        // encounter renders it; every later one keeps its own node and points back by symbolId under
        // repeated:true. Expand still runs either way, so blastRadius is untouched -- it counts what the
        // walk REACHED, which is a property of the walk and not of what survived rendering.
        Node Walk(string id, HashSet<string> pathAncestors, int depth)
        {
            var node = Expand(id, pathAncestors, depth);
            return node.Children is { Count: > 0 } && !rendered.Add(id)
                ? node with { Children = null, Repeated = true }
                : node;
        }

        Node Expand(string id, HashSet<string> pathAncestors, int depth)
        {
            Track(id, depth);
            expanded.Add(id);

            var neighbors = depth == 0 && rootNeighbors is not null
                ? rootNeighbors
                : walkCallers ? _symbols.Callers(id) : _symbols.CallTargets(id);

            if (depth >= maxDepth)
            {
                if (neighbors.Count > 0)
                    depthCapped = true;
                return new Node(id, null, false, false, null);
            }
            if (neighbors.Count == 0)
                return new Node(id, [], false, false, null);

            if (expanded.Count >= HardNodeCap)
            {
                TrackOmitted(neighbors, depth + 1);
                return new Node(id, [], false, true, neighbors.Count);
            }

            var kept = neighbors.Take(Math.Max(1, maxChildrenPerNode)).ToList();
            var omitted = neighbors.Count - kept.Count;
            TrackOmitted(neighbors.Skip(kept.Count), depth + 1);

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
        return new Result(root, reached.Count, [.. depthSets.Select(s => s.Count)], depthCapped, omittedTotal);
    }
}
