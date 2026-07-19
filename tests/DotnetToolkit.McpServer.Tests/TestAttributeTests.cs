using DotnetToolkit.McpServer.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Test detection reads the attributes on a method's own declaration. The rule it replaced asked
/// whether the containing project referenced xunit, which was both imprecise (every helper in a test
/// project counted) and un-invalidatable (it depended on MSBuild load state, which no content hash
/// can express).
/// </summary>
public class TestAttributeTests
{
    private const string Source = """
        namespace Demo;

        public sealed class FactAttribute : System.Attribute { }
        public sealed class TheoryAttribute : System.Attribute { }
        public sealed class TestAttribute : System.Attribute { }
        public sealed class TestMethodAttribute : System.Attribute { }
        public sealed class ObsoleteMarkerAttribute : System.Attribute { }

        public class Suite
        {
            [Fact] public void XunitFact() { }
            [Theory] public void XunitTheory() { }
            [Test] public void NUnitTest() { }
            [TestMethod] public void MsTestMethod() { }

            [ObsoleteMarker] public void NotATest() { }
            public void PlainHelper() { }
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
    [InlineData("XunitFact")]
    [InlineData("XunitTheory")]
    [InlineData("NUnitTest")]
    [InlineData("MsTestMethod")]
    public void RecognisesTestAttributesAcrossFrameworks(string member) =>
        Assert.True(TestAttributes.IsTestMethod(Member(member)));

    /// <summary>
    /// The precision the project-level check lacked: a helper sitting in a test file is not a test,
    /// and counting it as one overstates a symbol's coverage.
    /// </summary>
    [Theory]
    [InlineData("NotATest")]
    [InlineData("PlainHelper")]
    public void DoesNotMarkNonTestMethods(string member) =>
        Assert.False(TestAttributes.IsTestMethod(Member(member)));

    /// <summary>Only methods can be tests; a property never is, whatever it carries.</summary>
    [Fact]
    public void DoesNotMarkNonMethods() =>
        Assert.False(TestAttributes.IsTestMethod(Member("Property")));
}
