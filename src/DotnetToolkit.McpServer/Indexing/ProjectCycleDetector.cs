namespace DotnetToolkit.McpServer.Indexing;

/// <summary>
/// Tarjan's SCC over a small project-reference graph (spec: <c>detect_circular_dependencies</c>). Finds
/// every strongly-connected component in one linear pass rather than plain DFS coloring, at no real
/// extra cost given a solution's project count is always small (tens, not thousands). For each
/// non-trivial component (size &gt; 1, or a self-edge) one representative cycle is reconstructed — not
/// every distinct cycle within it, which can be combinatorial — matching <see cref="CallSlice"/>'s
/// "shortest path, not all paths" scoping.
/// </summary>
public static class ProjectCycleDetector
{
    public static IReadOnlyList<IReadOnlyList<string>> FindCycles(IReadOnlyDictionary<string, List<string>> graph)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowlink = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var next = 0;
        var components = new List<List<string>>();

        // Iterative Tarjan (explicit work stack) instead of native recursion on StrongConnect, so a
        // pathologically long project-reference chain degrades gracefully instead of overflowing the
        // native stack, which would kill the whole server process rather than just this call.
        void StrongConnect(string start)
        {
            var work = new Stack<(string V, IEnumerator<string> Neighbors)>();

            void Enter(string v)
            {
                index[v] = next;
                lowlink[v] = next;
                next++;
                stack.Push(v);
                onStack.Add(v);
                work.Push((v, (graph.GetValueOrDefault(v) ?? []).GetEnumerator()));
            }

            Enter(start);

            while (work.Count > 0)
            {
                var (v, neighbors) = work.Peek();
                if (neighbors.MoveNext())
                {
                    var w = neighbors.Current;
                    if (!index.ContainsKey(w))
                    {
                        Enter(w);
                    }
                    else if (onStack.Contains(w))
                    {
                        lowlink[v] = Math.Min(lowlink[v], index[w]);
                    }
                    continue;
                }

                work.Pop();
                if (work.Count > 0)
                {
                    var (parent, _) = work.Peek();
                    lowlink[parent] = Math.Min(lowlink[parent], lowlink[v]);
                }

                if (lowlink[v] == index[v])
                {
                    var component = new List<string>();
                    string w;
                    do
                    {
                        w = stack.Pop();
                        onStack.Remove(w);
                        component.Add(w);
                    } while (w != v);
                    components.Add(component);
                }
            }
        }


        foreach (var v in graph.Keys)
            if (!index.ContainsKey(v))
                StrongConnect(v);

        var cycles = new List<IReadOnlyList<string>>();
        foreach (var component in components)
        {
            if (component.Count > 1)
            {
                cycles.Add(RepresentativeCycle(graph, component));
            }
            else
            {
                var v = component[0];
                if ((graph.GetValueOrDefault(v) ?? []).Contains(v))
                    cycles.Add([v, v]);
            }
        }
        return cycles;
    }

    /// <summary>
    /// One representative cycle within a strongly-connected component, via a DFS restricted to the
    /// component's own members — every member is reachable from every other by definition of "strongly
    /// connected", so this always finds a path back to <paramref name="component"/>'s first node.
    /// </summary>
    private static IReadOnlyList<string> RepresentativeCycle(IReadOnlyDictionary<string, List<string>> graph, List<string> component)
    {
        var members = component.ToHashSet(StringComparer.Ordinal);
        var start = component[0];
        var path = new List<string> { start };
        var onPath = new HashSet<string>(StringComparer.Ordinal) { start };

        // Iterative DFS (explicit work stack) rather than native recursion, so a pathologically large
        // strongly-connected component can't overflow the native stack.
        var work = new Stack<(string V, IEnumerator<string> Neighbors)>();
        work.Push((start, (graph.GetValueOrDefault(start) ?? []).Where(members.Contains).GetEnumerator()));

        while (work.Count > 0)
        {
            var (v, neighbors) = work.Peek();
            if (!neighbors.MoveNext())
            {
                work.Pop();
                if (work.Count > 0)
                {
                    path.RemoveAt(path.Count - 1);
                    onPath.Remove(v);
                }
                continue;
            }

            var w = neighbors.Current;
            if (w == start && path.Count > 1)
            {
                path.Add(start);
                break;
            }
            if (onPath.Contains(w))
                continue;

            path.Add(w);
            onPath.Add(w);
            work.Push((w, (graph.GetValueOrDefault(w) ?? []).Where(members.Contains).GetEnumerator()));
        }

        return path;
    }
}
