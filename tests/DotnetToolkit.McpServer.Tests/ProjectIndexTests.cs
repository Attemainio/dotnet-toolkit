using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

public sealed class ProjectIndexTests : IDisposable
{
    private readonly string _root;
    private readonly SolutionLocator _locator;

    public ProjectIndexTests()
    {
        _root = Directory.CreateTempSubdirectory("index-tests-").FullName;
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Widget.cs"), """
            namespace N;
            public class Widget { public int Spin(int t) => t; }
            """);
        File.WriteAllText(Path.Combine(_root, "src", "Gadget.cs"), """
            namespace N;
            public class Gadget { }
            """);
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "bin", "Generated.cs"), "public class ShouldNotIndex { }");
        _locator = new SolutionLocator(NullLogger<SolutionLocator>.Instance, _root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<ProjectIndex> CreateReadyIndexAsync()
    {
        var index = new ProjectIndex(_locator, NullLogger<ProjectIndex>.Instance);
        index.StartInitialization();
        await index.EnsureFreshAsync();
        return index;
    }

    [Fact]
    public async Task IndexesFilesAndSkipsBinDirs()
    {
        var index = await CreateReadyIndexAsync();
        Assert.Equal("ready", index.State);
        Assert.Equal(2, index.FileCount);
        Assert.Null(index.GetFile("bin/Generated.cs"));
        Assert.NotNull(index.GetFile("src/Widget.cs"));
    }

    [Fact]
    public async Task FindSymbolRanksExactBeforeSubstringAndFindsMembers()
    {
        var index = await CreateReadyIndexAsync();

        var (typeHits, _) = index.FindSymbol("Widget", kind: null, limit: 10);
        Assert.Equal("N.Widget", typeHits[0].FqName);

        var (memberHits, _) = index.FindSymbol("Spin", kind: "method", limit: 10);
        var hit = Assert.Single(memberHits);
        Assert.Equal("N.Widget.Spin", hit.FqName);
        Assert.Equal("Spin(int t) -> int", hit.Signature);
    }

    [Fact]
    public async Task ChangedFileIsReindexedOnForceRescan()
    {
        var index = await CreateReadyIndexAsync();
        var changedFiles = new List<string>();
        index.FilesChanged += (changed, _) => changedFiles.AddRange(changed);

        var path = Path.Combine(_root, "src", "Gadget.cs");
        File.WriteAllText(path, """
            namespace N;
            public class Gadget { public void Renamed() { } }
            """);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        await index.ForceRescanAsync();
        Assert.Contains("src/Gadget.cs", changedFiles);
        var (hits, _) = index.FindSymbol("Renamed", null, 10);
        Assert.Single(hits);
    }

    [Fact]
    public async Task CacheIsReusedAcrossInstances()
    {
        var first = await CreateReadyIndexAsync();
        Assert.Equal(2, first.FileCount);
        Assert.True(File.Exists(Path.Combine(_locator.CacheDir, "index.json")));

        var second = await CreateReadyIndexAsync();
        Assert.Equal(2, second.FileCount);
        var (hits, _) = second.FindSymbol("Gadget", null, 10);
        Assert.Single(hits);
    }

    [Fact]
    public async Task FirstSweepEstablishesProjectFileBaselineWithoutSignalling()
    {
        File.WriteAllText(Path.Combine(_root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var index = await CreateReadyIndexAsync();
        var reloads = 0;
        index.ProjectFilesChanged += () => reloads++;

        await index.ForceRescanAsync();

        // The baseline is taken during initialization, so a sweep that finds nothing moved must stay
        // silent — otherwise every server start would pay for an immediate redundant reload.
        Assert.Equal(0, reloads);
    }

    [Fact]
    public async Task EditedProjectFileSignalsReload()
    {
        var csproj = Path.Combine(_root, "App.csproj");
        File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var index = await CreateReadyIndexAsync();
        var reloads = 0;
        index.ProjectFilesChanged += () => reloads++;

        File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup /></Project>");
        File.SetLastWriteTimeUtc(csproj, DateTime.UtcNow.AddMinutes(1));

        await index.ForceRescanAsync();
        Assert.Equal(1, reloads);
    }

    [Fact]
    public async Task AddedProjectFileSignalsReload()
    {
        var index = await CreateReadyIndexAsync();
        var reloads = 0;
        index.ProjectFilesChanged += () => reloads++;

        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");

        await index.ForceRescanAsync();
        Assert.Equal(1, reloads);
    }

    [Fact]
    public async Task GeneratedFilesUnderObjDoNotSignalReload()
    {
        var index = await CreateReadyIndexAsync();
        var reloads = 0;
        index.ProjectFilesChanged += () => reloads++;

        // restore rewrites these on every run. Watching them would make each reload's own restore trip
        // the next reload, indefinitely.
        Directory.CreateDirectory(Path.Combine(_root, "obj"));
        File.WriteAllText(Path.Combine(_root, "obj", "App.csproj.nuget.g.props"), "<Project />");

        await index.ForceRescanAsync();
        Assert.Equal(0, reloads);
    }
}
