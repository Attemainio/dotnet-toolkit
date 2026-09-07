using DotnetToolkit.McpServer.Hooks;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Covers the command shapes a red-team pass got past the guards, one test per shape that was silent.
/// </summary>
/// <remarks>
/// Kept in its own class rather than folded into <c>BashCommandScannerTests</c> so the set stays legible
/// as what it is: a regression suite for known bypasses. Each of these read or wrote compiled C# with no
/// advisory and no denial, and a guard whose coverage depends on which utility the caller reached for
/// teaches the uncovered spelling rather than the intended route.
/// </remarks>
public sealed class BashScannerGapTests
{
    /// <summary>
    /// The write scanner tokenizes on whitespace, so a redirect with no space around it used to arrive as
    /// a single token and never match the operator scan — while the spaced spelling of the same command
    /// was denied.
    /// </summary>
    [Theory]
    [InlineData("cat a.txt>src/Foo.cs")]
    [InlineData("cat a.txt>>src/Foo.cs")]
    [InlineData("echo x >src/Foo.cs")]
    [InlineData("echo x 1>src/Foo.cs")]
    public void FindWriteTargetCsArgument_UnspacedRedirect_IsRecognized(string command) =>
        Assert.Equal("src/Foo.cs", BashCommandScanner.FindWriteTargetCsArgument(command));

    [Fact]
    public void FindWriteTargetCsArgument_SpacedRedirect_StillRecognized() =>
        Assert.Equal("src/Foo.cs", BashCommandScanner.FindWriteTargetCsArgument("echo x > src/Foo.cs"));

    /// <summary>
    /// A quoted filename containing <c>&gt;</c> is not a redirection, and splitting it would invent a
    /// write target the command never had.
    /// </summary>
    [Fact]
    public void FindWriteTargetCsArgument_RedirectInsideQuotes_IsNotATarget() =>
        Assert.Null(BashCommandScanner.FindWriteTargetCsArgument("""cat "a>b.cs" """));

    /// <summary>
    /// <c>dd</c>'s operands are welded to keys, so neither its read nor its write side is a bare path
    /// operand the suffix test can find.
    /// </summary>
    [Fact]
    public void Dd_ReadsAndWrites_AreBothRecognized()
    {
        Assert.Equal("src/Foo.cs", BashCommandScanner.FindReadTargetCsArgument("dd if=src/Foo.cs"));
        Assert.Equal("src/Foo.cs", BashCommandScanner.FindWriteTargetCsArgument("dd if=/dev/null of=src/Foo.cs"));
    }

    /// <summary>
    /// A path inside an inline program is never an argument, so every argument-shaped check misses it
    /// while the program reads exactly the bytes <c>cat</c> would.
    /// </summary>
    [Theory]
    [InlineData("""python3 -c "print(open('src/Foo.cs').read())" """)]
    [InlineData("""python -c "import sys; sys.stdout.write(open('src/Foo.cs').read())" """)]
    [InlineData("""node -e "console.log(require('fs').readFileSync('src/Foo.cs','utf8'))" """)]
    public void ScriptCsLiteral_InlineProgramReadingACsFile_IsFound(string command) =>
        Assert.Equal("src/Foo.cs", BashCommandScanner.ScriptCsLiteral(command));

    /// <summary>
    /// The interpreter gate is what keeps the literal search from firing on ordinary commands that merely
    /// mention a filename — a grep pattern, a commit message — where it would be noise rather than signal.
    /// </summary>
    [Theory]
    [InlineData("""git commit -m "rewrite src/Foo.cs" """)]
    [InlineData("echo src/Foo.cs")]
    public void ScriptCsLiteral_NonInterpreterCommands_AreNotSearched(string command) =>
        Assert.Null(BashCommandScanner.ScriptCsLiteral(command));

    /// <summary>
    /// A read-only one-liner must stay a read. Denying it as a write would block the exact command the
    /// read guard is meant only to advise on.
    /// </summary>
    [Fact]
    public void FindWriteTargetCsArgument_ScriptThatOnlyReads_IsNotAWrite() =>
        Assert.Null(BashCommandScanner.FindWriteTargetCsArgument(
            """python3 -c "print(open('src/Foo.cs').read())" """));

    [Theory]
    [InlineData("""python3 -c "open('src/Foo.cs','w').write('x')" """)]
    [InlineData("""python3 -c "open('src/Foo.cs','a').write('x')" """)]
    [InlineData("""node -e "require('fs').writeFileSync('src/Foo.cs','x')" """)]
    public void FindWriteTargetCsArgument_ScriptThatWrites_IsAWrite(string command) =>
        Assert.Equal("src/Foo.cs", BashCommandScanner.FindWriteTargetCsArgument(command));

    /// <summary>
    /// The read belongs to the utility <c>-exec</c> names: the segment's own first word is <c>find</c>,
    /// which the blocklist deliberately omits because listing names reads nothing.
    /// </summary>
    [Fact]
    public void ExecutedUtility_FindExecCat_ReportsTheReaderAndTheRoots()
    {
        var executed = BashCommandScanner.ExecutedUtility("""find src -name '*.cs' -exec cat {} ;""");

        Assert.NotNull(executed);
        Assert.Equal("cat", executed.Value.Utility);
        Assert.Equal(["src"], executed.Value.Roots);
    }

    /// <summary>A walk with no path operand searches the current directory, which is what it reports.</summary>
    [Fact]
    public void ExecutedUtility_NoPathOperand_DefaultsToTheCurrentDirectory()
    {
        var executed = BashCommandScanner.ExecutedUtility("""find -name '*.cs' -exec grep -n x {} ;""");

        Assert.NotNull(executed);
        Assert.Equal("grep", executed.Value.Utility);
        Assert.Equal(["."], executed.Value.Roots);
    }

    /// <summary>
    /// <c>find</c> without <c>-exec</c> only lists names, which is why it is not on the blocklist and must
    /// not start being treated as a read.
    /// </summary>
    [Fact]
    public void ExecutedUtility_FindWithoutExec_IsNotARead() =>
        Assert.Null(BashCommandScanner.ExecutedUtility("""find . -name '*.cs'"""));

    [Fact]
    public void ExecutedUtility_NonWalkerCommand_IsNotARead() =>
        Assert.Null(BashCommandScanner.ExecutedUtility("cat src/Foo.cs"));

    /// <summary>
    /// A hex or base64 dump puts the same source in the transcript as <c>cat</c> does, only less legibly,
    /// so the advisory has to reach them too.
    /// </summary>
    [Theory]
    [InlineData("xxd")]
    [InlineData("od")]
    [InlineData("hexdump")]
    [InlineData("base64")]
    [InlineData("strings")]
    [InlineData("python3")]
    [InlineData("perl")]
    [InlineData("node")]
    public void DefaultReadBlocklist_CoversTheByteDumpersAndInterpreters(string command) =>
        Assert.Contains(command, HookContext.DefaultReadBlocklist);

    /// <summary>
    /// <c>find</c> and <c>git</c> stay off the list: neither dumps a file's bytes on its own, and adding
    /// them would advise on every repository listing.
    /// </summary>
    [Theory]
    [InlineData("find")]
    [InlineData("git")]
    [InlineData("ls")]
    public void DefaultReadBlocklist_LeavesNonReadingCommandsAlone(string command) =>
        Assert.DoesNotContain(command, HookContext.DefaultReadBlocklist);
}
