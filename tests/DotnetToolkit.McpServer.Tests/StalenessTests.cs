using System.Reflection;
using System.Text.Json;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Telemetry;
using DotnetToolkit.McpServer.Tools;
using DotnetToolkit.McpServer.Validation;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Conformance C11 without MSBuild: with no solution to load, the workspace never becomes ready, so
/// index-backed responses must carry <c>limitedBy: index_only</c> while live-only tools refuse with
/// <c>workspace_loading</c>.
/// </summary>
public sealed class StalenessTests : IDisposable
{
    private readonly string _root;
    private readonly SolutionLocator _locator;
    private readonly ProjectIndex _index;
    private readonly WorkspaceHost _workspace;
    private readonly KnowledgeStore _store;
    private readonly SymbolStore _symbols;
    private readonly FeatureLogStore _featureLog;
    private readonly SymbolIndexBuilder _builder;
    private readonly TelemetryRecorder _telemetry;

    public StalenessTests()
    {
        // Pinned so JsonDocument.Parse assertions read plain JSON regardless of Formats.Current's
        // process-wide default (toon) — this fixture is constructed directly, not through Program.cs,
        // so the config.json-based seeding path never runs for it.
        Formats.Current = OutputFormat.Compact;
        _root = Directory.CreateTempSubdirectory("staleness-tests-").FullName;
        File.WriteAllText(Path.Combine(_root, "Foo.cs"),
            "namespace Demo;\n\n/// <summary>A demo type.</summary>\npublic class Foo\n{\n    public int Bar() => 1;\n}\n");
        Directory.CreateDirectory(Path.Combine(_root, ".claude", "dotnet-toolkit"));
        File.WriteAllText(Path.Combine(_root, ".claude", "dotnet-toolkit", "config.json"), "{\"defaultFormat\":\"compact\"}");

        _locator = new SolutionLocator(NullLogger<SolutionLocator>.Instance, _root);
        _index = new ProjectIndex(_locator, NullLogger<ProjectIndex>.Instance);
        _index.StartInitialization();
        _workspace = new WorkspaceHost(_locator, _index, NullLogger<WorkspaceHost>.Instance);
        _workspace.StartLoading(); // no solution → resolves to NoSolution, GetSolutionAsync returns null
        _store = new KnowledgeStore(_locator, NullLogger<KnowledgeStore>.Instance);
        _symbols = new SymbolStore(_store);
        _featureLog = new FeatureLogStore(_store);
        _builder = new SymbolIndexBuilder(_workspace, _symbols, NullLogger<SymbolIndexBuilder>.Instance);
        _telemetry = new TelemetryRecorder(_store, NullLogger<TelemetryRecorder>.Instance);
    }

