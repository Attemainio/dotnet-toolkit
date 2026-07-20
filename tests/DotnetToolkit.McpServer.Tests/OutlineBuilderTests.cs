using DotnetToolkit.McpServer.Indexing;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

public class OutlineBuilderTests
{
    private static FileEntry Build(string source) => OutlineBuilder.Build(source, 0, source.Length);

    [Fact]
    public void ClassWithMembersAndDocs()
    {
        var entry = Build("""
            namespace Contoso.Services;

            /// <summary>Coordinates order lifecycle. Also does more.</summary>
            public sealed class OrderService : IOrderService, IDisposable
            {
                public OrderService(IRepo repo) { }

                /// <summary>Validates and persists an order.</summary>
                public Task<OrderResult> PlaceOrder(OrderRequest req) => throw null!;

                public IReadOnlyList<Order> Pending { get; }

                private int _count;
            }
            """);

        Assert.Equal(["Contoso.Services"], entry.Namespaces);
        var type = Assert.Single(entry.Types);
        Assert.Equal("C", type.Kind);
        Assert.Equal("Contoso.Services.OrderService", type.FqName);
        Assert.Equal(["IOrderService", "IDisposable"], type.Bases);
        Assert.Contains("Coordinates order lifecycle", type.Doc);

        var ctor = type.Members.Single(m => m.Kind == "K");
        Assert.Equal("OrderService(IRepo repo)", ctor.Signature);

        var method = type.Members.Single(m => m.Kind == "M");
        Assert.Equal("PlaceOrder(OrderRequest req) -> Task<OrderResult>", method.Signature);
        Assert.Equal("Validates and persists an order.", method.Doc);

        var prop = type.Members.Single(m => m.Kind == "P");
        Assert.Equal("Pending: IReadOnlyList<Order> {get}", prop.Signature);

        var field = type.Members.Single(m => m.Kind == "F");
        Assert.False(field.IsPublic);
    }

    [Fact]
    public void GenericsInterfacesAndNestedTypes()
    {
        var entry = Build("""
            namespace N
            {
                public interface IRepo<T> where T : class
                {
                    T? Find(int id);
                }

                public class Outer
                {
                    public enum Mode { Fast, Slow = 5 }
                }
            }
            """);

        var repo = entry.Types.Single(t => t.Kind == "I");
        Assert.Equal("IRepo<T>", repo.Name);
        Assert.Equal("N.IRepo<T>", repo.FqName);
        // Interface members are public by default.
        Assert.True(repo.Members.Single().IsPublic);

        var outer = entry.Types.Single(t => t.Kind == "C");
        var mode = Assert.Single(outer.Nested);
        Assert.Equal("E", mode.Kind);
        Assert.Equal("N.Outer.Mode", mode.FqName);
        Assert.Equal(["Fast", "Slow = 5"], mode.Members.Select(m => m.Signature).ToArray());
    }

    [Fact]
    public void RecordPrimaryConstructorAndDelegate()
    {
        var entry = Build("""
            namespace N;
            public record Money(decimal Amount, string Currency);
            public delegate int Combine(int a, int b);
            """);

        var record = entry.Types.Single(t => t.Kind == "R");
        var ctor = record.Members.Single(m => m.Kind == "K");
        Assert.Equal("Money(decimal Amount, string Currency)", ctor.Signature);

        var del = entry.Types.Single(t => t.Kind == "D");
        Assert.Equal("Combine(int a, int b) -> int", del.Members.Single().Signature);
    }

    [Fact]
    public void DocSummaryStripsSeeCrefAndCollapsesWhitespace()
    {
        var entry = Build("""
            namespace N;
            /// <summary>
            /// Uses <see cref="T:System.Decimal"/> math
            /// end-to-end.
            /// </summary>
            public class PriceCalculator { }
            """);

        Assert.Equal("Uses Decimal math end-to-end.", entry.Types.Single().Doc);
    }

    /// <summary>
    /// ISymbol.GetDocumentationCommentXml (unlike the raw source trivia the other cref test exercises)
    /// compiles a generic method's cref into its full metadata name: arity marker, and every parameter
    /// type's own dotted namespace packed into the same attribute value. Splitting that whole string on
    /// '.' and taking the last piece lands inside the parameter list rather than on the member name —
    /// this is the shape that actually shipped garbled ("...Object}});" instead of "Of").
    /// </summary>
    [Fact]
    public void SummaryFromXmlResolvesAGenericMethodCrefToItsMemberNameNotAFragmentOfItsParameterList()
    {
        var xml = """
            <summary>
            See <see cref="M:DotnetToolkit.McpServer.Output.CompactTable.Of``1(System.Collections.Generic.IReadOnlyList{System.String},System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Collections.Generic.IReadOnlyList{System.Object}})"/> for details.
            </summary>
            """;

        Assert.Equal("See Of for details.", OutlineBuilder.SummaryFromXml(xml));
    }

    [Fact]
    public void FileScopedAndBlockNamespacesBothIndex()
    {
        var blockNs = Build("namespace A { public class X { } }");
        var fileNs = Build("namespace A;\npublic class X { }");
        Assert.Equal(blockNs.Namespaces, fileNs.Namespaces);
        Assert.Equal(blockNs.Types.Single().FqName, fileNs.Types.Single().FqName);
    }
}
