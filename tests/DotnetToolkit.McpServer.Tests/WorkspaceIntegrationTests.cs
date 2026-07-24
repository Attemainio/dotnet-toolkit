using System.Diagnostics;
using System.Text.Json;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Telemetry;
using DotnetToolkit.McpServer.Validation;
using DotnetToolkit.McpServer.Tools;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Loads the SampleSolution fixture once (restore + MSBuildWorkspace + SQLite symbol index) and shares
/// it across every test in the class via <see cref="IClassFixture{T}"/>. The load is the 15–60 s
/// "workspace ready" tier and is especially slow on WSL /mnt drives, so running it per test method would
/// multiply the cost by the method count.
/// </summary>
public sealed class SampleSolutionFixture : IAsyncLifetime
{
    public SolutionLocator Locator { get; private set; } = null!;
    public ProjectIndex Index { get; private set; } = null!;
    public WorkspaceHost Workspace { get; private set; } = null!;
    public SymbolStore Symbols { get; private set; } = null!;
    public FeatureLogStore FeatureLog { get; private set; } = null!;
    public SymbolIndexBuilder Builder { get; private set; } = null!;
    public TargetedTests TargetedTests { get; private set; } = null!;
    public CallSlice CallSlice { get; private set; } = null!;
    public TelemetryRecorder Telemetry { get; private set; } = null!;

    private KnowledgeStore _store = null!;
    private string _workDir = "";

    public async Task InitializeAsync()
    {
        if (!Microsoft.Build.Locator.MSBuildLocator.IsRegistered)
            Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();

        // Pinned so every JsonDocument.Parse assertion in this class reads plain JSON regardless of
        // Formats.Current's process-wide default (toon) — this fixture is constructed directly, not
        // through Program.cs, so the config.json-based seeding path never runs for it.
        Formats.Current = OutputFormat.Compact;

        // Copy the fixture to a throwaway temp dir (native /tmp on WSL — faster than /mnt, and
        // isolated so validate_patch's disk writes never pollute the repo/bin fixture).
        var source = Path.Combine(AppContext.BaseDirectory, "fixtures", "SampleSolution");
        _workDir = Path.Combine(Path.GetTempPath(), "dt-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        CopyDirectory(source, _workDir);
        await RunDotnet("restore Sample.slnx", _workDir);

        Locator = new SolutionLocator(NullLogger<SolutionLocator>.Instance, _workDir);
        Index = new ProjectIndex(Locator, NullLogger<ProjectIndex>.Instance);
        Index.StartInitialization();
        Workspace = new WorkspaceHost(Locator, Index, NullLogger<WorkspaceHost>.Instance);
        Workspace.StartLoading();

        var solution = await Workspace.GetSolutionAsync(TimeSpan.FromMinutes(3));
        Assert.NotNull(solution);

        _store = new KnowledgeStore(Locator, NullLogger<KnowledgeStore>.Instance);
        Symbols = new SymbolStore(_store);
        FeatureLog = new FeatureLogStore(_store);
        Builder = new SymbolIndexBuilder(Workspace, Symbols, NullLogger<SymbolIndexBuilder>.Instance);
        await Builder.RebuildAsync();
        Assert.True(Builder.Ready);
        Telemetry = new TelemetryRecorder(_store, NullLogger<TelemetryRecorder>.Instance);
        TargetedTests = new TargetedTests(Locator, NullLogger<TargetedTests>.Instance);
        CallSlice = new CallSlice(Symbols);
    }

    public Task DisposeAsync()
    {
        Workspace.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            // Skip build output — checked on the path RELATIVE to source, so an ancestor "bin"
            // (the fixture lives under the test's own bin dir) does not exclude everything.
            var rel = Path.GetRelativePath(source, file);
            var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s is "bin" or "obj"))
                continue;
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static async Task RunDotnet(string args, string workingDir)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var psi = new ProcessStartInfo(dotnet, args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // `dotnet restore` spawns PERSISTENT MSBuild worker nodes that inherit these redirected pipes and
        // outlive the parent. ReadToEndAsync waits for EOF, which such a node never delivers — so the read
        // blocks forever even though restore itself exited. Disabling node reuse (and the build server)
        // keeps every child short-lived so the pipes actually close.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using var process = Process.Start(psi)!;

        // Drain BOTH pipes concurrently: reading one to completion first deadlocks as soon as the child
        // fills the other pipe's buffer.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            // Bound the drain too: a stray pipe holder must fail loudly, never hang the suite.
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            Assert.Fail($"dotnet {args} did not complete within the timeout (likely a pipe held open by a build server)");
        }

        Assert.True(process.ExitCode == 0, $"dotnet {args} failed:\n{await stdoutTask}\n{await stderrTask}");
    }
}

