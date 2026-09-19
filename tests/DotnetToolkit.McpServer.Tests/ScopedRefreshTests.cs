using System.Net.Sockets;
using System.Text.Json;
using DotnetToolkit.McpServer.Control;
using DotnetToolkit.McpServer.Hooks;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// The symbol store has to follow an edit made with ANY tool — Edit, a shell redirect, an external
/// editor — without a manual reload_workspace. Before these existed, the store subscribed to nothing:
/// only the apply path of the (since removed) patch tool ever rebuilt it, so a file edited any other
/// way left search_index, every sym_ id and the edge cache frozen until a full MSBuild reload. Each
/// test here writes to disk and restores the file in its finally, since the fixture is shared.
/// </summary>
[Collection("SampleSolution")]
public sealed class ScopedRefreshTests
{
    private readonly SampleSolutionFixture _f;

    public ScopedRefreshTests(SampleSolutionFixture fixture) => _f = fixture;

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    private Task<string> Search(string query) =>
        ContextTools.SearchIndex(_f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, query, groupBy: "none");

    private static List<string> Names(JsonElement root) =>
        root.TryGetProperty("items", out var items)
            ? items.EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToList()
            : [];

    /// <summary>Writes the file and pushes it through the same two steps the refresh hook drives.</summary>
    private async Task<SymbolIndexBuilder.RefreshResult> WriteAndRefreshAsync(string abs, string text)
    {
        await File.WriteAllTextAsync(abs, text);
        File.SetLastWriteTimeUtc(abs, DateTime.UtcNow.AddSeconds(5));   // a same-second rewrite must still register
        await _f.Index.RefreshPathsAsync([abs]);
        return await _f.Builder.RefreshDocumentsAsync([abs]);
    }

    [Fact]
    public async Task EditedMember_IsSearchableAndItsIdResolves_WithoutAReload()
    {
        var path = _f.Locator.AbsPath("Lib/UsingsSample.cs");
        var original = await File.ReadAllTextAsync(path);
        var symbolsBefore = _f.Symbols.SymbolCount();
        try
        {
            var edited = original.Replace(
                "public DateTime Stamp() => DateTime.UtcNow;",
                "public DateTime Stamp() => DateTime.UtcNow;\n\n    public int SpinTwice() => new Widget().Spin(2);",
                StringComparison.Ordinal);
            Assert.NotEqual(original, edited);

            await WriteAndRefreshAsync(path, edited);

            var hits = Root(await Search("SpinTwice"));
            var hit = Assert.Single(
                hits.GetProperty("items").EnumerateArray(),
                i => i.GetProperty("name").GetString()!.Contains("UsingsSample.SpinTwice(", StringComparison.Ordinal));

            // The id search_index handed out resolves, against the refreshed text.
            var id = hit.GetProperty("symbolId").GetString()!;
            var symbol = Root(await ContextTools.GetSymbol(
                _f.Workspace, _f.Locator, _f.Index, _f.Symbols, _f.Builder, _f.Telemetry, id));
            Assert.False(symbol.TryGetProperty("error", out _), symbol.GetRawText());
            Assert.Equal(id, symbol.GetProperty("symbolId").GetString());

            // And the edge cache knows the new call: Widget.Spin gained a caller.
            var callers = Root(await ContextTools.GetReferences(
                _f.Workspace, _f.Locator, _f.Symbols, _f.Telemetry, "Sample.Lib.Widget.Spin", "callers"));
            Assert.Contains(callers.GetProperty("items").EnumerateArray(),
                r => r.GetProperty("displayString").GetString()!.Contains("SpinTwice", StringComparison.Ordinal));

            // Scoped: exactly one new row, nothing else in the store was touched.
            Assert.Equal(symbolsBefore + 1, _f.Symbols.SymbolCount());
        }
        finally
        {
            var result = await WriteAndRefreshAsync(path, original);
            Assert.False(result.FullRebuildQueued, "restoring one member should not need a full rebuild");
        }

        Assert.DoesNotContain(Names(Root(await Search("SpinTwice"))), n => n.Contains("UsingsSample.SpinTwice(", StringComparison.Ordinal));
        Assert.Equal(symbolsBefore, _f.Symbols.SymbolCount());
    }