    public void Dispose()
    {
        _workspace.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task GetSymbol_IsIndexBackedWhenWorkspaceUnavailable_C11()
    {
        var root = Root(await ContextTools.GetSymbol(
            _workspace, _locator, _index, _symbols, _featureLog, _builder, _telemetry,
            "Demo.Foo"));

        Assert.Equal("index_only", root.GetProperty("limitedBy").GetString());
        Assert.True(root.TryGetProperty("content", out _));
        Assert.Equal("Type", root.GetProperty("content").GetProperty("kind").GetString());
        Assert.StartsWith("decl:", root.GetProperty("contentVersion").GetString());
    }

    [Fact]
    public async Task GetReferences_IsLiveOnlyAndRefuses_C11()
    {
        var root = Root(await ContextTools.GetReferences(
            _workspace, _locator, _symbols, _telemetry, "Demo.Foo", "callers"));

        Assert.Equal("workspace_loading", root.GetProperty("error").GetString());
    }

    // Conformance C8: applyOnSuccess without an intent is rejected before any validation runs.
    [Fact]
    public async Task ValidatePatch_ApplyWithoutIntent_Rejected_C8()
    {
        var edits = new[] { new PatchEditInput("Foo.cs", 4, 6, "public class Foo { }") };
        var root = Root(await PatchTools.ValidatePatch(
            _workspace, _locator, _symbols, _featureLog, _builder,
            new TargetedTests(_locator, NullLogger<TargetedTests>.Instance), _telemetry,
            new Dictionary<string, string>(), edits,
            requestedLevel: null, applyOnSuccess: true, intent: null, tags: null));

        Assert.Equal("intent_required", root.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RunExclusiveApplyAsync_SerializesConcurrentCalls()
    {
        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var secondEntered = false;

        var first = _workspace.RunExclusiveApplyAsync(async () =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return 1;
        });
        await firstEntered.Task;

        var second = _workspace.RunExclusiveApplyAsync(() =>
        {
            secondEntered = true;
            return Task.FromResult(2);
        });

        // The gate must still be held by `first` here -- give `second` a real chance to have run if it
        // wrongly could, then confirm it didn't.
        await Task.Delay(50);
        Assert.False(secondEntered);

        releaseFirst.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.True(secondEntered);
        Assert.Equal(1, results[0]);
        Assert.Equal(2, results[1]);
    }

    [Fact]
    public async Task RunExclusiveApplyAsync_ReleasesGateWhenActionThrows()
    {
        Task<int> Throwing() => throw new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _workspace.RunExclusiveApplyAsync<int>(Throwing));

        // If the semaphore weren't released in a finally, this would hang until the test times out.
        var result = await _workspace.RunExclusiveApplyAsync(() => Task.FromResult(42));
        Assert.Equal(42, result);
    }

    // OnProjectFilesChanged is private, invoked only via ProjectIndex's ProjectFilesChanged event; the
    // race it guards against (a project-file change during the initial load spawning a second concurrent
    // LoadAsync) needs real MSBuild timing to reproduce end-to-end, which is exactly what this fixture
    // avoids. Reflection exercises the handler directly against known State values instead, asserting on
    // the private _pendingProjectReload flag and on _loadTask's identity to prove whether TriggerReload
    // actually ran -- a deliberate white-box test of wiring with no other seam to reach it through.
    private static void InvokeOnProjectFilesChanged(WorkspaceHost workspace) =>
        typeof(WorkspaceHost).GetMethod("OnProjectFilesChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(workspace, null);

    private static bool PendingProjectReload(WorkspaceHost workspace) =>
        (bool)typeof(WorkspaceHost).GetField("_pendingProjectReload", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(workspace)!;

    private static Task LoadTask(WorkspaceHost workspace) =>
        (Task)typeof(WorkspaceHost).GetField("_loadTask", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(workspace)!;

    [Fact]
    public void OnProjectFilesChanged_DefersReloadWhileNotLoaded()
    {
        // Fresh WorkspaceHost, StartLoading() never called: State is still the default NotStarted.
        var workspace = new WorkspaceHost(_locator, _index, NullLogger<WorkspaceHost>.Instance);
        var loadTaskBefore = LoadTask(workspace);

        InvokeOnProjectFilesChanged(workspace);

        Assert.True(PendingProjectReload(workspace));
        // TriggerReload was NOT invoked -- _loadTask is untouched, still the original Task.CompletedTask.
        Assert.Same(loadTaskBefore, LoadTask(workspace));
        workspace.Dispose();
    }

    [Fact]
    public void OnProjectFilesChanged_TriggersReloadWhileLoaded()
    {
        var workspace = new WorkspaceHost(_locator, _index, NullLogger<WorkspaceHost>.Instance);
        typeof(WorkspaceHost).GetProperty(nameof(WorkspaceHost.State))!.SetValue(workspace, WorkspaceState.Loaded);
        var loadTaskBefore = LoadTask(workspace);

        InvokeOnProjectFilesChanged(workspace);

        Assert.False(PendingProjectReload(workspace));
        // TriggerReload reassigns _loadTask to a new Task.Run(...) -- a different instance proves it ran.
        Assert.NotSame(loadTaskBefore, LoadTask(workspace));
        workspace.Dispose();
    }
}
