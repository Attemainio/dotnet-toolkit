using DotnetToolkit.McpServer.Output;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

public class SymbolShapeTests
{
    [Fact]
    public void SymbolWithNothingToReportGetsNoShapeAtAll()
    {
        Assert.Null(SymbolShape.For(default));
        Assert.Null(SymbolShape.For(new ShapeFacts(MemberCount: 0, DocLines: 0, CommentLines: 0)));
    }

    [Fact]
    public void EveryFactIsReportedAtItsRealValueInAFixedOrder()
    {
        var facts = new ShapeFacts(
            ParameterCount: 5, MemberCount: 64, NestedCount: 2, LineCount: 1822, LandmarkCount: 11,
            DocLines: 6, CommentLines: 214, AttributeCount: 3);

        Assert.Equal("P5 M64 N2 L1822 O11 D6 C214 A3", SymbolShape.For(facts));
    }

    // The point of removing the thresholds: a one-line, one-member, one-comment symbol reports exactly
    // that, so a caller can read the column as a description of the symbol instead of first working out
    // which of two policies governed each blank.
    [Fact]
    public void NoFactIsGatedBySize()
    {
        Assert.Equal("L1", SymbolShape.For(new ShapeFacts(LineCount: 1)));
        Assert.Equal("M1", SymbolShape.For(new ShapeFacts(MemberCount: 1)));
        Assert.Equal("C1", SymbolShape.For(new ShapeFacts(CommentLines: 1)));
        Assert.Equal("A1", SymbolShape.For(new ShapeFacts(AttributeCount: 1)));
    }

    [Fact]
    public void EachPartIsReportedWithoutTheOthers()
    {
        Assert.Equal("P3", SymbolShape.For(new ShapeFacts(ParameterCount: 3)));
        Assert.Equal("N4", SymbolShape.For(new ShapeFacts(NestedCount: 4)));
        Assert.Equal("O7", SymbolShape.For(new ShapeFacts(LandmarkCount: 7)));
        Assert.Equal("D9", SymbolShape.For(new ShapeFacts(DocLines: 9)));
    }

    // A null count is a fact the symbol's kind cannot have; a zero is a measured absence. They render
    // identically on purpose - nobody is shown "M0" on a method - so only the code that POPULATES the
    // facts is allowed to tell them apart, and the renderer must elide both.
    [Fact]
    public void AZeroAndAnInapplicableCountAreBothElided()
    {
        Assert.Equal("L20", SymbolShape.For(new ShapeFacts(LineCount: 20, MemberCount: 0, LandmarkCount: 0)));
        Assert.Equal("L20", SymbolShape.For(new ShapeFacts(LineCount: 20, MemberCount: null, LandmarkCount: null)));
    }

    [Fact]
    public void UnresolvedOrInvertedSiteReportsNoLineCount()
    {
        Assert.Null(ShapeFacts.LinesBetween(null, null));
        Assert.Null(ShapeFacts.LinesBetween(500, 10));
        Assert.Equal(1, ShapeFacts.LinesBetween(10, 10));
        Assert.Equal(4, ShapeFacts.LinesBetween(750, 753));

        // A member count still stands on its own when the site did not resolve to a line span.
        Assert.Equal("M64", SymbolShape.For(new ShapeFacts(
            MemberCount: 64, LineCount: ShapeFacts.LinesBetween(null, null))));
    }

    // The legend is the caller's only key to the column, so it has to name every letter the renderer can
    // emit, in the order it emits them, and nothing it cannot - a letter added without a legend entry is
    // an unreadable column, and a legend entry with no letter behind it is a promise nothing keeps.
    [Fact]
    public void LegendNamesExactlyTheLettersTheRendererCanEmit()
    {
        var emitted = SymbolShape.For(new ShapeFacts(
            ParameterCount: 1, MemberCount: 1, NestedCount: 1, LineCount: 1, LandmarkCount: 1,
            DocLines: 1, CommentLines: 1, AttributeCount: 1))!;

        var rendered = emitted.Split(' ').Select(part => part[0]);
        var declared = SymbolShape.Legend.Split(';')[0].Split(' ').Select(part => part[0]);

        Assert.Equal(rendered, declared);
        Assert.Contains("absent=none", SymbolShape.Legend);
    }
}