    [Fact]
    public async Task EditingOnePartOfAPartialType_KeepsBothDeclarationSites()
    {
        var extra = _f.Locator.AbsPath("Lib/Gadget.Extra.cs");
        var main = _f.Locator.AbsPath("Lib/Gadget.cs");
        var original = await File.ReadAllTextAsync(extra);
        try
        {
            await WriteAndRefreshAsync(extra, original.Replace("=> 2;", "=> 22;", StringComparison.Ordinal));

            var inExtra = _f.Symbols.SymbolsDeclaredIn([extra]);
            var inMain = _f.Symbols.SymbolsDeclaredIn([main]);
            var gadget = Root(await ContextTools.GetSymbol(
                _f.Workspace, _f.Locator, _f.Index, _f.Symbols, _f.Builder, _f.Telemetry, "Sample.Lib.Gadget"));
            var gadgetId = gadget.GetProperty("symbolId").GetString()!;

            Assert.Contains(gadgetId, inExtra);
            Assert.Contains(gadgetId, inMain);
            Assert.Contains(Names(Root(await Search("Gadget Right"))), n => n.Contains("Gadget.Right(", StringComparison.Ordinal));
            Assert.Contains(Names(Root(await Search("Gadget Left"))), n => n.Contains("Gadget.Left(", StringComparison.Ordinal));
        }
        finally
        {
            await WriteAndRefreshAsync(extra, original);
        }
    }

    [Fact]
    public async Task RemovingACalledMember_ReindexesItsCallersEdges()
    {
        // TurboWidget.Spin has an implements edge to IWidget.Spin and is referenced from elsewhere in
        // the fixture only through the interface; a member with a direct caller in ANOTHER file is what
        // exercises the referrer expansion, so this test first creates one.
        var caller = _f.Locator.AbsPath("Lib/UsingsSample.cs");
        var callee = _f.Locator.AbsPath("Lib/Gadget.Extra.cs");
        var callerOriginal = await File.ReadAllTextAsync(caller);
        var calleeOriginal = await File.ReadAllTextAsync(callee);
        try
        {
            await WriteAndRefreshAsync(callee, calleeOriginal.Replace("=> 2;", "=> 2;\n    public int Removable() => 9;", StringComparison.Ordinal));
            await WriteAndRefreshAsync(caller, callerOriginal.Replace(
                "public DateTime Stamp() => DateTime.UtcNow;",
                "public DateTime Stamp() => DateTime.UtcNow;\n    public int UsesRemovable() => new Gadget().Removable();",
                StringComparison.Ordinal));

            var removableId = Assert.Single(
                Root(await Search("Removable")).GetProperty("items").EnumerateArray(),
                i => i.GetProperty("name").GetString()!.Contains("Gadget.Removable(", StringComparison.Ordinal))
                .GetProperty("symbolId").GetString()!;
            Assert.NotEmpty(_f.Symbols.Callers(removableId));

            var usesRemovableId = Assert.Single(_f.Symbols.Callers(removableId));

            // Now the callee vanishes while the caller's file is untouched on disk: the caller still
            // names it, so the caller's document must be re-indexed in the same pass — and its dead
            // edge dropped, without a full rebuild.
            var result = await WriteAndRefreshAsync(callee, calleeOriginal);
            Assert.False(result.FullRebuildQueued);
            Assert.Null(_f.Symbols.FqNameFor(removableId));
            Assert.DoesNotContain(removableId, _f.Symbols.CallTargets(usesRemovableId));
            Assert.NotNull(_f.Symbols.FqNameFor(usesRemovableId));
        }
        finally
        {
            await WriteAndRefreshAsync(caller, callerOriginal);
            await WriteAndRefreshAsync(callee, calleeOriginal);
        }
    }

    [Fact]
    public async Task ControlChannel_RefreshCommand_AnswersOnceTheStoreHasTheEdit()
    {
        var server = new ControlServer(
            _f.Locator, _f.Index, _f.Workspace, _f.Builder, _f.Telemetry, NullLogger<ControlServer>.Instance);
        server.Start();
        var portFile = Path.Combine(_f.Locator.CacheDir, "control.port");
        for (var i = 0; i < 100 && !File.Exists(portFile); i++)
            await Task.Delay(50);
        Assert.True(File.Exists(portFile), "control channel did not start");

        var path = _f.Locator.AbsPath("Lib/UsingsSample.cs");
        var original = await File.ReadAllTextAsync(path);
        try
        {
            await File.WriteAllTextAsync(path, original.Replace(
                "public DateTime Stamp() => DateTime.UtcNow;",
                "public DateTime Stamp() => DateTime.UtcNow;\n    public int ViaChannel() => 1;",
                StringComparison.Ordinal));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

            var response = await ControlClient.SendAsync(
                _f.Locator.Root, $"refresh {JsonSerializer.Serialize(new[] { path })}", TimeSpan.FromSeconds(30));

            Assert.NotNull(response);
            Assert.StartsWith("ok:refreshed", response);
            Assert.Contains(Names(Root(await Search("ViaChannel"))), n => n.Contains("UsingsSample.ViaChannel(", StringComparison.Ordinal));

            var again = await ControlClient.SendAsync(
                _f.Locator.Root, $"refresh {JsonSerializer.Serialize(new[] { path })}", TimeSpan.FromSeconds(30));
            Assert.Equal("ok:nothing changed", again);
        }
        finally
        {
            await WriteAndRefreshAsync(path, original);
        }
    }
}

