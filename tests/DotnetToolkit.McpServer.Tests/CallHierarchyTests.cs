using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// The walk behind get_call_hierarchy, driven straight off the edge cache rather than through a loaded
/// workspace. These pin the two properties a rendered tree has to hold at once: a symbol reached through
/// two branches states its subtree once, and the blast-radius counters do not move when it does.
/// </summary>
public sealed class CallHierarchyTests : IDisposable
{
    private readonly string _root;
    private readonly KnowledgeStore _store;
    private readonly SymbolStore _symbols;

    public CallHierarchyTests()
    {
        _root = Directory.CreateTempSubdirectory("call-hierarchy-tests-").FullName;
        var locator = new SolutionLocator(NullLogger<SolutionLocator>.Instance, _root);
        _store = new KnowledgeStore(locator, NullLogger<KnowledgeStore>.Instance);
        _symbols = new SymbolStore(_store);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private static SymbolStore.SymbolRow Row(string id) =>
        new(id, "Ns.Type." + id, "Method", "Proj", "d1", null, "Type." + id);

    private static SymbolStore.EdgeRow Calls(string from, string to) =>
        new(from, to, "call", null, null);

    /// <summary>
    /// Both branches out of the root converge on one caller, whose own callers used to be rendered in full
    /// under each of them — the single largest avoidable cost in a well-connected tree.
    /// </summary>
    [Fact]
    public void Build_DiamondSubtree_IsExpandedUnderOnlyOneBranch()
    {
        Seed();

        var result = new CallHierarchy(_symbols).Build("shared_root", walkCallers: true, maxDepth: 3, maxChildrenPerNode: 25);

        var branches = result.Root.Children!;
        Assert.Equal(2, branches.Count);

        // One "shared" node per branch: the same symbol, reached two ways.
        var shared = branches.Select(b => Assert.Single(b.Children!)).ToList();
        Assert.All(shared, node => Assert.Equal("shared", node.SymbolId));

        var expanded = Assert.Single(shared, n => n.Children is not null);
        var pointer = Assert.Single(shared, n => n.Children is null);

        Assert.Equal(2, expanded.Children!.Count);
        Assert.False(expanded.Repeated);
        Assert.True(pointer.Repeated);
    }

    /// <summary>
    /// Collapsing the second copy is a rendering decision, so it must not move a single blast-radius
    /// number: those count what the walk reached, not what survived into the tree.
    /// </summary>
    [Fact]
    public void Build_DiamondSubtree_LeavesBlastRadiusUntouched()
    {
        Seed();

        var result = new CallHierarchy(_symbols).Build("shared_root", walkCallers: true, maxDepth: 3, maxChildrenPerNode: 25);

        Assert.Equal(6, result.TotalUniqueNodes);
        Assert.Equal([1, 2, 1, 2], result.PerDepth);
        Assert.False(result.DepthCapped);
        Assert.Equal(0, result.OmittedChildren);
    }

    /// <summary>
    /// A diamond: two callers of the root that share one caller of their own, which in turn has two.
    /// </summary>
    private void Seed()
    {
        _symbols.ReplaceAll(
            [Row("shared_root"), Row("left"), Row("right"), Row("shared"), Row("leaf_a"), Row("leaf_b")],
            [
                Calls("left", "shared_root"),
                Calls("right", "shared_root"),
                Calls("shared", "left"),
                Calls("shared", "right"),
                Calls("leaf_a", "shared"),
                Calls("leaf_b", "shared"),
            ]);
    }
}
