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
    private static readonly ShapeFacts SmallField = new(LineCount: 1);

    /// <summary>Silence on the ordinary case is what keeps the column affordable on every other one.</summary>
    [Fact]
    public void SaysNothingWhenTheDefaultFetchIsAlreadyRight() =>
        Assert.Null(ReadAdvice.For(null, "Method", SmallMethod));

    [Fact]
    public void SendsALargeTypeToItsMemberList() =>
        Assert.Equal("mem", ReadAdvice.For(null, "Type", LargeType));

    [Fact]
    public void MapsALongBranchingBodyBeforeSlicingIt() =>
        Assert.Equal("out", ReadAdvice.For(null, "Method", BranchingBody));

    /// <summary>Nothing to map in a linear body, so the outline would be paid for and read once.</summary>
    [Fact]
    public void ReadsALongLinearBodyAsCode() =>
        Assert.Equal("code", ReadAdvice.For(null, "Method", LinearBody));

    /// <summary>
    /// An edit needs a body-carrying lease, but every body-serving include grants the identical layer, so
    /// the advice is the CHEAPEST one rather than the widest — and a type, which has no body layer at all,
    /// is sent to its surface instead. Answering one constant on every row carried no information.
    /// </summary>
    [Fact]
    public void AnEditTargetGetsTheCheapestLeaseThatCarriesTheBody()
    {
        Assert.Equal("out", ReadAdvice.For("edit", "Method", SmallMethod));
        Assert.Equal("mem", ReadAdvice.For("edit", "Type", LargeType));
    }

    /// <summary>
    /// A Field has no body layer at all -- bodyOutline refuses it outright rather than leasing an empty
    /// one, unlike an auto-property's empty accessor -- so unlike SmallMethod above, "edit" must NOT route
    /// it to "out": that would send the caller to a fetch that leaves the next validate_patch decl-only.
    /// </summary>
    [Fact]
    public void EditIntentExcludesFieldsWhichHaveNoBodyLayerToLease() =>
        Assert.Null(ReadAdvice.For("edit", "Field", SmallField));

    /// <summary>The API surface is a member-list question on a type and a signature question elsewhere.</summary>
    [Fact]
    public void SurfaceAsksForMembersOnATypeAndNothingOnAMember()
    {
        Assert.Equal("mem", ReadAdvice.For("surface", "Type", LargeType));
        Assert.Null(ReadAdvice.For("surface", "Method", BranchingBody));
    }

    /// <summary>
    /// source:code is source:full minus the leading doc comment, so on a symbol with no doc lines the two
    /// are byte-identical and the label names a saving that does not exist — while every silent row pays a
    /// cell for the column it keeps alive. With docs to drop it fires as before.
    /// </summary>
    [Fact]
    public void LogicRecommendsCodeOnlyWhenThereAreDocsToDrop()
    {
        Assert.Null(ReadAdvice.For("logic", "Method", SmallMethod));
        Assert.Equal("code", ReadAdvice.For("logic", "Method", SmallMethod with { DocLines = 4 }));
    }

    [Fact]
    public void LogicStillMapsALongBranchingBodyFirst() =>
        Assert.Equal("out", ReadAdvice.For("logic", "Method", BranchingBody));

    /// <summary>
    /// An unrecognized intent falls through to the shape-derived answer rather than being rejected —
    /// search_index normalizes before calling, so this is the in-process caller's safety net.
    /// </summary>
    [Fact]
    public void AnUnrecognizedIntentIsTreatedAsNoneAtAll() =>
        Assert.Equal(ReadAdvice.For(null, "Type", LargeType), ReadAdvice.For("whatever", "Type", LargeType));

    /// <summary>
    /// The legend is the caller's only key to the column, so a value the router emits but the legend never
    /// names would be an unreadable recommendation — worse than no column at all. The converse is asserted
    /// too: <c>all=include:all</c> outlived the router's last use of it, and a legend entry for a value that
    /// cannot occur is exactly the restatement this column is meant not to ship.
    /// </summary>
    [Fact]
    public void LegendNamesEveryValueTheRouterCanEmitAndNoOthers()
    {
        string?[] emitted =
        [
            ReadAdvice.For("edit", "Type", LargeType),
            ReadAdvice.For("edit", "Method", SmallMethod),
            ReadAdvice.For(null, "Method", LinearBody),
        ];

        Assert.Equal(3, emitted.Distinct().Count());
        Assert.All(emitted, value =>
        {
            Assert.NotNull(value);
            Assert.Contains($"{value}=", ReadAdvice.Legend);
        });

        var defined = ReadAdvice.Legend.Split(' ')
            .Where(token => token.Contains('='))
            .Select(token => token[..token.IndexOf('=')])
            .ToList();
        Assert.Equal(["mem", "out", "code", "absent"], defined);
    }
}