/// <summary>
/// End-to-end tests of the v2 read surface (get_symbol, get_references, search_index) against the shared
/// SampleSolution workspace. Requires the .NET SDK.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WorkspaceIntegrationTests : IClassFixture<SampleSolutionFixture>
{
    private readonly SampleSolutionFixture _f;

    public WorkspaceIntegrationTests(SampleSolutionFixture fixture) => _f = fixture;

    private Task<string> GetSymbol(
        string symbol, string? include = null, string? knownVersion = null, bool refetch = false) =>
        ContextTools.GetSymbol(_f.Workspace, _f.Locator, _f.Index, _f.Symbols, _f.FeatureLog, _f.Builder, _f.Telemetry,
            symbol, include, knownVersion, refetch);

    private Task<string> GetSymbols(string[] symbols, string? include = null) =>
        ContextTools.GetSymbol(_f.Workspace, _f.Locator, _f.Index, _f.Symbols, _f.FeatureLog, _f.Builder, _f.Telemetry,
            symbol: null, include, knownVersion: null, refetch: false, symbols: symbols);

    private Task<string> GetReferences(string symbol, string direction) =>
        ContextTools.GetReferences(_f.Workspace, _f.Locator, _f.Symbols, _f.Telemetry, symbol, direction);

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>
    /// Reads a plain JSON array of objects into the per-row lookup the rest of these tests were already
    /// written against.
    /// </summary>
    private static List<Dictionary<string, JsonElement>> TableRows(JsonElement items) =>
        items.EnumerateArray()
            .Select(item => item.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal))
            .ToList();

    /// <summary>Identity pass-through, kept so call sites written against the old hoisted-"rest" shape
    /// (ctx-contract/3.6, since removed) don't all need editing — there is no more rest object to merge.</summary>
    private static Dictionary<string, JsonElement> MergedRow(Dictionary<string, JsonElement> row) => row;

    /// <summary>
    /// Retrieval must work for a caller that supplies no session/task ids. Attribution is
    /// instrumentation and must never gate the tool it measures: when these were required, an agent
    /// that had not read the retrieval skill saw two mandatory ids it could not produce and fell
    /// back to grep — so the requirement meant to produce telemetry produced none at all.
    /// </summary>
    [Fact]
    public async Task Retrieval_WorksWithoutSessionOrTaskIds()
    {
        var json = await ContextTools.GetSymbol(
            _f.Workspace, _f.Locator, _f.Index, _f.Symbols, _f.FeatureLog, _f.Builder, _f.Telemetry,
            "Lib.TurboWidget");

        var root = Root(json);
        Assert.False(root.TryGetProperty("error", out _));
        Assert.True(root.TryGetProperty("contentVersion", out _));
    }

    /// <summary>
    /// A project count is not actionable when one project of a solution fails to load: the caller
    /// cannot tell which results are degraded. Status must name the projects it actually loaded.
    /// </summary>
    [Fact]
    public void WorkspaceStatus_NamesLoadedProjects()
    {
        var status = ServerTools.WorkspaceStatus(_f.Locator, _f.Index, _f.Workspace);

        Assert.Contains("loaded:", status);
        Assert.Contains("Lib", status);
    }

    /// <summary>
    /// A multi-word query must return the union of what its terms name. Observed on a real repo:
    /// the substring matcher forced 19 separate single-word search_index calls for one question,
    /// because any query with a space in it matched nothing at all.
    /// </summary>
    [Fact]
    public async Task SearchIndex_MultiWordQuery_FindsSymbolsForEachTerm()
    {
        var root = Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Widget Gadget", groupBy: "none"));

        var names = TableRows(root.GetProperty("items"))
            .Select(i => i["name"].GetString()!).ToList();

        Assert.Contains(names, n => n.Contains("Widget", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("Gadget", StringComparison.Ordinal));
    }

    /// <summary>
    /// The contract search_index's name field has to keep: whatever it emits must feed straight back
    /// into get_symbol. Shortening parameter types is only safe because the resolver strips those same
    /// prefixes before matching — this test is what proves the two stayed in step.
    /// </summary>
    [Fact]
    public async Task SearchIndex_EmittedNameResolvesBackToTheSameSymbol()
    {
        var hit = TableRows(Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "SpinTwice", groupBy: "none")).GetProperty("items")).First();
        var name = hit["name"].GetString()!;

        // Fully qualified up to the member, but the parameter's namespace is gone.
        Assert.StartsWith("Sample.Lib.WidgetExtensions.SpinTwice(", name);
        Assert.DoesNotContain("Sample.Lib.IWidget", name);
        Assert.Contains("IWidget", name);

        var resolved = Root(await GetSymbol(name));

        Assert.False(resolved.TryGetProperty("error", out _));
        Assert.Equal(hit["symbolId"].GetString(), resolved.GetProperty("symbolId").GetString());
    }

    /// <summary>
    /// An explicit include list is an exact query of the columns wanted — everything known about a
    /// symbol except the expensive part, spelled out directly, rather than reaching for "all" and
    /// dragging the whole body along.
    /// </summary>
    [Fact]
    public async Task GetSymbol_ExplicitIncludeListIsExactlyTheNamedComponents()
    {
        var full = Root(await GetSymbol("Sample.Lib.Widget.Spin", "all"));
        Assert.True(full.GetProperty("content").GetProperty("source").GetArrayLength() > 0);

        var trimmed = Root(await GetSymbol("Sample.Lib.Widget.Spin", "xmlDoc,mechanicalFacts,referenceCounts,recentLog"));
        var content = trimmed.GetProperty("content");

        // Absent entirely, not present-and-null: an unrequested component costs no tokens at all.
        Assert.False(content.TryGetProperty("source", out _));
        // ...while everything named in the list is present.
        Assert.True(content.TryGetProperty("referenceCounts", out _));
        Assert.Equal("Method", content.GetProperty("kind").GetString());
        // The resolved set is echoed only when the caller passed a non-default include.
        Assert.DoesNotContain("source", trimmed.GetProperty("components").EnumerateArray()
            .Select(c => c.GetString()));
    }

    /// <summary>
    /// symbols batches several fetches under one resolution into one call. Each result must be exactly
    /// what a single-symbol call for that same symbol would return — batching is an orchestration
    /// convenience, not a different code path with its own behaviour to drift from the single-symbol one.
    /// There is no more field hoisting (CompactTable/JsonHoist, removed): every result is its own
    /// complete, independent envelope, exactly the shape a single get_symbol call for that symbol
    /// produces — including keys like error being ABSENT (not present-and-null) on a successful result.
    /// </summary>
    [Fact]
    public async Task GetSymbol_SymbolsBatchesMultipleFetchesInOneCall()
    {
        var batch = Root(await GetSymbols(["Sample.Lib.Widget", "Sample.Lib.IWidget"]));
        var rows = TableRows(batch.GetProperty("results"));
        Assert.Equal(2, rows.Count);

        var widgetAlone = Root(await GetSymbol("Sample.Lib.Widget"));
        var iwidgetAlone = Root(await GetSymbol("Sample.Lib.IWidget"));

        Assert.Equal(widgetAlone.GetProperty("symbolId").GetString(), rows[0]["symbolId"].GetString());
        Assert.Equal(widgetAlone.GetProperty("content").GetProperty("kind").GetString(),
            rows[0]["content"].GetProperty("kind").GetString());
        Assert.Equal(iwidgetAlone.GetProperty("symbolId").GetString(), rows[1]["symbolId"].GetString());
        Assert.Equal("Interface", rows[1]["content"].GetProperty("kind").GetString());
        // Absent entirely on a successful result, not present-and-null.
        Assert.False(rows[0].ContainsKey("error"));

        // xmlDoc is present exactly where a single-symbol call would put it: Widget has one, IWidget does not.
        Assert.Equal("A spinning widget.", rows[0]["content"].GetProperty("xmlDoc").GetProperty("summary").GetString());
        Assert.False(rows[1]["content"].TryGetProperty("xmlDoc", out _));
    }

    /// <summary>
    /// A batch entry that fails to resolve has no symbolId/contentVersion/content to offer — its result is
    /// simply the error envelope ResolveAsync would have produced, exactly like an unresolved
    /// single-symbol call, not a row shaped to match its neighbours' columns (there are no columns).
    /// </summary>
    [Fact]
    public async Task GetSymbol_SymbolsBatchCarriesAPerRowErrorForAnUnresolvedEntry()
    {
        var batch = Root(await GetSymbols(["Sample.Lib.Widget", "Sample.Lib.NoSuchSymbolAtAll"]));
        var rows = TableRows(batch.GetProperty("results"));
        Assert.Equal(2, rows.Count);

        Assert.False(rows[0].ContainsKey("error"));

        Assert.False(rows[1].ContainsKey("symbolId"));
        Assert.False(rows[1].ContainsKey("contentVersion"));
        Assert.Equal("symbol_not_found", rows[1]["error"].GetString());
    }

    [Fact]
    public async Task GetSymbol_MissingBothSymbolAndSymbolsIsAnError()
    {
        var result = Root(await ContextTools.GetSymbol(_f.Workspace, _f.Locator, _f.Index, _f.Symbols, _f.FeatureLog,
            _f.Builder, _f.Telemetry, symbol: null));

        Assert.Equal("missing_symbol", result.GetProperty("error").GetString());
    }

    /// <summary>
    /// An explicit include list REPLACES the default set rather than adding to it: it is a literal query
    /// of exactly the columns wanted, so include:"members" alone drops the standard xmlDoc/referenceCounts/
    /// recentLog that a plain call would carry.
    /// </summary>
    [Fact]
    public async Task GetSymbol_IncludeReplacesTheDefaultSetWithExactlyWhatWasAsked()
    {
        var plain = Root(await GetSymbol("Sample.Lib.Widget"));
        Assert.False(plain.GetProperty("content").TryGetProperty("members", out _));
        Assert.True(plain.GetProperty("content").TryGetProperty("xmlDoc", out _));

        var withMembers = Root(await GetSymbol("Sample.Lib.Widget", include: "members"));
        var members = withMembers.GetProperty("content").GetProperty("members");

        Assert.NotEmpty(members.EnumerateArray());
        // The standard default is gone: an explicit list is exactly what was asked for, nothing implied.
        Assert.False(withMembers.GetProperty("content").TryGetProperty("xmlDoc", out _));
        Assert.False(withMembers.GetProperty("content").TryGetProperty("referenceCounts", out _));
        Assert.False(withMembers.GetProperty("content").TryGetProperty("source", out _));
    }

    /// <summary>
    /// A misspelled component fails loudly. Ignoring it would leave the caller believing it dropped a
    /// field it is in fact still paying for — the failure mode is silent and costs tokens every call.
    /// </summary>
    [Fact]
    public async Task GetSymbol_UnknownComponentIsRejectedRatherThanIgnored()
    {
        var root = Root(await GetSymbol("Sample.Lib.Widget.Spin", include: "sourceCode"));

        Assert.Equal("invalid_component", root.GetProperty("error").GetString());
        Assert.Contains("sourceCode", root.GetProperty("detail").GetString());
        Assert.Contains("source", root.GetProperty("detail").GetString());
    }

