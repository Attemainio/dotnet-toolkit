using DotnetToolkit.McpServer.Diagnostics;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Covers the source digest that lets a published <c>dist/</c> be proven to match the tree it was built
/// from.
/// </summary>
/// <remarks>
/// The properties asserted here are the ones the gate depends on: the digest must change when anything
/// the build compiles changes, must not change when something the build only copies changes, and must
/// be reproducible from the same tree. A digest that drifts on a documentation edit gets republished
/// past rather than obeyed, and one that misses a source edit is the failure it exists to catch.
/// </remarks>
public sealed class SourceStampTests : IDisposable
{
    private readonly string _root;

    public SourceStampTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dotnet-toolkit-stamp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "Lib"));
        File.WriteAllText(Path.Combine(_root, "src", "Lib", "Widget.cs"), "class Widget;");
        File.WriteAllText(Path.Combine(_root, "src", "Lib", "Lib.csproj"), "<Project />");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Compute_SameTreeTwice_IsStable() =>
        Assert.Equal(SourceStamp.Compute(_root), SourceStamp.Compute(_root));

    [Fact]
    public void Compute_ChangedSourceFile_ChangesTheDigest()
    {
        var before = SourceStamp.Compute(_root);

        File.WriteAllText(Path.Combine(_root, "src", "Lib", "Widget.cs"), "class Widget { }");

        Assert.NotEqual(before, SourceStamp.Compute(_root));
    }

    [Fact]
    public void Compute_AddedSourceFile_ChangesTheDigest()
    {
        var before = SourceStamp.Compute(_root);

        File.WriteAllText(Path.Combine(_root, "src", "Lib", "Gadget.cs"), "class Gadget;");

        Assert.NotEqual(before, SourceStamp.Compute(_root));
    }

    /// <summary>
    /// A rename changes what the build produces even when every byte of content is unchanged, so the
    /// path has to be part of the digest rather than only the content hashes.
    /// </summary>
    [Fact]
    public void Compute_RenamedSourceFile_ChangesTheDigest()
    {
        var before = SourceStamp.Compute(_root);

        File.Move(
            Path.Combine(_root, "src", "Lib", "Widget.cs"),
            Path.Combine(_root, "src", "Lib", "Gadget.cs"));

        Assert.NotEqual(before, SourceStamp.Compute(_root));
    }

    [Fact]
    public void Compute_ChangedBuildProperties_ChangeTheDigest()
    {
        var before = SourceStamp.Compute(_root);

        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");

        Assert.NotEqual(before, SourceStamp.Compute(_root));
    }

    /// <summary>
    /// Documentation, skills and agent briefs ship as files rather than being compiled in, so an edit to
    /// one leaves the published binary correct. A digest that moved here would report <c>dist/</c> stale
    /// after every doc change, and a gate that cries wolf gets worked around.
    /// </summary>
    [Fact]
    public void Compute_NonCompiledFiles_LeaveTheDigestAlone()
    {
        var before = SourceStamp.Compute(_root);

        File.WriteAllText(Path.Combine(_root, "src", "Lib", "README.md"), "docs");

        Assert.Equal(before, SourceStamp.Compute(_root));
    }

    /// <summary>Build output under the source tree is a product of the build, not an input to it.</summary>
    [Fact]
    public void Compute_BuildOutputDirectories_AreExcluded()
    {
        var before = SourceStamp.Compute(_root);

        var obj = Path.Combine(_root, "src", "Lib", "obj");
        Directory.CreateDirectory(obj);
        File.WriteAllText(Path.Combine(obj, "Generated.cs"), "class Generated;");

        Assert.Equal(before, SourceStamp.Compute(_root));
    }

    /// <summary>An installed plugin has no <c>src/</c>, which is a reason to skip the check, not to fail it.</summary>
    [Fact]
    public void Compute_TreeWithoutSources_IsNull() =>
        Assert.Null(SourceStamp.Compute(Path.Combine(_root, "src", "Lib")));

    [Fact]
    public void WriteThenRead_RoundTripsTheDigest()
    {
        Assert.Equal(0, SourceStamp.Write(_root));

        Assert.Equal(SourceStamp.Compute(_root), SourceStamp.Read(_root));
    }

    /// <summary>
    /// The whole point of the gate: a source edit after a publish must be visible as a mismatch, since
    /// the stamp can only be refreshed by republishing.
    /// </summary>
    [Fact]
    public void Read_AfterASourceEditWithoutRepublishing_NoLongerMatches()
    {
        SourceStamp.Write(_root);

        File.WriteAllText(Path.Combine(_root, "src", "Lib", "Widget.cs"), "class Widget { int X; }");

        Assert.NotEqual(SourceStamp.Compute(_root), SourceStamp.Read(_root));
    }

    [Fact]
    public void Read_NoStampWritten_IsNull() =>
        Assert.Null(SourceStamp.Read(_root));
}
