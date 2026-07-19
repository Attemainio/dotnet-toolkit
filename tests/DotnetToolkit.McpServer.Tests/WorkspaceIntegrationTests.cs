using System.Diagnostics;
using System.Text.Json;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Telemetry;
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
        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"dotnet {args} failed:\n{stdout}\n{stderr}");
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

    private Task<string> GetSymbol(string symbol, string resolution = "signature", string? knownVersion = null, bool refetch = false) =>
        ContextTools.GetSymbol(_f.Workspace, _f.Locator, _f.Index, _f.Symbols, _f.Builder, _f.Telemetry,
            Session, Task_, symbol, resolution, knownVersion, refetch);

    private Task<string> GetReferences(string symbol, string direction) =>
        ContextTools.GetReferences(_f.Workspace, _f.Locator, _f.Symbols, _f.Telemetry, Session, Task_, symbol, direction);

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

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
        var root = Root(ContextTools.SearchIndex(_f.Symbols, _f.Index, _f.Telemetry, Session, Task_,
            "Widget", kinds: "class", limit: 10));

        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items); // "class" must alias to the stored "Type" kind, case-insensitively

        // The returned name is directly usable as a get_symbol target (no global:: prefix).
        var name = items[0].GetProperty("name").GetString()!;
        Assert.DoesNotContain("global::", name);
        var fetched = Root(await GetSymbol(name, "signature"));
        Assert.True(fetched.TryGetProperty("content", out _));
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
        var full = Root(await GetSymbol("Sample.Lib.Widget.Spin", "full"));
        var fullVersion = full.GetProperty("contentVersion").GetString()!;
        Assert.Contains("|body:", fullVersion); // a method carries both layers
        var declOnly = fullVersion.Split('|')[0];

        var leased = Root(await GetSymbol("Sample.Lib.Widget.Spin", knownVersion: declOnly));

        Assert.False(leased.GetProperty("changed").GetBoolean());
        Assert.Equal(declOnly, leased.GetProperty("heldVersion").GetString());
        Assert.Equal(fullVersion, leased.GetProperty("contentVersion").GetString());
        Assert.NotEqual(leased.GetProperty("heldVersion").GetString(), leased.GetProperty("contentVersion").GetString());
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
        var root = Root(await PatchTools.ValidatePatch(_f.Workspace, _f.Locator, _f.FeatureLog, _f.Builder, _f.Telemetry,
            Session, applyTask, new Dictionary<string, string> { [symbolId] = version }, edits,
            requestedLevel: null, applyOnSuccess: true, intent: "tune spin factor", tags: null));

        Assert.True(root.GetProperty("ladder").GetProperty("isSufficient").GetBoolean());
        Assert.True(root.GetProperty("applied").GetBoolean());

        var after = _f.FeatureLog.CountsForTask(applyTask);
        Assert.Equal(before.Entries + 1, after.Entries);   // exactly one feature_log row
        Assert.Equal(before.Symbols + 1, after.Symbols);   // one per changed symbol (Widget.Spin)
    }

    private Task<string> ContextToolsValidate(Dictionary<string, string> baseVersions, PatchEditInput[] edits, bool applyOnSuccess, string? intent) =>
        PatchTools.ValidatePatch(_f.Workspace, _f.Locator, _f.FeatureLog, _f.Builder, _f.Telemetry,
            Session, Task_, baseVersions, edits, requestedLevel: null, applyOnSuccess: applyOnSuccess, intent: intent, tags: null);
}
