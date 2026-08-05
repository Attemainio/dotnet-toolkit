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

    /// <summary>
    /// BuildType/BuildMember populate DocSections with a comma-joined list of every recognized XML
    /// doc tag present beyond plain summary text — the presence signal search_index's xmlDoc filter
    /// checks. Null (not empty) when a member has no doc comment at all.
    /// </summary>
    [Fact]
    public void BuildPopulatesDocSectionsWithEveryRecognizedTagPresent()
    {
        var entry = Build("""
            namespace Contoso.Services;

            /// <summary>Coordinates order lifecycle.</summary>
            /// <remarks>Not thread-safe.</remarks>
            public sealed class OrderService
            {
                /// <returns>The placed order's id.</returns>
                public int PlaceOrder() => 1;

                public int Cancel() => 0;
            }
            """);

        var type = Assert.Single(entry.Types);
        Assert.Equal("summary,remarks", type.DocSections);

        var placeOrder = type.Members.Single(m => m.Name == "PlaceOrder");
        Assert.Equal("returns", placeOrder.DocSections);

        var cancel = type.Members.Single(m => m.Name == "Cancel");
        Assert.Null(cancel.DocSections);
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
    public void SectionsFromXmlExtractsEveryRecognizedTag()
    {
        var xml = """
            <summary>Builds a widget.</summary>
            <param name="count">How many to build.</param>
            <typeparam name="T">The widget's element type.</typeparam>
            <returns>The built widgets.</returns>
            <exception cref="T:System.ArgumentException">count is negative.</exception>
            <remarks>Not thread-safe.</remarks>
            """;

        var sections = OutlineBuilder.SectionsFromXml(xml);

        Assert.Equal("Builds a widget.", sections?.Summary);
        Assert.Equal("The built widgets.", sections?.Returns);
        Assert.Equal("Not thread-safe.", sections?.Remarks);
        Assert.Equal("count", sections?.Params?.Single().Name);
        Assert.Equal("How many to build.", sections?.Params?.Single().Text);
        Assert.Equal("T", sections?.TypeParams?.Single().Name);
        Assert.Equal("ArgumentException", sections?.Exceptions?.Single().Type);
        Assert.Null(sections?.Inheritdoc);
    }

    [Fact]
    public void SectionsFromXmlSurfacesInheritdocEvenWithNoOtherTag()
    {
        var sections = OutlineBuilder.SectionsFromXml("<inheritdoc/>");

        Assert.True(sections?.Inheritdoc);
        Assert.Null(sections?.Summary);
    }

    [Fact]
    public void SectionsFromXmlReturnsNullWhenNoRecognizedTagIsPresent()
    {
        Assert.Null(OutlineBuilder.SectionsFromXml("just text, no tags"));
        Assert.Null(OutlineBuilder.SectionsFromXml(null));
    }

    [Fact]
    public void FileScopedAndBlockNamespacesBothIndex()
    {
        var blockNs = Build("namespace A { public class X { } }");
        var fileNs = Build("namespace A;\npublic class X { }");
        Assert.Equal(blockNs.Namespaces, fileNs.Namespaces);
        Assert.Equal(blockNs.Types.Single().FqName, fileNs.Types.Single().FqName);
    }

    [Fact]
    public void TopLevelStatementsIndexTheSynthesizedEntryPoint()
    {
        var entry = Build("using System;\nConsole.WriteLine(1);\nConsole.WriteLine(2);\n");

        // Program.Main is the name SymbolIndexBuilder stores the semantic entry point under, so this is
        // the key ProjectIndex.LocateWithDocs has to be able to offer — without it the row resolves to no
        // site, search_index reports no file/line, and every pathPrefix filter silently excludes it.
        var program = Assert.Single(entry.Types);
        Assert.Equal("Program", program.FqName);
        var main = Assert.Single(program.Members);
        Assert.Equal("Main", main.Name);

        // Spans the whole compilation unit, matching what get_symbol reports for the semantic entry point.
        Assert.Equal(1, program.Line);
        Assert.Equal(main.Line, program.Line);
        Assert.Equal(main.EndLine, program.EndLine);
    }

    [Fact]
    public void FileWithoutTopLevelStatementsGainsNoSyntheticEntryPoint()
    {
        Assert.Empty(Build("namespace A;\n").Types);
        Assert.Equal("A.X", Build("namespace A;\npublic class X { }").Types.Single().FqName);
    }

    [Fact]
    public void DocAndCommentLinesAreCountedPerDeclaration()
    {
        var entry = Build("""
            namespace Contoso;

            /// <summary>
            /// A three-line doc comment.
            /// </summary>
            public sealed class Gadget
            {
                // one
                // two
                public int Spin() => 0;
            }
            """);

        // Three /// lines, not four: trivia's span runs through the newline that ends it, so a naive
        // end-minus-start over-counts by one and this is where that would show.
        var type = Assert.Single(entry.Types);
        Assert.Equal(3, type.DocLines);

        var method = type.Members.Single(m => m.Kind == "M");
        Assert.Equal(0, method.DocLines);
        Assert.Equal(2, method.CommentLines);
    }

    // A type's comment count is what fetching it WHOLE would cost, so it counts its members' comments
    // as well as its own -- the overlap with each member's own count is deliberate, not a bug.
    [Fact]
    public void TypeCommentLinesIncludeItsMembersComments()
    {
        var entry = Build("""
            namespace Contoso;

            public sealed class Gadget
            {
                // at class scope
                private int _count;

                public int Spin()
                {
                    // inside a body
                    return _count;
                }
            }
            """);

        var type = Assert.Single(entry.Types);
        Assert.Equal(2, type.CommentLines);

        var method = type.Members.Single(m => m.Kind == "M");
        Assert.Equal(1, method.CommentLines);
    }

    /// <summary>
    /// An empty XML-doc reference element carries its target in an attribute, so stripping it as a plain
    /// tag deletes a word from the sentence rather than leaving a gap a reader could notice.
    /// </summary>
    [Fact]
    public void SummaryKeepsEmptyRefElementNames()
    {
        var summary = OutlineBuilder.SummaryFromXml("""
            <member name="M:Contoso.Gadget.Spin">
              <summary>Translates every filter on <paramref name="normalized"/> into one <c>WHERE</c> body for <typeparamref name="TRecord"/>, like <see cref="T:Contoso.Other"/> does.</summary>
            </member>
            """);

        // Without the name substitution this reads "Translates every filter on into one WHERE body for ,
        // like does." -- which looks like the author wrote it badly, not like a rendering fault.
        Assert.Equal(
            "Translates every filter on normalized into one WHERE body for TRecord, like Other does.",
            summary);
    }

    /// <summary>
    /// A type's doc-line count is transitive over its members, exactly as its comment-line count is:
    /// the D column's contract is what source:code strips relative to source:full, and that strip takes
    /// member doc comments with it.
    /// </summary>
    [Fact]
    public void TypeDocLinesIncludeItsMembersDocComments()
    {
        var entry = Build("""
            namespace Contoso;

            /// <summary>
            /// A three-line doc comment.
            /// </summary>
            public sealed class Gadget
            {
                /// <summary>Documented member.</summary>
                public int Spin() => 0;
            }
            """);

        // 3 at class scope + 1 on the member. Counting only the declaration's own leading trivia gives 3,
        // which under-reports what fetching the whole type would strip.
        var type = Assert.Single(entry.Types);
        Assert.Equal(4, type.DocLines);

        var method = type.Members.Single(m => m.Kind == "M");
        Assert.Equal(1, method.DocLines);
    }

    [Fact]
    public void MultiLineCommentCountsEveryLineItSpans()
    {
        var entry = Build("""
            namespace Contoso;

            public sealed class Gadget
            {
                /* one
                   two
                   three */
                public int Spin() => 0;
            }
            """);

        var method = Assert.Single(entry.Types).Members.Single(m => m.Kind == "M");
        Assert.Equal(3, method.CommentLines);
    }

    [Fact]
    public void PlainDeclarationCountsNoDocOrCommentLines()
    {
        var entry = Build("""
            namespace Contoso;

            public sealed class Gadget
            {
                public int Spin() => 0;
            }
            """);

        var type = Assert.Single(entry.Types);
        Assert.Equal(0, type.DocLines);
        Assert.Equal(0, type.CommentLines);
    }
}
