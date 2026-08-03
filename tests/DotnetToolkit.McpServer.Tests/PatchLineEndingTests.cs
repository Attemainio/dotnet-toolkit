using DotnetToolkit.McpServer.Validation;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// A patch hunk always arrives with LF endings, because it comes over JSON. These cover splicing it
/// into a file that does not use them - the ordinary case on a Windows checkout with no
/// <c>.gitattributes</c> forcing LF, where the old behavior left the file mixed and made every
/// applied patch look like it rewrote lines it never touched.
/// </summary>
public sealed class PatchLineEndingTests
{
    [Fact]
    public void DominantLineEnding_CrlfFile_ReturnsCrlf() =>
        Assert.Equal("\r\n", PatchSandbox.DominantLineEnding(SourceText.From("a\r\nb\r\nc\r\n")));

    [Fact]
    public void DominantLineEnding_LfFile_ReturnsLf() =>
        Assert.Equal("\n", PatchSandbox.DominantLineEnding(SourceText.From("a\nb\nc\n")));

    [Fact]
    public void DominantLineEnding_MixedFile_ReturnsTheMajority() =>
        Assert.Equal("\r\n", PatchSandbox.DominantLineEnding(SourceText.From("a\r\nb\r\nc\nd\r\n")));

    [Fact]
    public void DominantLineEnding_SingleLineWithNoBreak_DefaultsToLf() =>
        Assert.Equal("\n", PatchSandbox.DominantLineEnding(SourceText.From("no trailing newline")));

    [Fact]
    public void Retarget_LfHunkIntoCrlfFile_RewritesEveryBreak() =>
        Assert.Equal("one\r\ntwo\r\nthree", PatchSandbox.Retarget("one\ntwo\nthree", "\r\n"));

    [Fact]
    public void Retarget_CrlfHunkIntoLfFile_RewritesEveryBreak() =>
        Assert.Equal("one\ntwo", PatchSandbox.Retarget("one\r\ntwo", "\n"));

    [Fact]
    public void Retarget_CrlfHunkIntoCrlfFile_DoesNotDoubleTheCarriageReturn() =>
        Assert.Equal("one\r\ntwo", PatchSandbox.Retarget("one\r\ntwo", "\r\n"));

    [Fact]
    public void Retarget_SingleLineHunk_IsUnchanged() =>
        Assert.Equal("    return 0;", PatchSandbox.Retarget("    return 0;", "\r\n"));
}
