using DotnetToolkit.McpServer.Tools;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

public class VocabularyHintTests
{
    private static readonly string[] Direction = ["callers", "callees"];
    private static readonly string[] Ladder =
        ["parse", "semantic_bind", "project_compile", "dependent_compile", "targeted_tests", "solution_validate"];

    [Fact]
    public void ExactMatchReturnsNull()
    {
        Assert.Null(VocabularyHint.NearestToken("callers", Direction));
        Assert.Null(VocabularyHint.NearestToken("Callers", Direction));
    }

    [Fact]
    public void SingularPluralMismatchIsFound()
    {
        Assert.Equal("project", VocabularyHint.NearestToken("projects", ["project"]));
    }

    [Fact]
    public void MissingUnderscoreIsFound()
    {
        Assert.Equal("solution_validate", VocabularyHint.NearestToken("solutionvalidate", Ladder));
        Assert.Equal("project_compile", VocabularyHint.NearestToken("project compile", Ladder));
    }

    [Fact]
    public void ATypoOfTheNonDefaultValueIsFound()
    {
        Assert.Equal("callees", VocabularyHint.NearestToken("callee", Direction));
        Assert.Equal("callees", VocabularyHint.NearestToken("calees", Direction));
    }

    [Fact]
    public void AnEquidistantTieReturnsNullRatherThanGuessing()
    {
        Assert.Null(VocabularyHint.NearestToken("call", Direction));
    }

    [Fact]
    public void SomethingUnrelatedReturnsNull()
    {
        Assert.Null(VocabularyHint.NearestToken("xyz123", Direction));
        Assert.Null(VocabularyHint.NearestToken("implementations", Direction));
    }

    [Fact]
    public void NullOrWhitespaceReturnsNull()
    {
        Assert.Null(VocabularyHint.NearestToken(null, Direction));
        Assert.Null(VocabularyHint.NearestToken("", Direction));
        Assert.Null(VocabularyHint.NearestToken("   ", Direction));
    }
}
