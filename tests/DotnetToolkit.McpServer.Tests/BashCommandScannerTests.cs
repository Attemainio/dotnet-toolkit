using DotnetToolkit.McpServer.Hooks;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

public sealed class BashCommandScannerTests
{
    [Fact]
    public void Segments_PipelineSeparators_SplitsEachStage()
    {
        var segments = BashCommandScanner.Segments("cat a.cs | head -n 5; grep x b.cs && wc -l");

        Assert.Equal(["cat a.cs", "head -n 5", "grep x b.cs", "wc -l"], segments);
    }

    [Fact]
    public void Segments_SeparatorInsideDoubleQuotes_DoesNotSplit()
    {
        // The regression this whole scanner exists for: a plain split on every separator character
        // broke this idiom into segments where the one carrying the .cs path did not start with a
        // read utility, so the guard silently allowed it.
        var segments = BashCommandScanner.Segments("""grep -n "Alpha\|Beta\|Gamma" src/Foo.cs | head""");

        Assert.Equal(2, segments.Count);
        Assert.Equal("""grep -n "Alpha\|Beta\|Gamma" src/Foo.cs""", segments[0]);
        Assert.Equal("head", segments[1]);
    }

    [Fact]
    public void Segments_SeparatorInsideSingleQuotes_DoesNotSplit()
    {
        var segments = BashCommandScanner.Segments("grep 'a|b' src/Foo.cs");

        Assert.Equal(["grep 'a|b' src/Foo.cs"], segments);
    }

    [Fact]
    public void Segments_EscapedSeparator_DoesNotSplit()
    {
        var segments = BashCommandScanner.Segments(@"grep a\|b src/Foo.cs");

        Assert.Single(segments);
    }

    [Theory]
    [InlineData("cat Foo.cs", "cat")]
    [InlineData("/usr/bin/sed -n 1,5p Foo.cs", "sed")]
    [InlineData(@"C:\tools\grep.exe -n x Foo.cs", "grep")]
    public void CommandName_StripsDirectoryAndExecutableSuffix(string segment, string expected) =>
        Assert.Equal(expected, BashCommandScanner.CommandName(segment));

    [Fact]
    public void FindCsArgument_QuotedPath_StripsSurroundingQuotes()
    {
        // Tokens arrive already expanded, so the quote characters are still attached and would
        // otherwise defeat the .cs suffix test.
        Assert.Equal("src/Foo.cs", BashCommandScanner.FindCsArgument("""cat "src/Foo.cs" """));
    }

    [Fact]
    public void FindCsArgument_OptionFlags_AreNotTreatedAsPaths()
    {
        Assert.Null(BashCommandScanner.FindCsArgument("grep -n --include=*.cs pattern"));
    }

    [Fact]
    public void FindCsArgument_NoCsFile_ReturnsNull()
    {
        Assert.Null(BashCommandScanner.FindCsArgument("cat README.md"));
    }
}
