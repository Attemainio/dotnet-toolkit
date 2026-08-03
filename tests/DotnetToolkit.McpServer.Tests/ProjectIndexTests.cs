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

    /// <summary>
    /// Two overloads sharing a name AND a parameter count are the case arity cannot separate. The requested
    /// name's parameter TYPES pick each one out, so both keep their own line instead of both being dropped
    /// as ambiguous and costing a get_symbol round trip purely to navigate.
    /// </summary>
    [Fact]
    public async Task LocatesOverloadsThatCollideOnNameAndArity()
    {
        File.WriteAllText(Path.Combine(_root, "src", "Overloads.cs"), """
            namespace N;
            public class Overloads
            {
                public int Pick(int only) => only;
                public int Pick(string only) => only.Length;
            }
            """);
        var index = await CreateReadyIndexAsync();

        var located = index.Locate(new HashSet<string>(StringComparer.Ordinal)
        {
            "N.Overloads.Pick(int)",
            "N.Overloads.Pick(string)",
        });

        Assert.Equal(2, located.Count);
        Assert.Equal(4, located["N.Overloads.Pick(int)"].Line);
        Assert.Equal(5, located["N.Overloads.Pick(string)"].Line);
    }

    /// <summary>
    /// A method states its own type parameters in the indexed signature but not in the indexed name, so the
    /// symbol store's <c>Pick&lt;T&gt;</c> form matched no key at all and every generic method came back
    /// with no file or line. The non-generic sibling is here to prove the added key separates them rather
    /// than merging both onto one site.
    /// </summary>
    [Fact]
    public async Task LocatesAMethodDeclaringItsOwnTypeParameters()
    {
        File.WriteAllText(Path.Combine(_root, "src", "Generic.cs"), """
            namespace N;
            public class Generic
            {
                public int Pick(int only) => only;
                public T Pick<T>(T only) => only;
            }
            """);
        var index = await CreateReadyIndexAsync();

        var located = index.Locate(new HashSet<string>(StringComparer.Ordinal)
        {
            "N.Generic.Pick<T>(T)",
            "N.Generic.Pick(int)",
        });

        Assert.Equal(2, located.Count);
        Assert.Equal(5, located["N.Generic.Pick<T>(T)"].Line);
        Assert.Equal(4, located["N.Generic.Pick(int)"].Line);
    }

    [Fact]
    public async Task LocatesTheSynthesizedEntryPointOfATopLevelStatementsFile()
    {
        File.WriteAllText(Path.Combine(_root, "src", "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "src", "Program.cs"), """
            using System;
            Console.WriteLine(1);
            """);
        var index = await CreateReadyIndexAsync();

        // "Program.Main" is the name SymbolIndexBuilder stores the semantic entry point under. Until the
        // syntax tier offered the same key this returned nothing, so search_index reported the row with no
        // file/line and ANY pathPrefix silently excluded it -- an unlocated hit is treated as out of scope,
        // which made Program.cs look unreachable through the tools.
        var located = index.Locate(new HashSet<string>(StringComparer.Ordinal) { "Program.Main" });

        var site = Assert.Single(located).Value;
        Assert.Equal("src/Program.cs", site.File);
        Assert.Equal(1, site.Line);
    }

    [Fact]
    public async Task SynthesizedEntryPointIsScopedToFilesAProjectCompiles()
    {
        File.WriteAllText(Path.Combine(_root, "src", "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "src", "Program.cs"), "System.Console.WriteLine(1);");

        // The synthesized type is called Program whatever the file is named, so every top-level-statements
        // file in the tree competes for one key. A sample solution under tests/ is its own independent
        // tree and a standalone `dotnet run` script belongs to no project at all — indexing those left
        // three equally-good candidates and Disambiguate correctly resolved to none, dropping the location
        // of the one entry point that mattered.
        var fixture = Path.Combine(_root, "tests", "Sample");
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "Sample.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(fixture, "Sample.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(fixture, "Program.cs"), "System.Console.WriteLine(2);");

        Directory.CreateDirectory(Path.Combine(_root, "scripts"));
        File.WriteAllText(Path.Combine(_root, "scripts", "Program.cs"), "System.Console.WriteLine(3);");

        var index = await CreateReadyIndexAsync();

        var located = index.Locate(new HashSet<string>(StringComparer.Ordinal) { "Program.Main" });

        Assert.Equal("src/Program.cs", Assert.Single(located).Value.File);
    }

    [Fact]
    public async Task LocatesGenericsWhateverTheirTypeParametersAreNamedOrSpaced()
    {
        File.WriteAllText(Path.Combine(_root, "src", "Shapes.cs"), """
            namespace N;
            public class Cache<TKey,TValue>
            {
                public TValue Convert<TInput,TResult>(TInput input) => default!;
                public TItem Only<TItem>(TItem item) => item;
            }
            public delegate TOut Mapper<TIn,TOut>(TIn input);
            """);
        var index = await CreateReadyIndexAsync();

        var located = index.Locate(new HashSet<string>(StringComparer.Ordinal)
        {
            "N.Cache<TKey, TValue>",
            "N.Cache<TKey, TValue>.Convert<TInput, TResult>(TInput)",
            "N.Cache<TKey, TValue>.Only<TItem>(TItem)",
            "N.Mapper<TIn, TOut>",
        });

        Assert.Equal(2, located["N.Cache<TKey, TValue>"].Line);
        Assert.Equal(4, located["N.Cache<TKey, TValue>.Convert<TInput, TResult>(TInput)"].Line);
        Assert.Equal(5, located["N.Cache<TKey, TValue>.Only<TItem>(TItem)"].Line);
        Assert.Equal(7, located["N.Mapper<TIn, TOut>"].Line);
    }
}
