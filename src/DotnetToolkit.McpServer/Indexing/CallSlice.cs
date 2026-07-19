using DotnetToolkit.McpServer.Store;

namespace DotnetToolkit.McpServer.Indexing;

/// <summary>
/// Bounded path search between two symbols over the cached edge table (spec §12). Answers
/// "how does X reach Y" without the agent walking the graph by hand with repeated reference queries.
///
/// The search is bidirectional and meets in the middle: expanding from both ends explores far fewer
/// nodes than a one-directional walk at the same depth, which is what keeps a deep slice affordable.
/// A miss is still informative — the nearest frontier reached from each side is returned rather than
/// a bare "not found".
/// </summary>
public sealed class CallSlice
{
    private readonly SymbolStore _symbols;

    public CallSlice(SymbolStore symbols) => _symbols = symbols;

    public sealed record Result(
        bool Found,
        IReadOnlyList<string> Path,
        int NodesExplored,
        int AlternatePathCount,
        IReadOnlyList<string> ForwardFrontier,
        IReadOnlyList<string> BackwardFrontier);

    public Result Find(string fromId, string toId, int maxDepth = 8)
    {
        if (string.Equals(fromId, toId, StringComparison.Ordinal))
            return new Result(true, [fromId], 1, 0, [], []);

        // Parent maps double as visited sets; each records how a node was reached from its own side.
        var forwardParent = new Dictionary<string, string?>(StringComparer.Ordinal) { [fromId] = null };
        var backwardParent = new Dictionary<string, string?>(StringComparer.Ordinal) { [toId] = null };
        var forwardFrontier = new List<string> { fromId };
        var backwardFrontier = new List<string> { toId };

        for (var depth = 0; depth < maxDepth; depth++)
        {
            // Always expand the smaller frontier — that is what makes the bidirectional search pay off.
            var expandForward = forwardFrontier.Count <= backwardFrontier.Count;
            var frontier = expandForward ? forwardFrontier : backwardFrontier;
            var parents = expandForward ? forwardParent : backwardParent;
            var opposite = expandForward ? backwardParent : forwardParent;

            if (frontier.Count == 0)
                break;

            var next = new List<string>();
            foreach (var node in frontier)
            {
                var neighbors = expandForward ? _symbols.CallTargets(node) : _symbols.Callers(node);
                foreach (var neighbor in neighbors)
                {
                    if (parents.ContainsKey(neighbor))
                        continue;
                    parents[neighbor] = node;
                    next.Add(neighbor);

                    if (opposite.ContainsKey(neighbor))
                    {
                        var path = Join(neighbor, forwardParent, backwardParent);
                        var explored = forwardParent.Count + backwardParent.Count;
                        return new Result(true, path, explored, 0, [], []);
                    }
                }
            }

            if (expandForward)
                forwardFrontier = next;
            else
                backwardFrontier = next;
        }

        return new Result(false, [], forwardParent.Count + backwardParent.Count, 0,
            [.. forwardFrontier.Take(5)], [.. backwardFrontier.Take(5)]);
    }

    /// <summary>Stitches the two half-paths at the meeting node into one from-&gt;to ordering.</summary>
    private static List<string> Join(string meeting, Dictionary<string, string?> forward, Dictionary<string, string?> backward)
    {
        var head = new List<string>();
        for (string? node = meeting; node is not null; node = forward.GetValueOrDefault(node))
        {
            head.Add(node);
            if (forward.GetValueOrDefault(node) is null)
                break;
        }
        head.Reverse();

        var tail = new List<string>();
        for (var node = backward.GetValueOrDefault(meeting); node is not null; node = backward.GetValueOrDefault(node))
        {
            tail.Add(node);
            if (backward.GetValueOrDefault(node) is null)
                break;
        }

        head.AddRange(tail);
        return head;
    }
}
