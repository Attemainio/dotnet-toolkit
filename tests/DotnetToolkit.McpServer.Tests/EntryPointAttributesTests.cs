using DotnetToolkit.McpServer.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Covers get_references' zero-caller entry-point hint for the frameworks past tests it now
/// recognises. The 2026-08-17 performance benchmark caught get_references answering "0 callers" for
/// an [McpServerTool]-attributed method — a live entry point invoked by the MCP SDK's own reflection
/// scan, not dead code — and a caller reading the raw count concluded "safe to delete". These stand-in
/// attribute classes exercise the same name-based matching TestAttributeTests uses for [Fact]: the
/// fixture carries no reference to the real frameworks, since EntryPointAttributes matches on the
/// attribute class's own simple name.
/// </summary>
public class EntryPointAttributesTests
{
    private const string Source = """
        namespace Demo;

        public sealed class McpServerToolAttribute : System.Attribute { }
        public sealed class HttpGetAttribute : System.Attribute { }
        public sealed class JsonConverterAttribute : System.Attribute { }
        public sealed class ModuleInitializerAttribute : System.Attribute { }
        public sealed class FactAttribute : System.Attribute { }
        public sealed class ObsoleteMarkerAttribute : System.Attribute { }

        public class Suite
        {
            [McpServerTool] public void SearchEnergyCertificates() { }
            [HttpGet] public void GetWidget() { }
            [JsonConverter] public void ReadJson() { }
            [ModuleInitializer] public static void Init() { }
            [Fact] public void XunitFact() { }

            [ObsoleteMarker] public void NotAnEntryPoint() { }
            public void PlainHelper() { }
            public static void Main() { }
            public int Property { get; set; }
        }
        """;

    private static ISymbol Member(string name)
    {
        var tree = CSharpSyntaxTree.ParseText(Source);
        var compilation = CSharpCompilation.Create("t", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var suite = compilation.GetTypeByMetadataName("Demo.Suite")!;
        return suite.GetMembers(name).First();
    }

    [Theory]
    [InlineData("SearchEnergyCertificates", "McpServerTool")]
    [InlineData("GetWidget", "HttpGet")]
    [InlineData("ReadJson", "JsonConverter")]
    [InlineData("Init", "ModuleInitializer")]
    public void RecognisesAttributeAcrossFrameworks_AndNamesItInTheReason(string member, string expectedAttributeName)
    {
        var reason = EntryPointAttributes.MatchedReason(Member(member));

        Assert.NotNull(reason);
        Assert.Contains($"[{expectedAttributeName}]", reason);
    }

    /// <summary>Delegates to TestAttributes rather than duplicating its list — one recognised set.</summary>
    [Fact]
    public void RecognisesTestAttributes_ViaTestAttributes() =>
        Assert.NotNull(EntryPointAttributes.MatchedReason(Member("XunitFact")));

    [Fact]
    public void RecognisesStaticMain_AsTheProcessEntryPoint()
    {
        var reason = EntryPointAttributes.MatchedReason(Member("Main"));

        Assert.NotNull(reason);
        Assert.Contains("Main", reason);
    }

    [Theory]
    [InlineData("NotAnEntryPoint")]
    [InlineData("PlainHelper")]
    [InlineData("Property")]
    public void DoesNotMarkOrdinaryMembers(string member) =>
        Assert.Null(EntryPointAttributes.MatchedReason(Member(member)));
}
