using DotnetToolkit.McpServer.Indexing;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

public sealed class ProjectCycleDetectorTests
{
    [Fact]
    public void NoEdges_FindsNoCycles()
    {
        var graph = new Dictionary<string, List<string>>
        {
            ["A"] = [],
            ["B"] = [],
        };
        Assert.Empty(ProjectCycleDetector.FindCycles(graph));
    }

    [Fact]
    public void AcyclicChain_FindsNoCycles()
    {
        var graph = new Dictionary<string, List<string>>
        {
            ["A"] = ["B"],
            ["B"] = ["C"],
            ["C"] = [],
        };
        Assert.Empty(ProjectCycleDetector.FindCycles(graph));
    }

    [Fact]
    public void SelfEdge_IsReportedAsACycle()
    {
        var graph = new Dictionary<string, List<string>>
        {
            ["A"] = ["A"],
        };
        var cycles = ProjectCycleDetector.FindCycles(graph);
        var cycle = Assert.Single(cycles);
        Assert.Equal(["A", "A"], cycle);
    }

    [Fact]
    public void TwoNodeCycle_IsFound()
    {
        var graph = new Dictionary<string, List<string>>
        {
            ["A"] = ["B"],
            ["B"] = ["A"],
        };
        var cycles = ProjectCycleDetector.FindCycles(graph);
        var cycle = Assert.Single(cycles);
        Assert.Equal(3, cycle.Count);
        Assert.Equal(cycle[0], cycle[^1]);
        Assert.Equal(["A", "B"], [.. cycle.Take(2).OrderBy(x => x)]);
    }

    [Fact]
    public void LargerStronglyConnectedComponent_ReturnsOneRepresentativeCycle()
    {
        var graph = new Dictionary<string, List<string>>
        {
            ["A"] = ["B"],
            ["B"] = ["C"],
            ["C"] = ["A"],
            ["D"] = ["A"], // feeds into the cycle but is not itself part of it
        };
        var cycles = ProjectCycleDetector.FindCycles(graph);
        var cycle = Assert.Single(cycles);
        Assert.Equal(4, cycle.Count);
        Assert.Equal(cycle[0], cycle[^1]);
        Assert.DoesNotContain("D", cycle);
    }

    [Fact]
    public void DisjointAcyclicAndCyclicComponents_OnlyReportsTheCycle()
    {
        var graph = new Dictionary<string, List<string>>
        {
            ["A"] = ["B"],
            ["B"] = [],
            ["X"] = ["Y"],
            ["Y"] = ["X"],
        };
        var cycles = ProjectCycleDetector.FindCycles(graph);
        var cycle = Assert.Single(cycles);
        Assert.Equal(["X", "Y"], [.. cycle.Take(2).OrderBy(x => x)]);
    }
}