/// <summary>Path extraction for the refresh hook: pure, no channel involved.</summary>
public sealed class CsFileRefreshTests : IClassFixture<HookRepoFixture>
{
    private readonly HookRepoFixture _repo;

    public CsFileRefreshTests(HookRepoFixture repo) => _repo = repo;

    [Theory]
    [InlineData("Edit")]
    [InlineData("Write")]
    [InlineData("NotebookEdit")]
    public void NativeEditTools_ReportTheirFilePath(string tool)
    {
        var (files, rewrite) = CsFileRefresh.WrittenFiles(new HookPayload(tool, _repo.CompiledFile, null), _repo.Context());
        Assert.Equal([_repo.CompiledFile], files);
        Assert.False(rewrite);
    }

    [Fact]
    public void ApplyPatch_ReportsEveryChangeKind()
    {
        var command = "*** Begin Patch\n*** Update File: src/App/Compiled.cs\n@@\n-a\n+b\n*** Delete File: src/App/Old.cs\n*** Add File: src/App/New.cs\n+x\n*** End Patch";
        var (files, rewrite) = CsFileRefresh.WrittenFiles(new HookPayload("apply_patch", null, command), _repo.Context());
        Assert.Equal(["src/App/Compiled.cs", "src/App/Old.cs", "src/App/New.cs"], files);
        Assert.False(rewrite);
    }

    [Fact]
    public void Bash_InPlaceEditAfterCd_ResolvesAgainstTheMovedDirectory()
    {
        var (files, rewrite) = CsFileRefresh.WrittenFiles(
            new HookPayload("Bash", null, "cd src/App && sed -i 's/a/b/' Compiled.cs"), _repo.Context());
        Assert.Equal([_repo.CompiledFile], files);
        Assert.False(rewrite);
    }

    [Theory]
    [InlineData("git pull")]
    [InlineData("git -C . checkout main")]
    [InlineData("git stash pop")]
    public void Bash_TreeRewritingGit_IsAFullRescan(string command)
    {
        var (files, rewrite) = CsFileRefresh.WrittenFiles(new HookPayload("Bash", null, command), _repo.Context());
        Assert.Empty(files);
        Assert.True(rewrite);
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("dotnet build")]
    [InlineData("cat src/App/Compiled.cs")]
    public void Bash_ReadsAndUnrelatedCommands_ReportNothing(string command)
    {
        var (files, rewrite) = CsFileRefresh.WrittenFiles(new HookPayload("Bash", null, command), _repo.Context());
        Assert.Empty(files);
        Assert.False(rewrite);
    }

    [Fact]
    public async Task UnreachableServer_IsSilentForAnExistingFile()
    {
        // No control.port under this repo root, so the channel is unreachable; an edited file that is
        // already indexed needs no note - the sweep converges.
        var context = _repo.Context();
        Directory.CreateDirectory(Path.Combine(context.Root, ".claude", "dotnet-toolkit", "cache"));
        await File.WriteAllTextAsync(
            Path.Combine(context.Root, ".claude", "dotnet-toolkit", "cache", "index.json"),
            """{"Version":0,"Root":"x","Files":{"src/App/Compiled.cs":{}}}""");

        var outcome = await CsFileRefresh.EvaluateAsync(new HookPayload("Edit", _repo.CompiledFile, null), context);
        Assert.Equal(HookOutcome.Allow, outcome);

        var created = await CsFileRefresh.EvaluateAsync(
            new HookPayload("Write", Path.Combine(_repo.Root, "src", "App", "Brand.cs"), null), context);
        Assert.Equal(0, created.ExitCode);
        Assert.Contains("reload_workspace", created.Stdout);
    }
}
