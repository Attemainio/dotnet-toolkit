using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Output;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

public class CompactFormatterTests
{
    [Fact]
    public void TableWithTruncationMarker()
    {
        var output = CompactFormatter.Table(
            "refs",
            ["file", "line"],
            [["a.cs", "1"], ["b.cs", "2"]],
            total: 7);

        Assert.Equal("""
            refs(2/7) file|line
            a.cs|1
            b.cs|2
            …+5 more (raise limit)
            """, output);
    }

    [Fact]
    public void TableWithoutTruncationOmitsMarker()
    {
        var output = CompactFormatter.Table("symbols", ["kind"], [["C"]], total: 1);
        Assert.Equal("symbols(1/1) kind\nC", output);
    }

    [Fact]
    public void FirstSentenceCutsAtSentenceBoundaryAndLength()
    {
        Assert.Equal("First.", CompactFormatter.FirstSentence("First. Second. Third."));
        Assert.Equal("First.", CompactFormatter.FirstSentence("First. Second."));
        Assert.Null(CompactFormatter.FirstSentence("  "));
        Assert.Equal(120, CompactFormatter.FirstSentence(new string('x', 500))!.Length);
    }

    [Fact]
    public void TruncateCutsAtLengthWithEllipsis()
    {
        Assert.Equal("hello", CompactFormatter.Truncate("hello", 10));

        var result = CompactFormatter.Truncate(new string('x', 500), 160);
        Assert.Equal(160, result.Length);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void OutlineRendererMatchesCompactSpec()
    {
        var entry = OutlineBuilder.Build("""
            namespace Contoso.Services;

            /// <summary>Coordinates order lifecycle.</summary>
            public class OrderService : IOrderService
            {
                /// <summary>Validates and persists an order.</summary>
                public Task<OrderResult> PlaceOrder(OrderRequest req) => throw null!;

                private void Internal() { }
            }
            """, 0, 0);

        var rendered = OutlineRenderer.RenderFile("src/Services/OrderService.cs", entry, includePrivate: false);
        Assert.Equal("""
            src/Services/OrderService.cs ns Contoso.Services
            C OrderService : IOrderService  // Coordinates order lifecycle.
              M PlaceOrder(OrderRequest req) -> Task<OrderResult>  // Validates and persists an order.
            """, rendered);

        var withPrivate = OutlineRenderer.RenderFile("f.cs", entry, includePrivate: true);
        Assert.Contains("Internal() -> void", withPrivate);
    }
}
