using System.Diagnostics;
using System.Text.Json;
using DotnetToolkit.McpServer.Indexing;
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
    private const string Session = "ses_test";
    private const string Task_ = "tsk_test";

    private readonly SampleSolutionFixture _f;

    public WorkspaceIntegrationTests(SampleSolutionFixture fixture) => _f = fixture;

    private Task<string> GetSymbol(
        string symbol, string resolution = "signature", string? knownVersion = null, bool refetch = false,
        string? include = null, string? exclude = null) =>
        ContextTools.GetSymbol(_f.Workspace, _f.Locator, _f.Index, _f.Symbols, _f.FeatureLog, _f.Builder, _f.Telemetry,
            symbol, resolution, include, exclude, knownVersion, refetch, Session, Task_);

    private Task<string> GetReferences(string symbol, string direction) =>
        ContextTools.GetReferences(_f.Workspace, _f.Locator, _f.Symbols, _f.Telemetry, symbol, direction, sessionId: Session, taskId: Task_);

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

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
    public void SearchIndex_MultiWordQuery_FindsSymbolsForEachTerm()
    {
        var root = Root(ContextTools.SearchIndex(_f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Widget Gadget"));

        var names = root.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()!).ToList();

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
        var hit = Root(ContextTools.SearchIndex(_f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "SpinTwice"))
            .GetProperty("items").EnumerateArray().First();
        var name = hit.GetProperty("name").GetString()!;

        // Fully qualified up to the member, but the parameter's namespace is gone.
        Assert.StartsWith("Sample.Lib.WidgetExtensions.SpinTwice(", name);
        Assert.DoesNotContain("Sample.Lib.IWidget", name);
        Assert.Contains("IWidget", name);

        var resolved = Root(await GetSymbol(name));

        Assert.False(resolved.TryGetProperty("error", out _));
        Assert.Equal(hit.GetProperty("symbolId").GetString(), resolved.GetProperty("symbolId").GetString());
    }

    /// <summary>
    /// include/exclude adjust the resolution's default set. The point is a targeted fetch: everything
    /// known about a symbol except the expensive part, in one call rather than a resolution that is
    /// either too thin or drags the whole body along.
    /// </summary>
    [Fact]
    public async Task GetSymbol_ExcludeDropsAComponentFromTheResolutionDefault()
    {
        var full = Root(await GetSymbol("Sample.Lib.Widget.Spin", "full"));
        Assert.False(string.IsNullOrEmpty(full.GetProperty("content").GetProperty("source").GetString()));

        var trimmed = Root(await GetSymbol("Sample.Lib.Widget.Spin", "full", exclude: "source"));
        var content = trimmed.GetProperty("content");

        // Absent entirely, not present-and-null: an excluded component costs no tokens at all.
        Assert.False(content.TryGetProperty("source", out _));
        // ...while the rest of "full" is untouched.
        Assert.True(content.TryGetProperty("referenceCounts", out _));
        Assert.Equal("Method", content.GetProperty("kind").GetString());
        // The resolved set is echoed only when the caller narrowed it, so it can see what it got.
        Assert.DoesNotContain("source", trimmed.GetProperty("components").EnumerateArray()
            .Select(c => c.GetString()));
    }

    [Fact]
    public async Task GetSymbol_IncludeAddsAComponentToTheResolutionDefault()
    {
        var plain = Root(await GetSymbol("Sample.Lib.Widget"));
        Assert.False(plain.GetProperty("content").TryGetProperty("members", out _));

        var withMembers = Root(await GetSymbol("Sample.Lib.Widget", include: "members"));
        var members = withMembers.GetProperty("content").GetProperty("members");

        Assert.NotEmpty(members.EnumerateArray());
        // Still no source: include adds one component, it does not escalate to full.
        Assert.False(withMembers.GetProperty("content").TryGetProperty("source", out _));
    }

    /// <summary>
    /// A misspelled component fails loudly. Ignoring it would leave the caller believing it dropped a
    /// field it is in fact still paying for — the failure mode is silent and costs tokens every call.
    /// </summary>
    [Fact]
    public async Task GetSymbol_UnknownComponentIsRejectedRatherThanIgnored()
    {
        var root = Root(await GetSymbol("Sample.Lib.Widget.Spin", exclude: "sourceCode"));

        Assert.Equal("invalid_component", root.GetProperty("error").GetString());
        Assert.Contains("sourceCode", root.GetProperty("detail").GetString());
        Assert.Contains("source", root.GetProperty("detail").GetString());
    }

    /// <summary>
    /// outline used to be built by an early return from its own object literal, which silently omitted
    /// containingType and recentLog. One build path means a component appears whenever it is asked for,
    /// regardless of which resolution named it.
    /// </summary>
    [Fact]
    public async Task GetSymbol_OutlineCarriesTheSameSkeletonAsEveryOtherResolution()
    {
        var outline = Root(await GetSymbol("Sample.Lib.Widget", "outline"));
        var content = outline.GetProperty("content");

        Assert.NotEmpty(content.GetProperty("members").EnumerateArray());
        Assert.True(content.TryGetProperty("declarationSites", out _));
        Assert.Equal("Type", content.GetProperty("kind").GetString());
        Assert.True(content.TryGetProperty("accessibility", out _));
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
        var items = root.GetProperty("items").EnumerateArray().ToList();

        Assert.NotEmpty(items);
        // isTest is emitted only when true, so absence is the "not a test" signal.
        foreach (var item in items)
        {
            var isTest = item.TryGetProperty("isTest", out var flag) && flag.GetBoolean();
            var name = item.GetProperty("displayString").GetString()!;
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
    public void ReferenceCounts_OmittedWhenProjectHasNoEdgeCoverage()
    {
        // A symbol id from no indexed project at all: coverage cannot be established for it.
        Assert.False(_f.Symbols.HasEdgeCoverageFor("sym_not_a_real_symbol"));

        // The fixture's own project does have edges, so real symbols stay measurable.
        var root = Root(ContextTools.SearchIndex(_f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Spin", kinds: "Method"));
        var id = root.GetProperty("items").EnumerateArray().First().GetProperty("symbolId").GetString()!;
        Assert.True(_f.Symbols.HasEdgeCoverageFor(id));
    }

    [Fact]
    public async Task GetSymbol_Full_CarriesVersionAndReferenceCounts()
    {
        var root = Root(await GetSymbol("Sample.Lib.IWidget", "full"));

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
        var byName = Root(await GetSymbol("Sample.Lib.Widget", "signature"));
        var symbolId = byName.GetProperty("symbolId").GetString()!;

        var byId = Root(await GetSymbol(symbolId, "signature"));

        Assert.Equal(symbolId, byId.GetProperty("symbolId").GetString());
        Assert.Equal(byName.GetProperty("contentVersion").GetString(), byId.GetProperty("contentVersion").GetString());
    }

    [Fact]
    public async Task SearchIndex_ReturnsResolvableNames_AndAcceptsClassAlias()
    {
        var root = Root(ContextTools.SearchIndex(_f.Symbols, _f.Index, _f.Workspace, _f.Telemetry,
            "Widget", kinds: "class", limit: 10));

        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items); // "class" must alias to the stored "Type" kind, case-insensitively

        // The returned name is directly usable as a get_symbol target (no global:: prefix).
        var name = items[0].GetProperty("name").GetString()!;
        Assert.DoesNotContain("global::", name);
        var fetched = Root(await GetSymbol(name, "signature"));
        Assert.True(fetched.TryGetProperty("content", out _));
    }

    // referenceCounts gates expansion (P1.4: "0 callers -> no get_references"), so a false zero makes
    // the agent skip an expansion it needs. The count must agree with get_references — including calls
    // made from top-level statements, which are not ordinary member declarations.
    [Fact]
    public async Task ReferenceCounts_AgreeWithGetReferences_IncludingTopLevelCallers()
    {
        var sym = Root(await GetSymbol("Sample.Lib.Widget.Spin", "full"));
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
        var sym = Root(await GetSymbol("Sample.Lib.Pipeline.Deep", "signature"));
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
        var type = Root(await GetSymbol("Sample.Lib.Widget", "signature"));
        var typeCounts = type.GetProperty("content").GetProperty("referenceCounts");
        Assert.False(typeCounts.TryGetProperty("callers", out _), "a type must not claim a caller count");
        Assert.True(typeCounts.TryGetProperty("implementations", out _), "implementations is meaningful for a type");

        var member = Root(await GetSymbol("Sample.Lib.Widget.Spin", "signature"));
        var memberCounts = member.GetProperty("content").GetProperty("referenceCounts");
        Assert.True(memberCounts.GetProperty("callers").GetInt32() >= 1);
    }

    // Internal helper properties must not ride along in the wire payload.
    [Fact]
    public async Task MechanicalFacts_DoNotLeakInternalProperties()
    {
        var root = Root(await GetSymbol("Sample.Lib.Pipeline.Deep", "full"));
        if (root.GetProperty("content").TryGetProperty("mechanicalFacts", out var facts)
            && facts.ValueKind == JsonValueKind.Object)
        {
            Assert.False(facts.TryGetProperty("IsEmpty", out _), "IsEmpty is an internal guard, not a fact");
        }
    }

    // Conformance C10: one partial-class part returns the unified type with all declaration sites.
    [Fact]
    public async Task GetSymbol_UnifiesPartialClass_C10()
    {
        var root = Root(await GetSymbol("Sample.Lib.Gadget", "signature"));
        var sites = root.GetProperty("content").GetProperty("declarationSites");
        Assert.Equal(2, sites.GetArrayLength());
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
        const string DeclOnlySet = "referenceCounts,recentLog";
        var first = Root(await GetSymbol("Sample.Lib.Widget.Spin", exclude: DeclOnlySet));
        var declOnly = first.GetProperty("contentVersion").GetString()!;
        Assert.DoesNotContain("|", declOnly);
        Assert.StartsWith("decl:", declOnly);

        // Holding exactly what that response covered leases cleanly.
        var leased = Root(await GetSymbol(
            "Sample.Lib.Widget.Spin", knownVersion: declOnly, exclude: DeclOnlySet));

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

        var full = Root(await GetSymbol("Sample.Lib.Widget.Spin", "full", knownVersion: signatureToken));

        Assert.False(full.TryGetProperty("changed", out _));
        Assert.False(string.IsNullOrEmpty(
            full.GetProperty("content").GetProperty("source").GetString()));
    }

    [Fact]
    public async Task GetReferences_Implementations_FindsBothWidgets()
    {
        var root = Root(await GetReferences("Sample.Lib.IWidget", "implementations"));
        var displays = root.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("displayString").GetString() ?? "").ToList();
        Assert.Contains(displays, d => d.Contains("Widget"));
        Assert.Contains(displays, d => d.Contains("TurboWidget"));
    }

    [Fact]
    public async Task GetReferences_Overrides_FindsHighGear()
    {
        var root = Root(await GetReferences("Sample.Lib.GearBase.Ratio", "overrides"));
        var displays = root.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("displayString").GetString() ?? "").ToList();
        Assert.Contains(displays, d => d.Contains("HighGear"));
    }

    // Conformance C7: comment/string matches are excluded from items, counted in excludedKinds.
    [Fact]
    public async Task GetReferences_ExcludesCommentAndStringMatches_C7()
    {
        var root = Root(await GetReferences("Sample.Lib.Widget.Spin", "callers"));

        // Program.cs mentions "Spin" once in a comment and once in a string literal.
        Assert.Equal(2, root.GetProperty("excludedTextMatches").GetInt32());

        // The only returned item is the real call site; no item points at the comment/string.
        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            foreach (var site in item.GetProperty("sites").EnumerateArray())
            {
                var snippet = site.GetProperty("snippet").GetString() ?? "";
                Assert.DoesNotContain("Spin complete", snippet);
                Assert.DoesNotContain("a few times", snippet);
            }
        }
        Assert.Contains(root.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("sites").EnumerateArray().Any(s => (s.GetProperty("snippet").GetString() ?? "").Contains("Spin(3)")));
    }

    // Conformance C3 + C5: a breaking change is neither sufficient nor applied, and every root cause
    // carries a non-empty suggestedInspection.
    [Fact]
    public async Task ValidatePatch_BreakingChange_NotAppliedWithRootCauses_C3_C5()
    {
        var sym = Root(await GetSymbol("Sample.Lib.Widget.Spin", "full"));
        var symbolId = sym.GetProperty("symbolId").GetString()!;
        var version = sym.GetProperty("contentVersion").GetString()!;

        var edits = new[] { new PatchEditInput("Lib/Widget.cs", 12, 12, "    public int Spin(int turns, int extra) => turns * 2 + extra;") };
        var root = Root(await ContextToolsValidate(new Dictionary<string, string> { [symbolId] = version }, edits,
            applyOnSuccess: true, intent: "add extra factor"));

        Assert.False(root.GetProperty("ladder").GetProperty("isSufficient").GetBoolean());
        Assert.False(root.GetProperty("applied").GetBoolean()); // C3: applied never co-occurs with insufficient

        var rootCauses = root.GetProperty("diagnostics").GetProperty("rootCauses");
        Assert.True(rootCauses.GetArrayLength() > 0);
        foreach (var rc in rootCauses.EnumerateArray())
            Assert.True(rc.GetProperty("suggestedInspection").GetArrayLength() > 0); // C5
    }

    // Conformance C12 (+ C3 positive): a sufficient, successful, applied patch appends exactly one
    // feature_log row with per-symbol rows matching detectedChanges.
    [Fact]
    public async Task ValidatePatch_BodyChange_AppliesAndLogsOnce_C12()
    {
        // The fixture runs on a throwaway temp copy, so this apply's disk write is discarded on dispose.
        var applyTask = "tsk_apply_" + Guid.NewGuid().ToString("N")[..8];
        var sym = Root(await GetSymbol("Sample.Lib.Widget.Spin", "full"));
        var symbolId = sym.GetProperty("symbolId").GetString()!;
        var version = sym.GetProperty("contentVersion").GetString()!;
        var before = _f.FeatureLog.CountsForTask(applyTask);

        var edits = new[] { new PatchEditInput("Lib/Widget.cs", 12, 12, "    public int Spin(int turns) => turns * 3;") };
        var root = Root(await PatchTools.ValidatePatch(_f.Workspace, _f.Locator, _f.Symbols, _f.FeatureLog, _f.Builder, _f.TargetedTests, _f.Telemetry,
            new Dictionary<string, string> { [symbolId] = version }, edits,
            requestedLevel: null, applyOnSuccess: true, intent: "tune spin factor", tags: null,
            sessionId: Session, taskId: applyTask));

        Assert.True(root.GetProperty("ladder").GetProperty("isSufficient").GetBoolean());
        Assert.True(root.GetProperty("applied").GetBoolean());

        var after = _f.FeatureLog.CountsForTask(applyTask);
        Assert.Equal(before.Entries + 1, after.Entries);   // exactly one feature_log row
        Assert.Equal(before.Symbols + 1, after.Symbols);   // one per changed symbol (Widget.Spin)
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
        var path = _f.Locator.AbsPath("Lib/Widget.cs");
        var original = await File.ReadAllTextAsync(path);
        Assert.Null(Root(await GetSymbol("Sample.Lib.Widget.Spin", "full")).TryGetProperty("limitedBy", out var before) ? before.GetString() : null);

        await File.WriteAllTextAsync(path, original + Environment.NewLine + "// moved on disk");
        try
        {
            var root = Root(await GetSymbol("Sample.Lib.Widget.Spin", "full"));
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
        var sym = Root(await GetSymbol("Sample.Lib.Widget.Spin", "full"));
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

    private Task<string> ContextToolsValidate(Dictionary<string, string> baseVersions, PatchEditInput[] edits, bool applyOnSuccess, string? intent) =>
        PatchTools.ValidatePatch(_f.Workspace, _f.Locator, _f.Symbols, _f.FeatureLog, _f.Builder, _f.TargetedTests, _f.Telemetry,
            baseVersions, edits, requestedLevel: null, applyOnSuccess: applyOnSuccess, intent: intent, tags: null);
}