/// <summary>
    /// An outline-equivalent include list used to be built by an early return from its own object
    /// literal, which silently omitted containingType and recentLog. One build path means a component
    /// appears whenever it is asked for, regardless of which other components were requested alongside it.
    /// </summary>
    [Fact]
    public async Task GetSymbol_MembersRequestCarriesTheSameSkeletonAsEveryOtherRequest()
    {
        var outline = Root(await GetSymbol("Sample.Lib.Widget", "xmlDoc,referenceCounts,recentLog,members"));
        var content = outline.GetProperty("content");

        Assert.NotEmpty(content.GetProperty("members").EnumerateArray());
        Assert.True(content.TryGetProperty("declarationSites", out _));
        Assert.Equal("Type", content.GetProperty("kind").GetString());
        // modifiers is unconditional, like the skeleton — present here even though it wasn't named.
        Assert.True(content.TryGetProperty("modifiers", out _));
    }

    /// <summary>
    /// A test caller is identified by the attribute on its own declaration, not by living in a test
    /// project. The previous project-level check read Project.MetadataReferences, so it depended on how
    /// completely MSBuild had loaded that project — and nothing could ever recompute it, because the
    /// incremental indexer only rewrites rows whose CONTENT moved. On this repo that left 53 of 113
    /// calling members permanently unattributed while a clean index of the same source attributed all
    /// of them, and the resulting tests:0 is what the validation ladder reads to decide escalation.
    /// </summary>
    [Fact]
    public async Task GetReferences_MarksTestCallersFromTheirOwnAttribute()
    {
        var root = Root(await GetReferences("Sample.Lib.Widget.Spin", "callers"));
        var items = TableRows(root.GetProperty("items")).Select(MergedRow).ToList();

        Assert.NotEmpty(items);
        // isTest is emitted only when true, so absence is the "not a test" signal.
        foreach (var item in items)
        {
            var isTest = item.TryGetValue("isTest", out var flag) && flag.GetBoolean();
            var name = item["displayString"].GetString()!;
            // Nothing in the sample solution is a test, so no caller may claim to be one.
            Assert.False(isTest, $"{name} was marked as a test");
        }
    }

    /// <summary>
    /// tests is now a subset of callers computed from the caller's own flag, so the two cannot
    /// disagree — previously they were separate edge sets written on the same pass and could.
    /// </summary>
    [Fact]
    public async Task ReferenceCounts_TestsNeverExceedCallers()
    {
        var content = Root(await GetSymbol("Sample.Lib.Widget.Spin")).GetProperty("content");
        var counts = content.GetProperty("referenceCounts");

        if (counts.TryGetProperty("callers", out var callers) && callers.ValueKind == JsonValueKind.Number
            && counts.TryGetProperty("tests", out var tests) && tests.ValueKind == JsonValueKind.Number)
        {
            Assert.True(tests.GetInt32() <= callers.GetInt32(),
                $"tests={tests.GetInt32()} exceeded callers={callers.GetInt32()}");
        }
    }

    /// <summary>
    /// Counts must be omitted, not reported as 0, for a project the edge cache never covered.
    /// A project that fails to load in MSBuild yields no edges, and reporting that absence as
    /// "0 callers" states something the store cannot know — observed live on a method with 5.
    /// </summary>
    [Fact]
    public async Task ReferenceCounts_OmittedWhenProjectHasNoEdgeCoverage()
    {
        // A symbol id from no indexed project at all: coverage cannot be established for it.
        Assert.False(_f.Symbols.HasEdgeCoverageFor("sym_not_a_real_symbol"));

        // The fixture's own project does have edges, so real symbols stay measurable.
        var root = Root(await ContextTools.SearchIndex(_f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Spin", kinds: "Method", groupBy: "none"));
        var id = TableRows(root.GetProperty("items")).First()["symbolId"].GetString()!;
        Assert.True(_f.Symbols.HasEdgeCoverageFor(id));
    }

    [Fact]
    public async Task GetSymbol_Full_CarriesVersionAndReferenceCounts()
    {
        var root = Root(await GetSymbol("Sample.Lib.IWidget", "all"));

        // "changed" is omitted when content is present — its presence is the signal.
        Assert.False(root.TryGetProperty("changed", out _));
        Assert.StartsWith("decl:", root.GetProperty("contentVersion").GetString());
        Assert.Equal("Interface", root.GetProperty("content").GetProperty("kind").GetString());
        // IWidget is implemented by Widget and TurboWidget.
        Assert.Equal(2, root.GetProperty("content").GetProperty("referenceCounts").GetProperty("implementations").GetInt32());
    }

    // A sym_... id handed out by any response is itself a valid retrieval target, so
    // suggestedInspection / search hits / reference items round-trip without name guessing.
    [Fact]
    public async Task GetSymbol_AcceptsSymbolIdHandle()
    {
        var byName = Root(await GetSymbol("Sample.Lib.Widget"));
        var symbolId = byName.GetProperty("symbolId").GetString()!;

        var byId = Root(await GetSymbol(symbolId));

        Assert.Equal(symbolId, byId.GetProperty("symbolId").GetString());
        Assert.Equal(byName.GetProperty("contentVersion").GetString(), byId.GetProperty("contentVersion").GetString());
    }

    [Fact]
    public async Task SearchIndex_ReturnsResolvableNames_AndAcceptsClassAlias()
    {
        var root = Root(await ContextTools.SearchIndex(_f.Symbols, _f.Index, _f.Workspace, _f.Telemetry,
            "Widget", kinds: "class", limit: 10, groupBy: "none"));

        var items = TableRows(root.GetProperty("items"));
        Assert.NotEmpty(items); // "class" must alias to the stored "Type" kind, case-insensitively

        // The returned name is directly usable as a get_symbol target (no global:: prefix).
        var name = items[0]["name"].GetString()!;
        Assert.DoesNotContain("global::", name);
        var fetched = Root(await GetSymbol(name));
        Assert.True(fetched.TryGetProperty("content", out _));
    }

    // referenceCounts gates expansion (P1.4: "0 callers -> no get_references"), so a false zero makes
    // the agent skip an expansion it needs. The count must agree with get_references — including calls
    // made from top-level statements, which are not ordinary member declarations.
    [Fact]
    public async Task ReferenceCounts_AgreeWithGetReferences_IncludingTopLevelCallers()
    {
        var sym = Root(await GetSymbol("Sample.Lib.Widget.Spin", "all"));
        var callers = sym.GetProperty("content").GetProperty("referenceCounts").GetProperty("callers").GetInt32();
        var refs = Root(await GetReferences("Sample.Lib.Widget.Spin", "callers"));

        // Program.cs calls widget.Spin(3) from top-level statements.
        Assert.True(callers >= 1, $"expected at least one caller, got {callers}");
        Assert.Equal(refs.GetProperty("totalItems").GetInt32(), callers);
    }

    // Fingerprint gating: re-running the builder over unchanged source must rewrite nothing. If this
    // regresses, every index refresh silently becomes a full rebuild again.
    [Fact]
    public async Task IndexRebuild_OverUnchangedSource_WritesNothing()
    {
        await _f.Builder.RebuildAsync();          // ensure the index reflects current source
        var before = _f.Symbols.SymbolCount();

        // A second pass with no source change: everything should compare equal and be skipped.
        await _f.Builder.RebuildAsync();

        Assert.Equal(before, _f.Symbols.SymbolCount());
        Assert.True(before > 0, "fixture should have indexed symbols");
    }

    // get_call_slice: a multi-hop path (Start -> Middle -> Deep -> Widget.Spin) must be found without
    // the caller walking the graph via repeated get_references calls.
    [Fact]
    public async Task GetCallSlice_FindsMultiHopPath()
    {
        var root = Root(await ContextToolsCallSlice("Sample.Lib.Pipeline.Start", "Sample.Lib.Widget.Spin"));

        Assert.True(root.GetProperty("found").GetBoolean());
        var path = root.GetProperty("path").EnumerateArray()
            .Select(n => n.GetProperty("displayString").GetString() ?? "").ToList();
        Assert.True(path.Count >= 2, $"expected a multi-node path, got: {string.Join(" -> ", path)}");
        Assert.Contains(path, p => p.Contains("Start"));
        Assert.Contains(path, p => p.Contains("Spin"));
    }

    // An unreachable pair still reports where each side ran out, rather than a bare "not found".
    [Fact]
    public async Task GetCallSlice_UnreachablePair_ReportsFrontier()
    {
        var root = Root(await ContextToolsCallSlice("Sample.Lib.Widget.Spin", "Sample.Lib.Pipeline.Start"));

        Assert.False(root.GetProperty("found").GetBoolean());
        Assert.True(root.TryGetProperty("forwardFrontier", out _));
    }

    // get_scope must surface an EXTENSION method on the receiver — the case grep structurally cannot
    // answer, since the extension shares no text with the call site.
    [Fact]
    public async Task GetScope_SurfacesExtensionMethodsOnReceiver()
    {
        // Inside Pipeline.Deep, on the line that calls _widget.Spin(turns).
        var sym = Root(await GetSymbol("Sample.Lib.Pipeline.Deep"));
        var site = sym.GetProperty("content").GetProperty("declarationSites")[0];
        var line = site.GetProperty("startLine").GetInt32();

        var root = Root(await FlowTools.GetScope(_f.Workspace, _f.Locator,
            file: "Lib/Pipeline.cs", line: line, column: 40, receiver: "_widget", filter: "methods"));

        var items = root.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("displayString").GetString() ?? "").ToList();
        Assert.Contains(items, i => i.Contains("Spin"));
        Assert.Contains(items, i => i.Contains("SpinTwice"));  // the extension method
    }

    private Task<string> ContextToolsCallSlice(string from, string to) =>
        FlowTools.GetCallSlice(_f.Workspace, _f.Symbols, _f.CallSlice, _f.Builder, from, to);

    // Call edges are recorded against members, never types, so a type reporting "callers: 0" would
    // assert "nothing uses this" when it simply is not measured at that level. Types omit the field;
    // members still report it.
    [Fact]
    public async Task ReferenceCounts_OmitsCallersForTypes_ButReportsThemForMembers()
    {
        var type = Root(await GetSymbol("Sample.Lib.Widget"));
        var typeCounts = type.GetProperty("content").GetProperty("referenceCounts");
        Assert.False(typeCounts.TryGetProperty("callers", out _), "a type must not claim a caller count");
        Assert.True(typeCounts.TryGetProperty("implementations", out _), "implementations is meaningful for a type");

        var member = Root(await GetSymbol("Sample.Lib.Widget.Spin"));
        var memberCounts = member.GetProperty("content").GetProperty("referenceCounts");
        Assert.True(memberCounts.GetProperty("callers").GetInt32() >= 1);
    }

    // Internal helper properties must not ride along in the wire payload.
    [Fact]
    public async Task MechanicalFacts_DoNotLeakInternalProperties()
    {
        var root = Root(await GetSymbol("Sample.Lib.Pipeline.Deep", "all"));
        if (root.GetProperty("content").TryGetProperty("mechanicalFacts", out var facts)
            && facts.ValueKind == JsonValueKind.Object)
        {
            Assert.False(facts.TryGetProperty("IsEmpty", out _), "IsEmpty is an internal guard, not a fact");
        }
    }

    // Conformance C10: one partial-class part returns the unified type with all declaration sites.
// Conformance C10: one partial-class part returns the unified type with all declaration sites.
    [Fact]
    public async Task GetSymbol_UnifiesPartialClass_C10()
    {
        var root = Root(await GetSymbol("Sample.Lib.Gadget"));
        var sites = root.GetProperty("content").GetProperty("declarationSites");
        Assert.Equal(2, sites.GetArrayLength());
    }

/// <summary>
    /// Widget.Spin has a /// doc comment on the line directly above its signature. declarationSites and
    /// source must both start AT the comment, not at the signature — otherwise a validate_patch edit
    /// built from declarationSites' own line span has no way to touch the comment at all.
    /// </summary>
    [Fact]
    public async Task GetSymbol_DeclarationSpanIncludesTheLeadingDocComment()
    {
        var root = Root(await GetSymbol("Sample.Lib.Widget.Spin", "all"));
        var content = root.GetProperty("content");
        var site = content.GetProperty("declarationSites")[0];

        var startLine = site.GetProperty("startLine").GetInt32();
        var fileLines = await File.ReadAllLinesAsync(_f.Locator.AbsPath(site.GetProperty("file").GetString()!));
        Assert.Contains("///", fileLines[startLine - 1]);

        // source reads exactly as the file does, no header line prepended — the doc comment is the
        // first line.
        var sourceLines = content.GetProperty("source");
        Assert.Contains("/// <summary>", sourceLines[0].GetProperty("text").GetString());
        Assert.Equal(startLine, sourceLines[0].GetProperty("line").GetInt32());
    }

    // Conformance C4: a matching knownVersion yields changed:false with heldVersion + refetchHint.
    [Fact]
    public async Task GetSymbol_LeaseHit_OmitsContent_C4()
    {
        var first = Root(await GetSymbol("Sample.Lib.Widget"));
        var version = first.GetProperty("contentVersion").GetString();

        var second = Root(await GetSymbol("Sample.Lib.Widget", knownVersion: version));
        Assert.False(second.GetProperty("changed").GetBoolean());
        Assert.Equal(version, second.GetProperty("heldVersion").GetString());
        Assert.False(string.IsNullOrEmpty(second.GetProperty("refetchHint").GetString()));
        // Content is omitted on a lease hit (null properties are not serialized).
        Assert.False(second.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null);

        // refetch:true overrides the lease and re-sends content.
        var forced = Root(await GetSymbol("Sample.Lib.Widget", knownVersion: version, refetch: true));
        Assert.True(forced.TryGetProperty("content", out _));
        Assert.False(forced.TryGetProperty("changed", out _));
    }

    // A lease may cover a SUBSET of layers: holding only decl (e.g. from a get_references item that
    // never carried a body) still yields changed:false. Here heldVersion and contentVersion genuinely
    // differ — heldVersion states which layers were actually verified, which is why it is not redundant.
    [Fact]
    public async Task GetSymbol_PartialLayerLease_ReportsWhichLayersMatched()
    {
        // A component set derived from decl alone gets a decl-only token back — the response must not
        // claim layers whose content it never sent.
        const string DeclOnlySet = "xmlDoc";
        var first = Root(await GetSymbol("Sample.Lib.Widget.Spin", DeclOnlySet));
        var declOnly = first.GetProperty("contentVersion").GetString()!;
        Assert.DoesNotContain("|", declOnly);
        Assert.StartsWith("decl:", declOnly);

        // Holding exactly what that response covered leases cleanly.
        var leased = Root(await GetSymbol(
            "Sample.Lib.Widget.Spin", DeclOnlySet, knownVersion: declOnly));

        Assert.False(leased.GetProperty("changed").GetBoolean());
        Assert.Equal(declOnly, leased.GetProperty("heldVersion").GetString());
    }

    /// <summary>
    /// Escalating resolution while holding a narrower token must return content, not a lease hit.
    /// <see cref="ContentVersion.Satisfies"/> compares only the layers the caller supplied, so a token
    /// from a signature fetch matches on decl and would report changed:false against a request for the
    /// source — handing back nothing, indistinguishable to the caller from an unchanged symbol. The
    /// lease therefore also requires the held token to COVER the layers the requested components need.
    /// </summary>
    [Fact]
    public async Task GetSymbol_EscalatingResolution_DoesNotFalselyLeaseAwayNewContent()
    {
        var signature = Root(await GetSymbol("Sample.Lib.Widget.Spin"));
        var signatureToken = signature.GetProperty("contentVersion").GetString()!;
        Assert.False(signature.GetProperty("content").TryGetProperty("source", out _));

        var full = Root(await GetSymbol("Sample.Lib.Widget.Spin", "all", knownVersion: signatureToken));

        Assert.False(full.TryGetProperty("changed", out _));
        Assert.True(
            full.GetProperty("content").GetProperty("source").GetArrayLength() > 0);
    }

    [Fact]
    public async Task GetReferences_Implementations_FindsBothWidgets()
    {
        var root = Root(await GetReferences("Sample.Lib.IWidget", "implementations"));
        var displays = TableRows(root.GetProperty("items")).Select(MergedRow)
            .Select(i => i["displayString"].GetString() ?? "").ToList();
        Assert.Contains(displays, d => d.Contains("Widget"));
        Assert.Contains(displays, d => d.Contains("TurboWidget"));
    }

    [Fact]
    public async Task GetReferences_Overrides_FindsHighGear()
    {
        var root = Root(await GetReferences("Sample.Lib.GearBase.Ratio", "overrides"));
        var displays = TableRows(root.GetProperty("items")).Select(MergedRow)
            .Select(i => i["displayString"].GetString() ?? "").ToList();
        Assert.Contains(displays, d => d.Contains("HighGear"));
    }

    // Conformance C7: comment/string matches are excluded from items, counted in excludedKinds.
    [Fact]
    public async Task GetReferences_ExcludesCommentAndStringMatches_C7()
    {
        var root = Root(await GetReferences("Sample.Lib.Widget.Spin", "callers"));

        // Program.cs mentions "Spin" once in a comment and once in a string literal.
        Assert.Equal(2, root.GetProperty("excludedTextMatches").GetInt32());

        var items = TableRows(root.GetProperty("items")).Select(MergedRow).ToList();

        // The only returned item is the real call site; no item points at the comment/string.
        foreach (var item in items)
        {
            foreach (var site in item["sites"].EnumerateArray())
            {
                var snippet = site.GetProperty("snippet").GetString() ?? "";
                Assert.DoesNotContain("Spin complete", snippet);
                Assert.DoesNotContain("a few times", snippet);
            }
        }
        Assert.Contains(items,
            i => i["sites"].EnumerateArray().Any(s => (s.GetProperty("snippet").GetString() ?? "").Contains("Spin(3)")));
    }

    // Conformance C3 + C5: a breaking change is neither sufficient nor applied, and every root cause
    // carries a non-empty suggestedInspection.
    [Fact]
    public async Task ValidatePatch_BreakingChange_NotAppliedWithRootCauses_C3_C5()
    {
        var sym = Root(await GetSymbol("Sample.Lib.Widget.Spin", "all"));
        var symbolId = sym.GetProperty("symbolId").GetString()!;
        var version = sym.GetProperty("contentVersion").GetString()!;

        var edits = new[] { new PatchEditInput("Lib/Widget.cs", 12, 12, "    public int Spin(int turns, int extra) => turns * 2 + extra;") };
        var root = Root(await ContextToolsValidate(new Dictionary<string, string> { [symbolId] = version }, edits,
            applyOnSuccess: true, intent: "add extra factor"));

        Assert.False(root.GetProperty("ladder").GetProperty("isSufficient").GetBoolean());
        Assert.False(root.GetProperty("applied").GetBoolean()); // C3: applied never co-occurs with insufficient

        var rootCauses = TableRows(root.GetProperty("diagnostics").GetProperty("rootCauses"));
        Assert.True(rootCauses.Count > 0);
        foreach (var rc in rootCauses)
            Assert.True(rc["suggestedInspection"].GetArrayLength() > 0); // C5
    }

    // Conformance C12 (+ C3 positive): a sufficient, successful, applied patch appends exactly one
    // feature_log row with per-symbol rows matching detectedChanges.
    [Fact]
    public async Task ValidatePatch_BodyChange_AppliesAndLogsOnce_C12()
    {
        // The fixture runs on a throwaway temp copy, so this apply's disk write is discarded on dispose.
        var sym = Root(await GetSymbol("Sample.Lib.Widget.Spin", "all"));
        var symbolId = sym.GetProperty("symbolId").GetString()!;
        var version = sym.GetProperty("contentVersion").GetString()!;
        // Scoped by symbolId rather than a unique taskId (task ids are no longer a caller-facing
        // concept - every call in this process shares one ambient session id), so isolation from other
        // tests comes from this being the only test that edits Widget.Spin.
        var before = _f.FeatureLog.RecentForSymbolWithChain(symbolId, 50).Count;

        var edits = new[] { new PatchEditInput("Lib/Widget.cs", 12, 12, "    public int Spin(int turns) => turns * 3;") };
        var root = Root(await PatchTools.ValidatePatch(_f.Workspace, _f.Locator, _f.Symbols, _f.FeatureLog, _f.Builder, _f.TargetedTests, _f.Telemetry,
            new Dictionary<string, string> { [symbolId] = version }, edits,
            requestedLevel: null, applyOnSuccess: true, intent: "tune spin factor", tags: null));

        Assert.True(root.GetProperty("ladder").GetProperty("isSufficient").GetBoolean());
        Assert.True(root.GetProperty("applied").GetBoolean());

        var after = _f.FeatureLog.RecentForSymbolWithChain(symbolId, 50).Count;
        Assert.Equal(before + 1, after);   // exactly one feature_log row logged for this symbol
    }

    /// <summary>
    /// A search hit carries where it was found, so "search, then go there" is one call rather than two.
    /// The line is checked against the file's actual content, not just asserted non-null — a location
    /// that points at the wrong line is worse than none, since a caller has no reason to doubt it.
    /// endLine is the same fetch-strategy signal get_symbol's declarationSites gives, cheap enough to
    /// check here too: a declaration's end can never come before its start.
    /// </summary>
    [Fact]
    public async Task SearchIndex_HitCarriesTheFileAndLineItWasFoundAt()
    {
        var hit = TableRows(Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "SpinTwice", groupBy: "none")).GetProperty("items")).First();

        var file = hit["file"].GetString()!;
        var line = hit["line"].GetInt32();
        var endLine = hit["endLine"].GetInt32();

        var text = await File.ReadAllLinesAsync(_f.Locator.AbsPath(file));
        Assert.Contains("SpinTwice", text[line - 1]);
        Assert.True(endLine >= line);
    }

    /// <summary>
    /// summary:"has" is a cheap presence check — a documented symbol reports hasSummary:true with no
    /// summary text sent, so a caller can spot "is this documented" without paying for the extracted text.
    /// </summary>
    [Fact]
    public async Task SearchIndex_SummaryHas_ReportsPresenceWithoutText()
    {
        var hit = TableRows(Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Spin", summary: "has", groupBy: "none")).GetProperty("items"))
            .First(h => !h["name"].GetString()!.Contains("Turbo") && !h["name"].GetString()!.Contains("SpinTwice"));

        Assert.True(hit["hasSummary"].GetBoolean());
        Assert.False(hit.ContainsKey("summary"));
    }

    /// <summary>
    /// summary:"full" returns the actual extracted &lt;summary&gt; text, matching what get_symbol's
    /// xmlDoc.summary reports for the same member — one call instead of a search followed by a fetch.
    /// </summary>
    [Fact]
    public async Task SearchIndex_SummaryFull_ReturnsExtractedText()
    {
        var hit = TableRows(Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Spin", summary: "full", groupBy: "none")).GetProperty("items"))
            .First(h => !h["name"].GetString()!.Contains("Turbo") && !h["name"].GetString()!.Contains("SpinTwice"));

        Assert.Equal("Spins the widget.", hit["summary"].GetString());
    }

    /// <summary>
    /// Omitting summary must be byte-for-byte the pre-3.18 response — no hasSummary/summary field on
    /// any item — so every existing caller that never asked for it sees nothing new.
    /// </summary>
    [Fact]
    public async Task SearchIndex_OmittingSummary_AddsNoFields()
    {
        var hit = TableRows(Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "SpinTwice", groupBy: "none")).GetProperty("items")).First();

        Assert.False(hit.ContainsKey("hasSummary"));
        Assert.False(hit.ContainsKey("summary"));
    }

    /// <summary>
    /// The index keys members without their parameter lists, so overloads collapse to one name and the
    /// site cannot be resolved. Omit it: absent already means "call get_symbol", which is what a caller
    /// did before locations existed, whereas a confidently wrong line is a new failure mode.
    /// </summary>
    [Fact]
    public async Task SearchIndex_OmitsTheLineForAnOverloadRatherThanPickingOne()
    {
        var hit = TableRows(Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Ambiguous", groupBy: "none")).GetProperty("items")).First();

        Assert.False(hit.ContainsKey("file"));
        Assert.False(hit.ContainsKey("line"));
        // The hit itself is still useful — it just costs a get_symbol to locate.
        Assert.Contains("Ambiguous", hit["name"].GetString());
    }

    /// <summary>
    /// The tier markers (degraded / index_only) describe the workspace, not the answer. A fully loaded,
    /// undegraded workspace can still hold a file that moved underneath it, and a response that says
    /// nothing then asserts content which no longer exists on disk while looking perfectly healthy.
    ///
    /// Observed live: get_symbol served a method body from before a commit, with no marker at all, and
    /// the only way to notice was to read the file by hand.
    /// </summary>
    [Fact]
    public async Task GetSymbol_MarksTheAnswerStaleWhenItsFileMovedUnderTheWorkspace()
    {
        // Deliberately does not read the symbol first, so the check is not handed a document whose text
        // this test itself materialised. That does not currently change the outcome — the fixture's
        // workspace has the text either way — so this is cheap insurance rather than the thing under
        // test, and it is not a substitute for a genuinely cold workspace, which the shared fixture
        // cannot offer.
        var path = _f.Locator.AbsPath("Lib/Gadget.cs");
        var original = await File.ReadAllTextAsync(path);

        await File.WriteAllTextAsync(path, original + Environment.NewLine + "// moved on disk");
        try
        {
            var root = Root(await GetSymbol("Sample.Lib.Gadget.Left", "all"));
            Assert.Equal("stale", root.GetProperty("limitedBy").GetString());
        }
        finally
        {
            await File.WriteAllTextAsync(path, original);
        }
    }

    /// <summary>
    /// An apply writes the whole document text back, so a patch built on a workspace copy that has
    /// fallen behind disk reverts every other change made to that file in the meantime — silently, with
    /// a success verdict. baseVersions cannot catch it: it guards the symbols the classifier saw
    /// change, and the damage is to the part of the file nobody touched.
    ///
    /// Observed live in this repo before the guard existed: the workspace had missed a commit, a
    /// one-method patch reported applied:true, and that commit's other edits to the same file were gone.
    /// </summary>
    [Fact]
    public async Task ValidatePatch_RefusesToApplyOverAFileThatMovedUnderTheWorkspace()
    {
        var sym = Root(await GetSymbol("Sample.Lib.Widget.Spin", "all"));
        var symbolId = sym.GetProperty("symbolId").GetString()!;
        var version = sym.GetProperty("contentVersion").GetString()!;

        var path = _f.Locator.AbsPath("Lib/Widget.cs");
        var original = await File.ReadAllTextAsync(path);
        // An out-of-band edit the workspace never saw — as a git checkout or a plain Edit would leave it.
        var outOfBand = original + Environment.NewLine + "// touched on disk, behind the workspace's back";
        await File.WriteAllTextAsync(path, outOfBand);
        try
        {
            var edits = new[] { new PatchEditInput("Lib/Widget.cs", 12, 12, "    public int Spin(int turns) => turns * 9;") };
            var root = Root(await ContextToolsValidate(
                new Dictionary<string, string> { [symbolId] = version }, edits,
                applyOnSuccess: true, intent: "should never be applied"));

            Assert.Equal("stale_workspace", root.GetProperty("error").GetString());
            // The out-of-band content is still intact: the patch reverted nothing.
            Assert.Equal(outOfBand, await File.ReadAllTextAsync(path));
        }
        finally
        {
            await File.WriteAllTextAsync(path, original);
        }
    }

/// <summary>
    /// The literal C# modifier phrase is unconditional, not an opt-in include component: it comes back on
    /// a default ("standard") call the same as any other. HighGear is `public sealed class HighGear :
    /// GearBase`, so its own modifiers render "public sealed", and its Ratio override renders
    /// "public override" — there is no separate accessibility field, modifiers already carries it.
    /// </summary>
    [Fact]
    public async Task GetSymbol_Modifiers_RendersLiteralKeywordPhrase()
    {
        var type = Root(await GetSymbol("Sample.Lib.HighGear"));
        Assert.Equal("public sealed", type.GetProperty("content").GetProperty("modifiers").GetString());
        Assert.False(type.GetProperty("content").TryGetProperty("accessibility", out _));

        var method = Root(await GetSymbol("Sample.Lib.HighGear.Ratio"));
        Assert.Equal("public override", method.GetProperty("content").GetProperty("modifiers").GetString());
    }

/// <summary>
    /// source suppresses everything that would just restate the declaration's own signature/body as
    /// structured JSON alongside the text: displayString, modifiers, xmlDoc, attributes, baseType,
    /// interfaces. usings is NOT suppressed — a symbol's own source span never includes the file's using
    /// directives, so it stays genuinely new information even next to source.
    /// </summary>
    [Fact]
    public async Task GetSymbol_Source_SuppressesFieldsSourceAlreadyPrintsAsText()
    {
        var root = Root(await GetSymbol("Sample.Lib.HighGear", "source,xmlDoc,attributes,baseType,interfaces,usings"));
        var content = root.GetProperty("content");

        Assert.True(content.GetProperty("source").GetArrayLength() > 0);
        Assert.False(content.TryGetProperty("displayString", out _));
        Assert.False(content.TryGetProperty("modifiers", out _));
        Assert.False(content.TryGetProperty("xmlDoc", out _));
        Assert.False(content.TryGetProperty("attributes", out _));
        Assert.False(content.TryGetProperty("baseType", out _));
        Assert.False(content.TryGetProperty("interfaces", out _));
    }

    /// <summary>
    /// baseType/interfaces are type-only: direct only (not the transitive chain get_type_hierarchy
    /// already owns), and absent entirely -- not null-and-present -- for a member.
    /// </summary>
    [Fact]
    public async Task GetSymbol_BaseTypeAndInterfaces_AreTypeOnlyAndDirect()
    {
        var highGear = Root(await GetSymbol("Sample.Lib.HighGear", "baseType,interfaces"));
        Assert.Equal("GearBase",
            highGear.GetProperty("content").GetProperty("baseType").GetProperty("displayString").GetString());

        var widget = Root(await GetSymbol("Sample.Lib.Widget", "interfaces"));
        var interfaces = widget.GetProperty("content").GetProperty("interfaces");
        Assert.Contains(interfaces.EnumerateArray(), i => i.GetProperty("displayString").GetString() == "IWidget");

        var method = Root(await GetSymbol("Sample.Lib.Widget.Spin", "baseType,interfaces"));
        Assert.False(method.GetProperty("content").TryGetProperty("baseType", out _));
        Assert.False(method.GetProperty("content").TryGetProperty("interfaces", out _));
    }

    /// <summary>
    /// usings reads straight off the Roslyn syntax tree: a file-scoped-namespace type sees the
    /// compilation unit's own using directives, a classic block-scoped namespace's type sees usings
    /// declared inside that namespace block instead, and a symbol with no usings in scope gets null
    /// rather than an empty array.
    /// </summary>
    [Fact]
    public async Task GetSymbol_Usings_ReadsFileScopedAndNamespaceScopedDirectives()
    {
        var fileScoped = Root(await GetSymbol("Sample.Lib.UsingsSample", "usings"));
        var usings = fileScoped.GetProperty("content").GetProperty("usings");
        Assert.Contains(usings.EnumerateArray(), u => u.GetString() == "using System;");
        Assert.Contains(usings.EnumerateArray(), u => u.GetString() == "using System.Collections.Generic;");

        var classic = Root(await GetSymbol("Sample.Lib.Classic.ClassicNamespaceSample", "usings"));
        var classicUsings = classic.GetProperty("content").GetProperty("usings");
        var only = Assert.Single(classicUsings.EnumerateArray());
        Assert.Equal("using System.Text;", only.GetString());

        var noUsings = Root(await GetSymbol("Sample.Lib.Widget", "usings"));
        Assert.False(noUsings.GetProperty("content").TryGetProperty("usings", out _));
    }

    /// <summary>Bare modifier tokens AND: "public sealed" must match TurboWidget only, not plain Widget.</summary>
    [Fact]
    public async Task SearchIndex_ModifiersFilter_RequiresAllIncludedTokens()
    {
        var root = Root(await ContextTools.SearchIndex(_f.Symbols, _f.Index, _f.Workspace, _f.Telemetry,
            "Widget", kinds: "class", modifiers: "public sealed", limit: 10, groupBy: "none"));

        var items = TableRows(root.GetProperty("items"));
        Assert.Single(items);
        Assert.Equal("Sample.Lib.TurboWidget", items[0]["name"].GetString());
    }

    /// <summary>The implements filter narrows to direct implementers of the named interface.</summary>
    [Fact]
    public async Task SearchIndex_ImplementsFilter_ReturnsDirectImplementersOfTheNamedInterface()
    {
        var root = Root(await ContextTools.SearchIndex(_f.Symbols, _f.Index, _f.Workspace, _f.Telemetry,
            "Widget", kinds: "class", implements: "IWidget", limit: 10, groupBy: "none"));

        var names = TableRows(root.GetProperty("items")).Select(i => i["name"].GetString()).ToList();
        Assert.Contains("Sample.Lib.Widget", names);
        Assert.Contains("Sample.Lib.TurboWidget", names);
    }

    /// <summary>A query matching only non-implementers of the named interface returns no items.</summary>
    [Fact]
    public async Task SearchIndex_ImplementsFilter_ExcludesNonImplementers()
    {
        var root = Root(await ContextTools.SearchIndex(_f.Symbols, _f.Index, _f.Workspace, _f.Telemetry,
            "Gear", kinds: "class", implements: "IWidget", limit: 10, groupBy: "none"));

        Assert.Empty(TableRows(root.GetProperty("items")));
    }

    /// <summary>
    /// The default groupBy:"namespace" collapses straight to flat namespace/file header fields plus one
    /// symbols table when the whole result set shares a single namespace and a single file — no wrapper
    /// arrays for the common single-file search. A leaf's kind column also hoists to a header field
    /// when every hit in that leaf shares one kind. limit:1 isolates SpinTwice on its own — the bare
    /// query also fuzzy-matches Spin, which spans a second file and would not collapse. limitedBy is
    /// omitted entirely (not printed as null) when nothing limited the answer, same as the flat
    /// groupBy:"none" shape.
    /// </summary>
    [Fact]
    public async Task SearchIndex_CollapsesToFlatHeader_WhenResultsShareOneNamespaceAndFile()
    {
        var root = Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "SpinTwice", limit: 1));

        Assert.False(root.TryGetProperty("limitedBy", out _));
        Assert.Equal("Sample.Lib", root.GetProperty("namespace").GetString());
        Assert.EndsWith("Pipeline.cs", root.GetProperty("file").GetString());
        Assert.Equal("Method", root.GetProperty("kind").GetString());
        var symbols = TableRows(root.GetProperty("symbols"));
        var symbol = Assert.Single(symbols);
        Assert.False(symbol.ContainsKey("kind"));
        Assert.Equal("WidgetExtensions.SpinTwice(IWidget,int)", symbol["name"].GetString());
    }

    /// <summary>
    /// A query spanning several files under one namespace nests namespaces[] -> files[] -> symbols[]
    /// rather than collapsing, since the file axis still varies — the wrapper array stays even though
    /// there is only one namespace, matching the file-grouped shape's own per-group array discipline.
    /// </summary>
    [Fact]
    public async Task SearchIndex_GroupsByNamespaceByDefault_NestingMultipleFilesUnderOneNamespace()
    {
        var root = Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Widget", kinds: "class", limit: 10));

        Assert.Equal("namespace", root.GetProperty("groupedBy").GetString());
        var namespaces = root.GetProperty("namespaces").EnumerateArray().ToList();
        var ns = Assert.Single(namespaces);
        Assert.Equal("Sample.Lib", ns.GetProperty("name").GetString());
        var files = ns.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()).ToList();
        Assert.Contains(files, f => f!.EndsWith("Widget.cs"));
        Assert.Contains(files, f => f!.EndsWith("Pipeline.cs"));
    }

    /// <summary>groupBy:"file" inverts the nesting: files[] -> namespaces[] -> symbols[].</summary>
    [Fact]
    public async Task SearchIndex_GroupByFile_NestsNamespaceInsideFile()
    {
        var root = Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Widget", kinds: "class", limit: 10, groupBy: "file"));

        Assert.Equal("file", root.GetProperty("groupedBy").GetString());
        var files = root.GetProperty("files").EnumerateArray().ToList();
        Assert.True(files.Count >= 2);
        foreach (var file in files)
        {
            var namespaces = file.GetProperty("namespaces").EnumerateArray().ToList();
            var ns = Assert.Single(namespaces);
            Assert.Equal("Sample.Lib", ns.GetProperty("name").GetString());
        }
    }

    /// <summary>groupBy:"none" keeps the flat items[] list — file/kind repeated per row, no namespace field.</summary>
    [Fact]
    public async Task SearchIndex_GroupByNone_ReturnsTheFlatItemsList()
    {
        var root = Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Widget", kinds: "class", limit: 10, groupBy: "none"));

        Assert.False(root.TryGetProperty("groupedBy", out _));
        var hit = TableRows(root.GetProperty("items")).First();
        Assert.True(hit.ContainsKey("file"));
        Assert.True(hit.ContainsKey("kind"));
        Assert.False(hit.ContainsKey("namespace"));
    }

    /// <summary>
    /// search_index defaults to origin:"source" — an external symbol discovered only as a call/implements
    /// target (never declared in this repo) must not appear in a plain query, matching every existing
    /// caller's expectations unchanged.
    /// </summary>
    [Fact]
    public async Task SearchIndex_DefaultOrigin_ExcludesExternalSymbols()
    {
        var root = Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "IDisposable", groupBy: "none"));

        Assert.Empty(TableRows(root.GetProperty("items")));
    }

    /// <summary>
    /// origin:"external" surfaces a BCL symbol ExternalRefSample references — System.IDisposable via the
    /// implements edge, System.Linq.Enumerable.Where via a reduced extension-method call — discovered
    /// only because this repo's own source references them, not as a general library browser.
    /// </summary>
    [Fact]
    public async Task SearchIndex_ExternalOrigin_FindsCallAndImplementsTargets()
    {
        var interfaces = TableRows(Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "IDisposable",
            kinds: "interface", origin: "external", groupBy: "none")).GetProperty("items"));
        Assert.Contains(interfaces, i => i["name"].GetString()!.Contains("IDisposable", StringComparison.Ordinal));

        var methods = TableRows(Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Where",
            kinds: "method", origin: "external", groupBy: "none")).GetProperty("items"));
        Assert.Contains(methods, m => m["name"].GetString()!.Contains("Where", StringComparison.Ordinal));
    }

    /// <summary>
    /// get_symbol resolves a previously-discovered external symbol via its stored documentation-comment
    /// id: origin reads "external", declarationSites is empty (no source location), and kind comes from
    /// the live metadata symbol, not a guess.
    /// </summary>
    [Fact]
    public async Task GetSymbol_ExternalSymbol_ResolvesWithEmptyDeclarationSites()
    {
        var hit = TableRows(Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "IDisposable",
            kinds: "interface", origin: "external", groupBy: "none")).GetProperty("items")).First();
        var symbolId = hit["symbolId"].GetString()!;

        var resolved = Root(await GetSymbol(symbolId));
        Assert.Equal("external", resolved.GetProperty("content").GetProperty("origin").GetString());
        Assert.Equal("Interface", resolved.GetProperty("content").GetProperty("kind").GetString());
        Assert.Empty(resolved.GetProperty("content").GetProperty("declarationSites").EnumerateArray());
    }

    /// <summary>A source symbol's origin still reads "source", unaffected by external indexing.</summary>
    [Fact]
    public async Task GetSymbol_SourceSymbol_OriginReadsSource()
    {
        var root = Root(await GetSymbol("Sample.Lib.Widget"));
        Assert.Equal("source", root.GetProperty("content").GetProperty("origin").GetString());
    }


    private Task<string> ContextToolsValidate(Dictionary<string, string> baseVersions, PatchEditInput[] edits, bool applyOnSuccess, string? intent) =>
        PatchTools.ValidatePatch(_f.Workspace, _f.Locator, _f.Symbols, _f.FeatureLog, _f.Builder, _f.TargetedTests, _f.Telemetry,
            baseVersions, edits, requestedLevel: null, applyOnSuccess: applyOnSuccess, intent: intent, tags: null);
}
