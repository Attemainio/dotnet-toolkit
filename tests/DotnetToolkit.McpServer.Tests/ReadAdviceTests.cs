using DotnetToolkit.McpServer.Output;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// The read column is advice rather than a fact, which makes it the one column that can be wrong
/// rather than merely expensive — so each route is pinned here, and the legend is asserted to name
/// every value the router can actually emit.
/// </summary>
public sealed class ReadAdviceTests
{
    private static readonly ShapeFacts SmallMethod = new(ParameterCount: 2, LineCount: 12, LandmarkCount: 1);
    private static readonly ShapeFacts LargeType = new(MemberCount: 40, LineCount: 800);
    private static readonly ShapeFacts BranchingBody = new(ParameterCount: 3, LineCount: 800, LandmarkCount: 12);
    private static readonly ShapeFacts LinearBody = new(ParameterCount: 3, LineCount: 800, LandmarkCount: 0);

    /// <summary>Silence on the ordinary case is what keeps the column affordable on every other one.</summary>
    [Fact]
    public void SaysNothingWhenTheDefaultFetchIsAlreadyRight() =>
        Assert.Null(ReadAdvice.For(null, SmallMethod));

    [Fact]
    public void SendsALargeTypeToItsMemberList() =>
        Assert.Equal("mem", ReadAdvice.For(null, LargeType));

    [Fact]
    public void MapsALongBranchingBodyBeforeSlicingIt() =>
        Assert.Equal("out", ReadAdvice.For(null, BranchingBody));

    /// <summary>Nothing to map in a linear body, so the outline would be paid for and read once.</summary>
    [Fact]
    public void ReadsALongLinearBodyAsCode() =>
        Assert.Equal("code", ReadAdvice.For(null, LinearBody));

    /// <summary>
    /// An edit needs the body-carrying lease whatever the symbol looks like — the one answer that is
    /// not derived from the facts, which is exactly why stating the intent beats reading the shape.
    /// </summary>
    [Fact]
    public void AnEditTargetWantsEverythingRegardlessOfSize()
    {
        Assert.Equal("all", ReadAdvice.For("edit", SmallMethod));
        Assert.Equal("all", ReadAdvice.For("edit", LargeType));
    }

    /// <summary>The API surface is a member-list question on a type and a signature question elsewhere.</summary>
    [Fact]
    public void SurfaceAsksForMembersOnATypeAndNothingOnAMember()
    {
        Assert.Equal("mem", ReadAdvice.For("surface", LargeType));
        Assert.Null(ReadAdvice.For("surface", BranchingBody));
    }

    /// <summary>
    /// A caller after behaviour is served by source:code at any size: the default fetch returns docs
    /// and reference counts and no code at all, so "small enough to fetch whole" is not the question.
    /// </summary>
    [Fact]
    public void LogicRecommendsCodeEvenOnASmallSymbol() =>
        Assert.Equal("code", ReadAdvice.For("logic", SmallMethod));

    [Fact]
    public void LogicStillMapsALongBranchingBodyFirst() =>
        Assert.Equal("out", ReadAdvice.For("logic", BranchingBody));

    /// <summary>
    /// An unrecognized intent falls through to the shape-derived answer rather than being rejected —
    /// search_index normalizes before calling, so this is the in-process caller's safety net.
    /// </summary>
    [Fact]
    public void AnUnrecognizedIntentIsTreatedAsNoneAtAll() =>
        Assert.Equal(ReadAdvice.For(null, LargeType), ReadAdvice.For("whatever", LargeType));

    /// <summary>
    /// The legend is the caller's only key to the column, so a value the router emits but the legend
    /// never names would be an unreadable recommendation — worse than no column at all.
    /// </summary>
    [Fact]
    public void LegendNamesEveryValueTheRouterCanEmit()
    {
        string?[] emitted =
        [
            ReadAdvice.For("edit", SmallMethod),
            ReadAdvice.For(null, LargeType),
            ReadAdvice.For(null, BranchingBody),
            ReadAdvice.For(null, LinearBody),
        ];

        Assert.Equal(4, emitted.Distinct().Count());
        Assert.All(emitted, value =>
        {
            Assert.NotNull(value);
            Assert.Contains($"{value}=", ReadAdvice.Legend);
        });
    }
}
