using DotnetToolkit.McpServer.Output;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

public class SymbolShapeTests
{
    [Fact]
    public void SmallUndocumentedSymbolGetsNoShapeAtAll()
    {
        Assert.Null(Gated(line: 750, endLine: 753, memberCount: null));
        Assert.Null(Gated(line: 10, endLine: 10, memberCount: 3));
    }

    [Fact]
    public void ThresholdsAreInclusiveOnTheLowSide()
    {
        Assert.Null(Gated(1, SymbolShape.LineThreshold - 1, null));
        Assert.Equal($"L{SymbolShape.LineThreshold}", Gated(1, SymbolShape.LineThreshold, null));

        Assert.Null(Gated(1, 1, SymbolShape.MemberThreshold - 1));
        Assert.Equal($"M{SymbolShape.MemberThreshold}", Gated(1, 1, SymbolShape.MemberThreshold));
    }

    [Fact]
    public void LongDocumentedTypeReportsEveryFactInOrder()
    {
        Assert.Equal("L1822 M64 D6 C214", SymbolShape.For(25, 1846, 64, docLines: 6, commentLines: 214));
    }

    [Fact]
    public void EachPartIsReportedWithoutTheOthers()
    {
        Assert.Equal("L179", Gated(line: 556, endLine: 734, memberCount: 4));
        Assert.Equal("M40", Gated(line: 1, endLine: 20, memberCount: 40));
        Assert.Equal("D9", SymbolShape.For(1, 20, 4, docLines: 9, commentLines: 0));
        Assert.Equal("C21", SymbolShape.For(1, 20, 4, docLines: 0, commentLines: 21));
    }

    // The point of making D and C unconditional: they are recoverable from nothing else on the row, so a
    // symbol under BOTH gates still reports them rather than leaving "none" and "not measured" identical.
    [Fact]
    public void DocsAndCommentsAreReportedBelowEveryThreshold()
    {
        Assert.Equal("D3", SymbolShape.For(1, 4, 1, docLines: 3, commentLines: 0));
        Assert.Equal("D3 C1", SymbolShape.For(1, 4, 1, docLines: 3, commentLines: 1));
    }

    [Fact]
    public void UnresolvedOrInvertedSiteReportsNoLineCount()
    {
        Assert.Null(Gated(line: null, endLine: null, memberCount: null));
        Assert.Null(Gated(line: 500, endLine: 10, memberCount: null));

        // A member count still stands on its own when the site did not resolve to a line span.
        Assert.Equal("M64", Gated(line: null, endLine: null, memberCount: 64));
    }

    // The legend spells both thresholds out for the caller, and const interpolation cannot derive them
    // from the constants, so nothing but this keeps the two in step after a threshold is ever retuned.
    [Fact]
    public void LegendQuotesTheThresholdsItGatesOn()
    {
        Assert.Contains($"L=lines({SymbolShape.LineThreshold}+)", SymbolShape.Legend);
        Assert.Contains($"M=members({SymbolShape.MemberThreshold}+)", SymbolShape.Legend);
    }

    /// <summary>A hit with neither docs nor comments, where only the gated facts can fire.</summary>
    private static string? Gated(int? line, int? endLine, int? memberCount) =>
        SymbolShape.For(line, endLine, memberCount, docLines: 0, commentLines: 0);
}
