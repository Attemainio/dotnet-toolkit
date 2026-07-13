using DotnetToolkit.McpServer.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

public sealed class SolutionLocatorTests : IDisposable
{
    private readonly string _root;

    public SolutionLocatorTests()
    {
        _root = Directory.CreateTempSubdirectory("locator-tests-").FullName;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SolutionLocator Create() => new(NullLogger<SolutionLocator>.Instance, _root);

    [Fact]
    public void SlnxPreferredOverSlnAndCsproj()
    {
        File.WriteAllText(Path.Combine(_root, "App.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(_root, "App.sln"), "");
        File.WriteAllText(Path.Combine(_root, "App.csproj"), "<Project />");

        var locator = Create();
        Assert.Equal("App.slnx", Path.GetFileName(locator.WorkspaceEntry));
    }

    [Fact]
    public void MultipleSolutionsReportAmbiguity()
    {
        File.WriteAllText(Path.Combine(_root, "A.sln"), "");
        File.WriteAllText(Path.Combine(_root, "B.sln"), "");

        var locator = Create();
        Assert.Null(locator.WorkspaceEntry);
        Assert.True(locator.IsAmbiguous);
        Assert.Equal(2, locator.Candidates.Count);
    }

    [Fact]
    public void ConfigSolutionOverridesGlobbing()
    {
        File.WriteAllText(Path.Combine(_root, "A.sln"), "");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "Real.sln"), "");
        Directory.CreateDirectory(Path.Combine(_root, ".claude", "dotnet-toolkit"));
        File.WriteAllText(Path.Combine(_root, ".claude", "dotnet-toolkit", "config.json"),
            """{ "solution": "sub/Real.sln" }""");

        var locator = Create();
        Assert.Equal("Real.sln", Path.GetFileName(locator.WorkspaceEntry));
    }

    [Fact]
    public void FindsSolutionOneLevelDeepButSkipsBinDirs()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "App.sln"), "");
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "bin", "Stale.sln"), "");

        var locator = Create();
        Assert.Equal(Path.Combine(_root, "src", "App.sln"), locator.WorkspaceEntry);
    }

    [Fact]
    public void EnsureCacheDirWritesSelfGitignore()
    {
        var locator = Create();
        var dir = locator.EnsureCacheDir();
        Assert.Equal("*\n", File.ReadAllText(Path.Combine(dir, ".gitignore")));
    }

    [Fact]
    public void AbsPathRejectsEscapes()
    {
        var locator = Create();
        Assert.Throws<ArgumentException>(() => locator.AbsPath("../outside.cs"));
        Assert.Equal(Path.Combine(_root, "a", "b.cs"), locator.AbsPath("a/b.cs"));
    }
}
