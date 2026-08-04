using DotnetToolkit.McpServer.Output;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

public class SymbolGroupingTests
{
    private static SymbolGrouping.Row Row(string name, string? shape) =>
        new($"sym_{name}", "Method", name, "Lib/Widget.cs", "Sample.Lib", 1, 2, null, null, shape);

    private static List<Dictionary<string, object?>> SymbolsOf(Dictionary<string, object?> grouped) =>
        Assert.IsType<List<Dictionary<string, object?>>>(grouped["symbols"]);

    [Fact]
    public void ShapeLegendIsStatedOnceAtTheTopWhenAnyRowCarriesAShape()
    {
        var grouped = SymbolGrouping.Build(
            [Row("Small", null), Row("Big", "L1822 M64")], primaryIsNamespace: true);

        Assert.Equal(SymbolShape.Legend, grouped["shape"]);
    }

    [Fact]
    public void NoRowCarryingAShapeLeavesTheLegendOutEntirely()
    {
        var grouped = SymbolGrouping.Build([Row("Small", null)], primaryIsNamespace: true);

        Assert.False(grouped.ContainsKey("shape"));
        Assert.False(SymbolsOf(grouped)[0].ContainsKey("shape"));
    }

    [Fact]
    public void ARowsShapeRidesAlongsideItsLineSpan()
    {
        var grouped = SymbolGrouping.Build([Row("Big", "L1822 M64")], primaryIsNamespace: true);

        var only = Assert.Single(SymbolsOf(grouped));
        Assert.Equal("L1822 M64", only["shape"]);
        Assert.Equal(1, only["line"]);
    }

    /// <summary>
    /// The legend belongs to the whole response, not to a group, so it survives the nested shape the
    /// same way it survives the collapsed one — a caller reading a multi-namespace result must not have
    /// to find the legend inside whichever group happened to hold the large symbol.
    /// </summary>
    [Fact]
    public void ShapeLegendSurvivesTheNestedGroupedShape()
    {
        var other = new SymbolGrouping.Row(
            "sym_Other", "Method", "Other", "Other/File.cs", "Other.Ns", 1, 2, null, null, null);

        var grouped = SymbolGrouping.Build([Row("Big", "L1822 M64"), other], primaryIsNamespace: true);

        Assert.Equal(SymbolShape.Legend, grouped["shape"]);
        Assert.Equal("namespace", grouped["groupedBy"]);
    }
}
