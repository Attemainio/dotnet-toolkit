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

    public async ValueTask InitializeAsync()
    {
        // The product's own registration, not MSBuildLocator.RegisterDefaults(): Register() pools the
        // user-local SDK into the candidates, and RegisterDefaults() cannot see one at all when the runner
        // was launched from a system-wide host. Using it here loaded the fixture's projects on a different
        // SDK than the server uses, which is not the configuration under test. Register() no-ops when
        // something is already registered, so the IsRegistered guard moved inside it.
        MSBuildRegistration.Register();

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
        Assert.True(solution is not null,
                $"workspace did not load ({Workspace.State}): {string.Join("; ", Workspace.LoadDiagnostics)}");

        _store = new KnowledgeStore(Locator, NullLogger<KnowledgeStore>.Instance);
        Symbols = new SymbolStore(_store);
        FeatureLog = new FeatureLogStore(_store);
        Builder = new SymbolIndexBuilder(Workspace, Symbols, Locator, NullLogger<SymbolIndexBuilder>.Instance);
        await Builder.RebuildAsync();
        Assert.True(Builder.Ready);
        Telemetry = new TelemetryRecorder(_store, NullLogger<TelemetryRecorder>.Instance);
        TargetedTests = new TargetedTests(Locator, NullLogger<TargetedTests>.Instance);
        CallSlice = new CallSlice(Symbols);
    }

    public ValueTask DisposeAsync()
    {
        Workspace.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best-effort cleanup */ }
        return ValueTask.CompletedTask;
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
// A shared collection rather than IClassFixture, so a second integration test class costs nothing:
// the fixture copies the sample solution to a temp dir, restores it and loads an MSBuildWorkspace,
// which is the most expensive thing in the suite to do twice.
[Collection("SampleSolution")]
public sealed class WorkspaceIntegrationTests
{
    private readonly SampleSolutionFixture _f;

    public WorkspaceIntegrationTests(SampleSolutionFixture fixture) => _f = fixture;

    private Task<string> GetSymbol(string symbol, string? include = null, string? source = null) =>
        ContextTools.GetSymbol(_f.Workspace, _f.Locator, _f.Index, _f.Symbols, _f.FeatureLog, _f.Builder, _f.Telemetry,
            symbol, include, source);

    private Task<string> GetSymbols(string[] symbols, string? include = null, string? source = null) =>
        ContextTools.GetSymbol(_f.Workspace, _f.Locator, _f.Index, _f.Symbols, _f.FeatureLog, _f.Builder, _f.Telemetry,
            symbol: null, include, source, symbols: symbols);

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
    /// A hit's M count is read off the syntax outline, which counts every member a type declares, so a
    /// listing filtered by accessibility under-delivered against the very count that advertised it.
    /// </summary>
    [Fact]
    public async Task GetSymbol_Members_ListsPrivateOnesToo()
    {
        var content = Root(await GetSymbol("Sample.Lib.Pipeline", include: "members")).GetProperty("content");

        var names = content.GetProperty("members").EnumerateArray()
            .Select(m => m.GetProperty("displayString").GetString() ?? "").ToList();

        Assert.Equal(4, names.Count);
        Assert.Contains(names, n => n.Contains("Start", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("Middle", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("Deep", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("_widget", StringComparison.Ordinal));
    }

    /// <summary>
    /// A caller-info parameter is filled in by the compiler from the use site, so rendering it as the
    /// attribute's arguments reported a location nobody wrote — an absolute machine path, on the shape
    /// xUnit v3's [Fact] has and therefore on the most common attribute in a test project.
    /// </summary>
    [Fact]
    public async Task GetSymbol_Attributes_DropCompilerSuppliedCallerInfo()
    {
        var bare = Root(await GetSymbol("Sample.Lib.AttributeArgumentSample.Bare", include: "attributes"))
            .GetProperty("content").GetProperty("attributes")[0];

        Assert.Equal("Traced", bare.GetProperty("name").GetString());
        Assert.True(IsAbsentOrNull(bare, "arguments"));

        var written = Root(await GetSymbol("Sample.Lib.AttributeArgumentSample.Legacy", include: "attributes"))
            .GetProperty("content").GetProperty("attributes")[0];

        Assert.Equal("call Bare instead", written.GetProperty("arguments").GetString());
    }

    /// <summary>
    /// A bare type name is a suffix of the type's own display name AND of its constructor's
    /// (<c>Sample.Lib.TracedAttribute.TracedAttribute</c>), so expanding a type hit into its constructors made
    /// every class that declares one report ambiguous against itself — two calls where one answers, on most
    /// non-static classes. The constructor is still reachable by naming it, which is the half that must not
    /// regress while fixing the other.
    /// </summary>
    [Fact]
    public async Task GetSymbol_BareTypeName_ResolvesToTheTypeNotItsOwnConstructor()
    {
        var type = Root(await GetSymbol("TracedAttribute"));

        Assert.False(type.TryGetProperty("error", out _));
        Assert.Equal("Type", type.GetProperty("content").GetProperty("kind").GetString());

        var constructor = Root(await GetSymbol("TracedAttribute.TracedAttribute"));

        Assert.False(constructor.TryGetProperty("error", out _));
        Assert.Equal("Method", constructor.GetProperty("content").GetProperty("kind").GetString());
    }

    /// <summary>
    /// didYouMean reached only get_references' resolver, so the same miss through get_symbol — the far more
    /// common one — came back with nothing to act on.
    /// </summary>
    [Fact]
    public async Task GetSymbol_UnresolvedName_OffersNearMissCandidates()
    {
        var root = Root(await GetSymbol("Nowhere.Widget"));

        Assert.Equal("symbol_not_found", root.GetProperty("error").GetString());

        var names = root.GetProperty("didYouMean").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString() ?? "").ToList();

        Assert.Contains(names, n => n.EndsWith("Widget", StringComparison.Ordinal));
    }

    /// <summary>
    /// The ranked lookup behind didYouMean tokenizes camel case, so a name that is a typo of nothing came
    /// back ranked against every symbol merely sharing one token with it.
    /// </summary>
    [Fact]
    public async Task GetSymbol_NameResemblingNothing_OffersNoCandidates()
    {
        var root = Root(await GetSymbol("Nowhere.NoSuchSymbolAtAllXyz"));

        Assert.Equal("symbol_not_found", root.GetProperty("error").GetString());
        Assert.False(root.TryGetProperty("didYouMean", out _));
    }

    /// <summary>
    /// source:code strips the leading doc comment by design, so suppressing xmlDoc alongside it removed the
    /// documentation from the response instead of deduplicating it. An unsliced source:full still suppresses.
    /// </summary>
    [Fact]
    public async Task GetSymbol_XmlDoc_SuppressedOnlyWhenSourceActuallyCarriesTheDocComment()
    {
        var code = Root(await GetSymbol("Sample.Lib.Widget.Spin", source: "code-exact", include: "xmlDoc")).GetProperty("content");
        var full = Root(await GetSymbol("Sample.Lib.Widget.Spin", source: "full-exact", include: "xmlDoc")).GetProperty("content");

        Assert.DoesNotContain(
            code.GetProperty("source").EnumerateArray(),
            line => line.GetProperty("text").GetString()!.TrimStart().StartsWith("///"));
        Assert.False(string.IsNullOrWhiteSpace(code.GetProperty("xmlDoc").GetProperty("summary").GetString()));

        Assert.False(full.TryGetProperty("xmlDoc", out _));
    }

    /// <summary>
    /// A slice is judged on the lines it actually kept, not on being a slice: one covering the whole doc
    /// comment suppresses xmlDoc as a restatement, one landing past it still serves the structured form.
    /// </summary>
    [Fact]
    public async Task GetSymbol_XmlDoc_SuppressedWhenTheSliceItselfCarriesTheWholeDocComment()
    {
        var whole = Root(await GetSymbol("Sample.Lib.Widget.Spin", source: "full-exact")).GetProperty("content");
        var docLines = whole.GetProperty("source").EnumerateArray()
            .Where(l => l.GetProperty("text").GetString()!.TrimStart().StartsWith("///"))
            .Select(l => l.GetProperty("line").GetInt32())
            .ToList();
        Assert.NotEmpty(docLines);

        var covering = Root(await GetSymbol(
            "Sample.Lib.Widget.Spin",
            source: $"full-exact@{docLines[0]}-{docLines[^1]}", include: "xmlDoc")).GetProperty("content");
        Assert.False(covering.TryGetProperty("xmlDoc", out _));

        var past = Root(await GetSymbol(
            "Sample.Lib.Widget.Spin",
            source: $"full-exact@{docLines[^1] + 1}", include: "xmlDoc")).GetProperty("content");
        Assert.False(string.IsNullOrWhiteSpace(past.GetProperty("xmlDoc").GetProperty("summary").GetString()));
    }

    /// <summary>
    /// Being told a type has M members is only useful if the member list then says which one to open and
    /// where it is. A row that carried only a name and a version left that second hop with nothing to go
    /// on, so every row now states its own line and its own shape.
    /// </summary>
    [Fact]
    public async Task GetSymbol_MemberRowsCarryTheirOwnLocationAndShape()
    {
        var content = Root(await GetSymbol("Sample.Lib.Widget", "members")).GetProperty("content");

        var rows = TableRows(content.GetProperty("members")).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.True(row.ContainsKey("line")));
        Assert.Contains(rows, row => row.TryGetValue("shape", out var s) && s.GetString() is { Length: > 0 });

        // The legend is stated once beside the list, exactly as search_index states it once per response.
        Assert.Equal(SymbolShape.Legend, content.GetProperty("shape").GetString());
    }

    /// <summary>
    /// A member's file is only news when it is not the type's own — which is exactly the partial-class
    /// case, and nowhere else. Emitting it on every row would repeat the type's own path per member.
    /// </summary>
    [Fact]
    public async Task GetSymbol_MemberRowNamesItsFileOnlyWhenItDiffersFromTheTypes()
    {
        var content = Root(await GetSymbol("Sample.Lib.Gadget", "members")).GetProperty("content");

        var rows = TableRows(content.GetProperty("members")).ToList();
        Assert.Contains(rows, row => row.ContainsKey("file"));
        Assert.Contains(rows, row => !row.ContainsKey("file"));
    }

    /// <summary>
    /// Two producers render the same column: search_index measures a hit off the syntax index, get_symbol
    /// measures a member off its own syntax. They must agree, or a caller comparing the two reads a
    /// difference that is not in the code.
    /// </summary>
    [Fact]
    public async Task GetSymbol_MemberShapeAgreesWithSearchIndexOnTheSameSymbol()
    {
        var content = Root(await GetSymbol("Sample.Lib.Widget", "members")).GetProperty("content");
        var member = TableRows(content.GetProperty("members"))
            .Single(row => row["displayString"].GetString()!.Contains("Spin", StringComparison.Ordinal));

        var hits = Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Spin", limit: 20, groupBy: "none"));
        var hit = TableRows(hits.GetProperty("items"))
            .Single(row => row["symbolId"].GetString() == member["symbolId"].GetString());

        Assert.Equal(
            hit.TryGetValue("shape", out var fromSearch) ? fromSearch.GetString() : null,
            member.TryGetValue("shape", out var fromMember) ? fromMember.GetString() : null);
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

    [Fact]
    public async Task GetReferences_TestInvocationHint_AppearsForZeroCallerTestMethod()
    {
        var root = Root(await GetReferences("Sample.Lib.OrphanTestSample.NeverCalledDirectly", "callers"));

        Assert.Equal(0, root.GetProperty("totalItems").GetInt32());
        Assert.True(root.TryGetProperty("testInvocationHint", out var hint));
        Assert.Contains("reflection", hint.GetString());
    }

    [Fact]
    public async Task GetReferences_TestInvocationHint_AbsentForOrdinaryZeroCallerMethod()
    {
        var root = Root(await GetReferences("Sample.Lib.DocSectionsFixture.Undocumented", "callers"));

        Assert.Equal(0, root.GetProperty("totalItems").GetInt32());
        Assert.False(root.TryGetProperty("testInvocationHint", out _));
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
        // include:"all" serves the source, and kind is suppressed alongside displayString and modifiers
        // whenever a served signature line already states it.
        Assert.True(root.GetProperty("content").TryGetProperty("source", out _));
        Assert.False(root.GetProperty("content").TryGetProperty("kind", out _));
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

        var root = Root(await FlowTools.GetScope(_f.Workspace, _f.Locator, _f.Telemetry,
            file: "Lib/Pipeline.cs", line: line, column: 40, receiver: "_widget", filter: "methods"));

        var items = root.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("displayString").GetString() ?? "").ToList();
        Assert.Contains(items, i => i.Contains("Spin"));
        Assert.Contains(items, i => i.Contains("SpinTwice"));  // the extension method
    }

    [Fact]
    public async Task GetScope_Paging_ReachesItemsPastTheFirstPage()
    {
        var sym = Root(await GetSymbol("Sample.Lib.Pipeline.Deep"));
        var site = sym.GetProperty("content").GetProperty("declarationSites")[0];
        var line = site.GetProperty("startLine").GetInt32();

        var first = Root(await FlowTools.GetScope(_f.Workspace, _f.Locator, _f.Telemetry,
            file: "Lib/Pipeline.cs", line: line, column: 40, receiver: "_widget", filter: "methods", limit: 1));
        var total = first.GetProperty("totalItems").GetInt32();
        Assert.True(total >= 2, $"fixture needs a receiver with >=2 in-scope methods, got {total}");
        Assert.Single(first.GetProperty("items").EnumerateArray());
        Assert.True(first.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, first.GetProperty("nextOffset").GetInt32());

        var second = Root(await FlowTools.GetScope(_f.Workspace, _f.Locator, _f.Telemetry,
            file: "Lib/Pipeline.cs", line: line, column: 40, receiver: "_widget", filter: "methods",
            limit: 1, offset: 1));
        Assert.Equal(1, second.GetProperty("offset").GetInt32());
        Assert.NotEqual(
            first.GetProperty("items")[0].GetProperty("displayString").GetString(),
            second.GetProperty("items")[0].GetProperty("displayString").GetString());
    }


    /// <summary>
    /// Roslyn's LookupSymbols answers a position with the synthesized top-level-statements entry point's
    /// locals no matter which file the position is in, so "what is callable here" has to discard any local
    /// or parameter declared in another syntax tree -- one of those was never callable at this cursor.
    /// </summary>
    [Fact]
    public async Task GetScope_DoesNotOfferLocalsDeclaredInAnotherFile()
    {
        // Program.cs in the fixture is a top-level-statements file; its locals must not leak into Lib.
        var sym = Root(await GetSymbol("Sample.Lib.Pipeline.Deep"));
        var line = sym.GetProperty("content").GetProperty("declarationSites")[0]
            .GetProperty("startLine").GetInt32();

        var root = Root(await FlowTools.GetScope(_f.Workspace, _f.Locator, _f.Telemetry,
            file: "Lib/Pipeline.cs", line: line, column: 9, filter: "locals", limit: 200));

        var locals = root.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("kind").GetString() is "Local")
            .Select(i => i.GetProperty("displayString").GetString() ?? "").ToList();
        Assert.DoesNotContain(locals, l => l.Contains("builder", StringComparison.Ordinal));
        Assert.DoesNotContain(locals, l => l.Contains("app", StringComparison.Ordinal));
    }

    private Task<string> ContextToolsCallSlice(string from, string to) =>
        FlowTools.GetCallSlice(_f.Workspace, _f.Symbols, _f.CallSlice, _f.Builder, _f.Telemetry, from, to);

        /// <summary>
        /// A disjoint selection reports the runs it actually holds. Reporting min-to-max claimed the whole
        /// envelope, which is exactly the string a caller reads as "I have the whole declaration".
        /// </summary>
        [Fact]
        public async Task GetSymbol_DisjointLineRanges_ReportRunsNotTheEnvelope()
        {
            var content = Root(await GetSymbol("Sample.Lib.SourceQueryFixture", source: "full-exact@9-10;12-13"))
                .GetProperty("content");

            Assert.Equal(new[] { 9, 10, 12, 13 }, SourceLineNumbers(content));
            Assert.Equal("9-10;12-13/5-13", content.GetProperty("sourceLines").GetString());
        }

    /// <summary>
    /// totalItems stays the FULL count while a page carries fewer, and nextOffset reaches the items the
    /// page left out — the remainder used to be unreachable by any argument.
    /// </summary>
    [Fact]
    public async Task GetReferences_Paging_ReachesItemsPastTheFirstPage()
    {
        var total = Root(await GetReferences("Sample.Lib.IWidget", "callers"))
            .GetProperty("totalItems").GetInt32();
        Assert.True(total >= 2, $"fixture needs a type with >=2 referencing members, got {total}");

        var first = Root(await ContextTools.GetReferences(
            _f.Workspace, _f.Locator, _f.Symbols, _f.Telemetry, "Sample.Lib.IWidget", "callers", limit: 1));
        Assert.Single(first.GetProperty("items").EnumerateArray());
        Assert.Equal(total, first.GetProperty("totalItems").GetInt32());
        Assert.True(first.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, first.GetProperty("nextOffset").GetInt32());

        var second = Root(await ContextTools.GetReferences(
            _f.Workspace, _f.Locator, _f.Symbols, _f.Telemetry, "Sample.Lib.IWidget", "callers",
            limit: 1, offset: 1));
        Assert.Equal(1, second.GetProperty("offset").GetInt32());
        Assert.NotEqual(
            first.GetProperty("items")[0].GetProperty("symbolId").GetString(),
            second.GetProperty("items")[0].GetProperty("symbolId").GetString());
    }

    /// <summary>
    /// A named type has no call edges of its own, so the edge walk alone answered "how much does changing
    /// this ripple" with a blast radius of 1 however many members referenced it.
    /// </summary>
    [Fact]
    public async Task GetCallHierarchy_NamedTypeRoot_CountsTheMembersThatReferenceIt()
    {
        var referencing = Root(await GetReferences("Sample.Lib.IWidget", "callers"))
            .GetProperty("totalItems").GetInt32();
        Assert.True(referencing >= 2);

        var root = Root(await FlowTools.GetCallHierarchy(
            _f.Workspace, _f.Symbols, _f.Index, _f.Builder, _f.Telemetry, "Sample.Lib.IWidget",
            maxDepth: 1, includeTree: false));

        var perDepth = root.GetProperty("blastRadius").GetProperty("perDepth")
            .EnumerateArray().Select(d => d.GetInt32()).ToList();

        Assert.Equal(1, perDepth[0]);
        Assert.Equal(referencing, perDepth[1]);
    }

    /// <summary>
    /// System.Object's members are in scope on every receiver, so they are never what a cursor is deciding
    /// between. Grouped by origin alone they are all "inherited" and took a full round-robin share of the
    /// budget — 6 of 15 rows on the specimen that found this.
    /// </summary>
    [Fact]
    public async Task GetScope_ObjectMembersAreAReserve_SpentOnlyWhenNothingElseWaits()
    {
        // string overrides Equals/GetHashCode/ToString, so GetType/ReferenceEquals are the only rows
        // System.Object still declares here -- exactly the ones that must not take a slot while
        // string's own members are waiting for one. Ordering them last does not achieve that: the
        // budget's round-robin hands every group it walks a slot per round whatever its position.
        var narrow = Root(await FlowTools.GetScope(_f.Workspace, _f.Locator, _f.Telemetry,
            file: "Lib/BodyOutlineFixture.cs", line: 36, column: 20, receiver: "result",
            filter: "methods", limit: 5));

        var narrowItems = Names(narrow);
        Assert.Equal(5, narrowItems.Count);
        Assert.DoesNotContain(narrowItems, i => i.Contains("GetType(", StringComparison.Ordinal));
        Assert.DoesNotContain(narrowItems, i => i.Contains("ReferenceEquals(", StringComparison.Ordinal));

        // Reserved, not dropped: on a receiver whose whole surface fits the budget they are still
        // listed, and listed last. A wide limit on `result` would not show this -- string has more
        // than the 200 rows limit clamps to, so object's members stay cut off however wide the ask.
        var wide = Root(await FlowTools.GetScope(_f.Workspace, _f.Locator, _f.Telemetry,
            file: "Lib/Pipeline.cs", line: 29, column: 40, receiver: "_widget",
            filter: "methods", limit: 40));

        var wideItems = Names(wide);
        Assert.Contains(wideItems, IsObjectMember);
        var firstObject = wideItems.FindIndex(IsObjectMember);
        Assert.True(firstObject > 0, "the receiver's own members should come before object's");
        Assert.All(wideItems.Skip(firstObject), i =>
            Assert.True(IsObjectMember(i), $"non-object row after the object block: {i}"));

        static List<string> Names(JsonElement root) => root.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("displayString").GetString() ?? "").ToList();

        static bool IsObjectMember(string display) =>
            display.Contains("Equals(", StringComparison.Ordinal)
            || display.Contains("GetHashCode(", StringComparison.Ordinal)
            || display.Contains("GetType(", StringComparison.Ordinal)
            || display.Contains("ReferenceEquals(", StringComparison.Ordinal)
            || display.Contains("ToString(", StringComparison.Ordinal);
    }

    /// <summary>
    /// Slice nodes render compactly by default and carry full signatures only on request — the full form
    /// spent about a third of a 4-node slice's tokens on parameter lists symbolId already disambiguates.
    /// </summary>
    [Fact]
    public async Task GetCallSlice_RendersCompactlyUnlessSignatureRequested()
    {
        var compact = Root(await ContextToolsCallSlice("Sample.Lib.Pipeline.Start", "Sample.Lib.Widget.Spin"))
            .GetProperty("path").EnumerateArray()
            .Select(n => n.GetProperty("displayString").GetString() ?? "").ToList();
        Assert.All(compact, d => Assert.DoesNotContain("(", d));

        var signature = Root(await FlowTools.GetCallSlice(
                _f.Workspace, _f.Symbols, _f.CallSlice, _f.Builder, _f.Telemetry,
                "Sample.Lib.Pipeline.Start", "Sample.Lib.Widget.Spin", fields: "signature"))
            .GetProperty("path").EnumerateArray()
            .Select(n => n.GetProperty("displayString").GetString() ?? "").ToList();
        Assert.Contains(signature, d => d.Contains("(", StringComparison.Ordinal));
    }

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
    /// Listing both parts under declarationSites is not the same as serving both under source, and until
    /// contract 3.63 it was not: the response named two files and returned one part's text. Every part
    /// must come back, each run tagged with the file it is in — a bare line number cannot identify a
    /// place once both parts have their own line 5.
    /// </summary>
    /// <remarks>
    /// The test above passed throughout that bug, which is why this one asserts on the source array
    /// rather than on the site count: the two fields disagreeing was the defect, so only a check that
    /// reads them together can see it.
    /// </remarks>
    [Fact]
    public async Task GetSymbol_PartialClass_SourceCarriesEveryPartTaggedByFile()
    {
        var content = Root(await GetSymbol("Sample.Lib.Gadget", source: "full")).GetProperty("content");
        var runs = content.GetProperty("source").EnumerateArray().ToList();

        var spans = runs.Select(r => r.GetProperty("lines").GetString()!).ToList();
        Assert.Contains(spans, s => s.Contains("Gadget.cs:", StringComparison.Ordinal));
        Assert.Contains(spans, s => s.Contains("Gadget.Extra.cs:", StringComparison.Ordinal));

        var text = string.Join("\n", runs.SelectMany(r =>
            r.GetProperty("text").EnumerateArray().Select(t => t.GetString())));
        Assert.Contains("Left()", text, StringComparison.Ordinal);
        Assert.Contains("Right()", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Compact is imposed on a multi-file partial even against an explicit -exact, since the per-line
    /// gutter has nowhere to put the file. sourceLineFormat is what tells the caller its request lost.
    /// </summary>
    [Fact]
    public async Task GetSymbol_PartialClass_ForcesSpansOverAnExplicitExact()
    {
        var content = Root(await GetSymbol("Sample.Lib.Gadget", source: "full-exact")).GetProperty("content");

        Assert.Equal("compact", content.GetProperty("sourceLineFormat").GetString());
        Assert.All(content.GetProperty("source").EnumerateArray(), r => Assert.True(r.TryGetProperty("lines", out _)));
    }

        /// <summary>
        /// Widget.Spin has a /// doc comment on the line directly above its signature. declarationSites and
        /// source must both start AT the comment, not at the signature — otherwise a validate_patch edit
        /// built from declarationSites' own line span has no way to touch the comment at all.
        /// </summary>
        [Fact]
        public async Task GetSymbol_DeclarationSpanIncludesTheLeadingDocComment()
        {
            var root = Root(await GetSymbol("Sample.Lib.Widget.Spin", source: "full-exact"));
            var content = root.GetProperty("content");
            var site = content.GetProperty("declarationSites")[0];

            var startLine = site.GetProperty("startLine").GetInt32();
            var fileLines = await File.ReadAllLinesAsync(_f.Locator.AbsPath(site.GetProperty("file").GetString()!));
            Assert.Contains("///", fileLines[startLine - 1]);

            // source reads exactly as the file does, no header line prepended — the doc comment is the
            // first line, leading indentation included (not just its own text) — a declaration's first
            // extracted line silently lost its indentation once before, since the span's own start point
            // sits on the first non-trivia character rather than that line's true start.
            var sourceLines = content.GetProperty("source");
            Assert.Contains("/// <summary>", sourceLines[0].GetProperty("text").GetString());
            Assert.Equal(startLine, sourceLines[0].GetProperty("line").GetInt32());
            Assert.Equal(fileLines[startLine - 1], sourceLines[0].GetProperty("text").GetString());
        }

        /// <summary>
        /// source:code renders the same declaration minus its leading doc comment — the signature line
        /// itself, not the comment above it, is where source:code's own span starts.
        /// </summary>
        [Fact]
        public async Task GetSymbol_SourceCode_ExcludesLeadingDocComment()
        {
            var full = Root(await GetSymbol("Sample.Lib.Widget.Spin", source: "full-exact"));
            var code = Root(await GetSymbol("Sample.Lib.Widget.Spin", source: "code-exact"));

            var fullFirstLine = full.GetProperty("content").GetProperty("source")[0];
            var codeSource = code.GetProperty("content").GetProperty("source");

            Assert.Contains("/// <summary>", fullFirstLine.GetProperty("text").GetString());
            Assert.DoesNotContain(
                codeSource.EnumerateArray(),
                line => line.GetProperty("text").GetString()!.TrimStart().StartsWith("///"));
            Assert.True(codeSource[0].GetProperty("line").GetInt32() > fullFirstLine.GetProperty("line").GetInt32());
        }

        /// <summary>
        /// Every rendered line carries its real indentation, the FIRST one included.
        /// </summary>
        /// <remarks>
        /// The span opens at the declaration's first token, which sits after the leading whitespace — so the
        /// first line used to arrive flush-left while every line beneath it kept its indent. That is not
        /// cosmetic: this tool tells callers to reconstruct a declaration line from this text and hand it
        /// back as a validate_patch edit, and the reconstructed line was misindented.
        /// </remarks>
        [Fact]
        public async Task GetSymbol_Source_FirstLineKeepsItsIndentation()
        {
        foreach (var spec in new[] { "full-exact", "code-exact" })
        {
            var lines = Root(await GetSymbol("Sample.Lib.Widget.Spin", source: spec))
                    .GetProperty("content").GetProperty("source");

                var first = lines[0].GetProperty("text").GetString()!;
                Assert.NotEqual(first, first.TrimStart());
            }
        }

        /// <summary>
        /// source:code on a whole type also strips each MEMBER's own doc comment, not just the type's own —
        /// otherwise a type-level fetch would still carry every member's /// block untouched.
        /// </summary>
        [Fact]
        public async Task GetSymbol_SourceCode_ExcludesMemberLevelDocCommentsToo()
        {
            var full = Root(await GetSymbol("Sample.Lib.Widget", source: "full-exact"));
            var code = Root(await GetSymbol("Sample.Lib.Widget", source: "code-exact"));

            var fullSource = full.GetProperty("content").GetProperty("source");
            var codeSource = code.GetProperty("content").GetProperty("source");

            Assert.Contains(
                fullSource.EnumerateArray(),
                line => line.GetProperty("text").GetString()!.Contains("Spins the widget"));

            Assert.DoesNotContain(
                codeSource.EnumerateArray(),
                line => line.GetProperty("text").GetString()!.TrimStart().StartsWith("///"));
            Assert.Contains(
                codeSource.EnumerateArray(),
                line => line.GetProperty("text").GetString()!.Contains("public int Spin(int turns)"));
        }

    /// <summary>A -tag modifier drops only that doc-comment tag, leaving the rest of the comment intact.</summary>
    [Fact]
    public async Task GetSymbol_SourceFullMinusRemarks_KeepsReturnsDropsRemarks()
    {
        var root = Root(await GetSymbol("Sample.Lib.DocSectionsFixture.Full", source: "full-remarks"));
        var lines = root.GetProperty("content").GetProperty("source");

        Assert.Contains(lines.EnumerateArray(), l => l.GetProperty("text").GetString()!.Contains("Always zero"));
        Assert.DoesNotContain(lines.EnumerateArray(), l => l.GetProperty("text").GetString()!.Contains("returns and remarks"));
    }

        /// <summary>
        /// -attributes drops an attribute that occupies its own whole line, but leaves one sharing a line
        /// with real code alone — fetching the whole type (not one member) is what makes both cases visible
        /// in a single response.
        /// </summary>
        [Fact]
        public async Task GetSymbol_SourceFullMinusAttributes_DropsWholeLineButKeepsInlineAttribute()
        {
            var root = Root(await GetSymbol("Sample.Lib.SourceQueryFixture", source: "full-attributes-exact"));
            var lines = root.GetProperty("content").GetProperty("source").EnumerateArray().ToArray();

            Assert.Single(lines, l => l.GetProperty("text").GetString()!.Contains("[Obsolete]"));
            Assert.Contains(lines, l => l.GetProperty("text").GetString()!.Contains("standalone comment"));
            Assert.Contains(lines, l => l.GetProperty("text").GetString()!.Contains("WithOwnLineAttribute"));
            Assert.Contains(lines, l => l.GetProperty("text").GetString()!.Contains("WithInlineAttribute"));
        }

    /// <summary>-attributes strips attributes under source:code too, not just source:full.</summary>
    [Fact]
    public async Task GetSymbol_SourceCodeMinusAttributes_DropsAttributesInCodeModeToo()
    {
        var root = Root(await GetSymbol("Sample.Lib.SourceQueryFixture.WithOwnLineAttribute", source: "code-attributes"));
        var lines = root.GetProperty("content").GetProperty("source");

        Assert.DoesNotContain(lines.EnumerateArray(), l => l.GetProperty("text").GetString()!.Contains("[Obsolete]"));
        Assert.Contains(lines.EnumerateArray(), l => l.GetProperty("text").GetString()!.Contains("WithOwnLineAttribute"));
    }

        /// <summary>
        /// -comments drops a standalone // line but leaves a trailing // comment sharing a line with code
        /// alone — same whole-type framing as the attributes test above.
        /// </summary>
        [Fact]
        public async Task GetSymbol_SourceFullMinusComments_DropsStandaloneButKeepsTrailingComment()
        {
            var root = Root(await GetSymbol("Sample.Lib.SourceQueryFixture", source: "full-comments-exact"));
            var lines = root.GetProperty("content").GetProperty("source").EnumerateArray().ToArray();

            Assert.DoesNotContain(lines, l => l.GetProperty("text").GetString()!.Contains("standalone comment"));
            Assert.Contains(lines, l => l.GetProperty("text").GetString()!.Contains("trailing comment"));
            Assert.Equal(2, lines.Count(l => l.GetProperty("text").GetString()!.Contains("[Obsolete]")));
        }

        /// <summary>
        /// An attribute or // comment sharing a line with real code is left untouched even when both
        /// -attributes and -comments are requested — whole-line removal only, never a partial-line rewrite.
        /// </summary>
        [Fact]
        public async Task GetSymbol_SourceFullMinusAttributesMinusComments_InlineContentSurvivesBoth()
        {
            var root = Root(await GetSymbol("Sample.Lib.SourceQueryFixture", source: "full-attributes-comments-exact"));
            var lines = root.GetProperty("content").GetProperty("source").EnumerateArray().ToArray();

            Assert.DoesNotContain(lines, l => l.GetProperty("text").GetString()!.Contains("standalone comment"));
            Assert.Single(lines, l => l.GetProperty("text").GetString()!.Contains("[Obsolete]"));
            Assert.Contains(lines, l => l.GetProperty("text").GetString()!.Contains("trailing comment"));
        }

    /// <summary>A doc-tag modifier under code is always redundant (code already excludes every tag) and rejected.</summary>
    [Fact]
    public async Task GetSymbol_SourceCodeMinusRemarks_IsInvalidComponent()
    {
        var root = Root(await GetSymbol("Sample.Lib.DocSectionsFixture.Full", source: "code-remarks"));

        Assert.Equal("invalid_component", root.GetProperty("error").GetString());
        Assert.Contains("source:code-remarks", root.GetProperty("detail").GetString());
    }

    /// <summary>An unrecognized modifier name is rejected the same way an unrecognized component is.</summary>
    [Fact]
    public async Task GetSymbol_SourceFullBogusModifier_IsInvalidComponent()
    {
        var root = Root(await GetSymbol("Sample.Lib.DocSectionsFixture.Full", source: "full-bogus"));

        Assert.Equal("invalid_component", root.GetProperty("error").GetString());
        Assert.Contains("source:full-bogus", root.GetProperty("detail").GetString());
    }

    /// <summary>An unrecognized source suffix is rejected the same way an unrecognized component is.</summary>
    [Fact]
    public async Task GetSymbol_SourceBadSuffix_IsInvalidComponent()
    {
        var root = Root(await GetSymbol("Sample.Lib.Widget.Spin", source: "bogus"));

        Assert.Equal("invalid_component", root.GetProperty("error").GetString());
        Assert.Contains("source:bogus", root.GetProperty("detail").GetString());
    }

        /// <summary>
        /// An @ selector narrows source to the named absolute file lines, and reports the kept span against
        /// the declaration's whole span so the caller can see it is holding a fragment.
        /// </summary>
        [Fact]
        public async Task GetSymbol_SourceLineRange_ReturnsOnlyThoseLines()
        {
            var content = Root(await GetSymbol("Sample.Lib.SourceQueryFixture", source: "full-exact@9-10")).GetProperty("content");

            Assert.Equal(new[] { 9, 10 }, SourceLineNumbers(content));
            Assert.Equal("9-10/5-13", content.GetProperty("sourceLines").GetString());
        }

    /// <summary>
    /// A slice usually cuts the signature line out, so displayString/modifiers — suppressed alongside a
    /// whole source — come back rather than leaving a fragment that never says what it belongs to.
    /// </summary>
    [Fact]
    public async Task GetSymbol_SlicedSource_RestoresDisplayStringAndModifiers()
    {
        var whole = Root(await GetSymbol("Sample.Lib.SourceQueryFixture", include: "source")).GetProperty("content");
        var sliced = Root(await GetSymbol("Sample.Lib.SourceQueryFixture", source: "@9-10")).GetProperty("content");

        Assert.True(IsAbsentOrNull(whole, "displayString"));
        Assert.True(IsAbsentOrNull(whole, "modifiers"));
        Assert.Equal("SourceQueryFixture", sliced.GetProperty("displayString").GetString());
        Assert.Contains("static", sliced.GetProperty("modifiers").GetString());
    }

        /// <summary>
        /// A range and a -modifier exclusion are both filters over the same absolute line numbers, so a line
        /// the exclusion dropped stays dropped even when a range names it.
        /// </summary>
        [Fact]
        public async Task GetSymbol_LineRange_ComposesWithModifierExclusions()
        {
            var content = Root(await GetSymbol("Sample.Lib.SourceQueryFixture", source: "code-exact@5-13")).GetProperty("content");

            // Line 5 is the type's doc comment, which source:code already removed.
            Assert.DoesNotContain(5, SourceLineNumbers(content));
            Assert.Equal("6-13/6-13", content.GetProperty("sourceLines").GetString());
        }

    /// <summary>
    /// -lineNumbers replaces the per-line gutter with one span entry per contiguous run, so an
    /// unbroken declaration comes back as a single span of bare text carrying no line property.
    /// </summary>
    [Fact]
    public async Task GetSymbol_SourceCodeMinusLineNumbers_ReturnsOneSpanOfBareText()
    {
        var content = Root(await GetSymbol("Sample.Lib.SourceQueryFixture", source: "code-lineNumbers")).GetProperty("content");
        var spans = content.GetProperty("source").EnumerateArray().ToArray();

        var span = Assert.Single(spans);
        Assert.Equal("6-13", span.GetProperty("lines").GetString());
        Assert.False(span.TryGetProperty("line", out _));
        Assert.Contains(span.GetProperty("text").EnumerateArray(), t => t.GetString()!.Contains("WithOwnLineAttribute"));
    }

    /// <summary>
    /// The default (Automatic) format renders as compact @start-end spans when that comes out shorter
    /// than the numbered gutter, which is normal for an unmodified declaration of any real size.
    /// </summary>
    [Fact]
    public async Task GetSymbol_DefaultFormat_IsAutomaticAndPicksCompactWhenShorter()
    {
        var content = Root(await GetSymbol("Sample.Lib.SourceQueryFixture", "source")).GetProperty("content");
        var span = Assert.Single(content.GetProperty("source").EnumerateArray());

        Assert.True(span.TryGetProperty("lines", out _));
        Assert.False(span.TryGetProperty("line", out _));
        Assert.Equal("compact", content.GetProperty("sourceLineFormat").GetString());
    }

        /// <summary>
        /// sourceLineFormat is only reported when Automatic had to choose — an explicit -exact/-compact
        /// (or its deprecated -lineNumbers alias) already told the caller what it would get, so restating
        /// it would be pure duplication.
        /// </summary>
        [Fact]
        public async Task GetSymbol_SourceLineFormat_AbsentWhenExplicitlyForced()
        {
            var compact = Root(await GetSymbol("Sample.Lib.SourceQueryFixture", source: "full-lineNumbers")).GetProperty("content");
            var exact = Root(await GetSymbol("Sample.Lib.SourceQueryFixture", source: "full-exact")).GetProperty("content");

            Assert.False(compact.TryGetProperty("sourceLineFormat", out _));
            Assert.False(exact.TryGetProperty("sourceLineFormat", out _));
        }

        /// <summary>-exact forces the numbered gutter even when Automatic would have picked the compact spans.</summary>
        [Fact]
        public async Task GetSymbol_SourceMinusExact_ForcesTheNumberedGutterEvenWhenCompactIsShorter()
        {
            var content = Root(await GetSymbol("Sample.Lib.SourceQueryFixture", source: "full-exact")).GetProperty("content");

            Assert.All(content.GetProperty("source").EnumerateArray(), l => Assert.True(l.TryGetProperty("line", out _)));
        }

        /// <summary>-exact and -compact contradict each other, so together they are rejected.</summary>
        [Fact]
        public async Task GetSymbol_SourceMinusExactMinusCompact_IsInvalidComponent()
        {
            var root = Root(await GetSymbol("Sample.Lib.Widget.Spin", source: "full-exact-compact"));

            Assert.Equal("invalid_component", root.GetProperty("error").GetString());
            Assert.Contains("source:full-exact-compact", root.GetProperty("detail").GetString());
        }

    /// <summary>
    /// A line an exclusion dropped breaks the declaration into two runs, and each run states its own
    /// absolute span — so bare text never reads as contiguous across a gap that is really there.
    /// </summary>
    [Fact]
    public async Task GetSymbol_MinusCommentsMinusLineNumbers_SplitsRunsAtTheDroppedLine()
    {
        var content = Root(await GetSymbol("Sample.Lib.SourceQueryFixture", source: "full-comments-lineNumbers")).GetProperty("content");
        var spans = content.GetProperty("source").EnumerateArray().ToArray();

        Assert.Equal(["5-7", "9-13"], spans.Select(s => s.GetProperty("lines").GetString()));
        Assert.DoesNotContain(
            spans.SelectMany(s => s.GetProperty("text").EnumerateArray()),
            t => t.GetString()!.Contains("standalone comment"));
    }

    /// <summary>Disjoint ranges are separated by ';', since ',' already separates include's components.</summary>
    /// <remarks>
    /// Forces -exact because the assertion reads per-line numbers, and this test is about the ';' grammar
    /// rather than about which rendering Automatic picks -- on a selection this small the compact spans
    /// are genuinely shorter, so leaving it to Automatic would couple the grammar to that arbitration.
    /// </remarks>
    [Fact]
    public async Task GetSymbol_SeveralLineRanges_AreSemicolonSeparated()
    {
        var content = Root(await GetSymbol("Sample.Lib.SourceQueryFixture", source: "full-exact@6;9-10")).GetProperty("content");

        Assert.Equal(new[] { 6, 9, 10 }, SourceLineNumbers(content));
    }

        /// <summary>An open-ended range clamps to the declaration rather than erroring or running past it.</summary>
        [Fact]
        public async Task GetSymbol_OpenEndedLineRange_ClampsToTheDeclaration()
        {
            var content = Root(await GetSymbol("Sample.Lib.SourceQueryFixture", source: "full-exact@10-")).GetProperty("content");

            Assert.Equal(new[] { 10, 11, 12, 13 }, SourceLineNumbers(content));
            Assert.Equal("10-13/5-13", content.GetProperty("sourceLines").GetString());
        }

    /// <summary>
    /// A range missing the declaration entirely yields no lines rather than an error — sourceLines states
    /// both that nothing was kept and which span would have worked.
    /// </summary>
    [Fact]
    public async Task GetSymbol_LineRangeOutsideDeclaration_ReturnsNoLinesAndNamesTheRealSpan()
    {
        var content = Root(await GetSymbol("Sample.Lib.SourceQueryFixture", source: "@900-910")).GetProperty("content");

        Assert.Empty(content.GetProperty("source").EnumerateArray());
        Assert.Equal("none/5-13", content.GetProperty("sourceLines").GetString());
    }

    /// <summary>sourceLines is a slice-only field — an unsliced source pays nothing for it.</summary>
    [Fact]
    public async Task GetSymbol_UnslicedSource_OmitsSourceLines()
    {
        var content = Root(await GetSymbol("Sample.Lib.SourceQueryFixture", include: "source")).GetProperty("content");

        Assert.True(IsAbsentOrNull(content, "sourceLines"));
    }

    /// <summary>A malformed range is rejected the same way an unrecognized modifier is.</summary>
    [Fact]
    public async Task GetSymbol_MalformedLineRange_IsInvalidComponent()
    {
        var root = Root(await GetSymbol("Sample.Lib.SourceQueryFixture", source: "@nope"));

        Assert.Equal("invalid_component", root.GetProperty("error").GetString());
        Assert.Contains("source:@nope", root.GetProperty("detail").GetString());
    }

    /// <summary>
    /// One include applies to every batch entry, but a line span belongs to one symbol's own file — so
    /// the combination is rejected instead of slicing each symbol by another's line numbers.
    /// </summary>
    [Fact]
    public async Task GetSymbol_LineRangeWithBatch_IsRejected()
    {
        var root = Root(await GetSymbols(["Sample.Lib.Widget.Spin", "Sample.Lib.SourceQueryFixture"], source: "@9-10"));

        Assert.Equal("lines_with_batch", root.GetProperty("error").GetString());
    }

    /// <summary>
    /// bodyOutline emits one row per control-flow landmark with text, span, and nesting depth among other
    /// landmarks; anonymous try/finally are omitted since their span is inferable from neighboring rows.
    /// </summary>
    [Fact]
    public async Task GetSymbol_BodyOutline_ReturnsControlFlowLandmarks()
    {
        var content = Root(await GetSymbol("Sample.Lib.BodyOutlineFixture.Classify", include: "bodyOutline")).GetProperty("content");
        var rows = content.GetProperty("bodyOutline").EnumerateArray().ToArray();

        Assert.Equal(7, rows.Length);
        Assert.Equal(("switch(node)", 13, 24, 0), Row(rows[0]));
        Assert.Equal(("case int", 15, 17, 1), Row(rows[1]));
        Assert.Equal(("case int", 18, 20, 1), Row(rows[2]));
        Assert.Equal(("case default", 21, 23, 1), Row(rows[3]));
        Assert.Equal(("foreach(name)", 26, 32, 0), Row(rows[4]));
        Assert.Equal(("if (name.Length > 3)", 28, 31, 1), Row(rows[5]));
        Assert.Equal(("catch(InvalidOperationException e..", 38, 41, 0), Row(rows[6]));
        Assert.True(IsAbsentOrNull(content, "bodyOutlineNote"));
    }

    /// <summary>
    /// A declaration short enough that fetching source directly is likely cheaper gets an advisory note
    /// alongside its (possibly empty) rows, never an error or a substituted component.
    /// </summary>
    [Fact]
    public async Task GetSymbol_BodyOutline_NotesShortDeclarationRatherThanDegrading()
    {
        var content = Root(await GetSymbol("Sample.Lib.BodyOutlineFixture.TooShortForOutline", include: "bodyOutline")).GetProperty("content");

        Assert.Empty(content.GetProperty("bodyOutline").EnumerateArray());
        Assert.Contains("2 lines", content.GetProperty("bodyOutlineNote").GetString());
    }

    /// <summary>
    /// Length alone does not make a body worth outlining: a declaration past the worthwhile-line threshold
    /// whose outline holds almost no landmarks is warned about on DENSITY, so the caller does not pay for
    /// the outline and then the source fetch it needed anyway.
    /// </summary>
    [Fact]
    public async Task GetSymbol_BodyOutline_NotesASparseOutlineOverALongBody()
    {
        var content = Root(await GetSymbol("Sample.Lib.BodyOutlineFixture.LongButLinear", include: "bodyOutline")).GetProperty("content");

        Assert.Single(content.GetProperty("bodyOutline").EnumerateArray());

        var note = content.GetProperty("bodyOutlineNote").GetString();
        Assert.Contains("only 1 entry", note, StringComparison.Ordinal);
        Assert.Contains("mostly linear", note, StringComparison.Ordinal);
        Assert.DoesNotContain("is likely cheaper than this outline", note, StringComparison.Ordinal);
    }

    /// <summary>bodyOutline is method-only, like mechanicalFacts's semantic-model facts — a type gets an
    /// explanatory bodyOutlineNote instead of both fields silently disappearing.</summary>
    [Fact]
    public async Task GetSymbol_BodyOutline_NotesNonMethodSymbol()
    {
        var content = Root(await GetSymbol("Sample.Lib.BodyOutlineFixture", include: "bodyOutline")).GetProperty("content");

        Assert.True(IsAbsentOrNull(content, "bodyOutline"));
        Assert.Contains("not applicable", content.GetProperty("bodyOutlineNote").GetString());
    }

    private static int[] SourceLineNumbers(JsonElement content) =>
        [.. content.GetProperty("source").EnumerateArray().Select(l => l.GetProperty("line").GetInt32())];

    private static bool IsAbsentOrNull(JsonElement element, string property) =>
        !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null;

    private static (string Text, int StartLine, int EndLine, int Depth) Row(JsonElement row) =>
        (row.GetProperty("text").GetString()!, row.GetProperty("startLine").GetInt32(),
         row.GetProperty("endLine").GetInt32(), row.GetProperty("depth").GetInt32());


    [Fact]
    public async Task GetReferences_DirectionTypoOfImplementations_FallsBackToCallersWithHint()
        {
            var root = Root(await GetReferences("Sample.Lib.IWidget", "implementaton"));

            var hint = root.GetProperty("directionHint").GetString();
            Assert.Contains("implementations", hint, StringComparison.Ordinal);
        }

        [Fact]
        public async Task GetReferences_DirectionCallers_CarriesNoHint()
        {
            var root = Root(await GetReferences("Sample.Lib.Widget.Spin", "callers"));

            Assert.False(root.TryGetProperty("directionHint", out _));
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

        var edits = new[] { new PatchEditInput(File: "Lib/Widget.cs", Lines: "12-12", NewText: "    public int Spin(int turns, int extra) => turns * 2 + extra;") };
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
        // This apply reaches DISK, and every test in this class shares one fixture copy. Being the only test
        // that EDITS Widget.Spin is not isolation -- ValidatePatch_FindReplace_* search its body for
        // "turns * 2", so they break on whichever ordering runs them after this. Undone in the finally.
        var sym = Root(await GetSymbol("Sample.Lib.Widget.Spin", "all"));
        var symbolId = sym.GetProperty("symbolId").GetString()!;
        var version = sym.GetProperty("contentVersion").GetString()!;
        // Scoped by symbolId rather than a unique taskId (task ids are no longer a caller-facing
        // concept - every call in this process shares one ambient session id).
        var before = _f.FeatureLog.RecentForSymbolWithChain(symbolId, 50).Count;

        var edits = new[] { new PatchEditInput(File: "Lib/Widget.cs", Lines: "12-12", NewText: "    public int Spin(int turns) => turns * 3;") };
        var root = Root(await PatchTools.ValidatePatch(_f.Workspace, _f.Locator, _f.Symbols, _f.FeatureLog, _f.Builder, _f.TargetedTests, _f.Telemetry,
            new PatchDraftStore(TimeProvider.System),
            new Dictionary<string, string> { [symbolId] = version }, edits,
            requestedLevel: null, applyOnSuccess: true, intent: "tune spin factor", tags: null));

        try
        {
            Assert.True(root.GetProperty("ladder").GetProperty("isSufficient").GetBoolean(), root.GetRawText());
            Assert.True(root.GetProperty("applied").GetBoolean(), root.GetRawText());

            var after = _f.FeatureLog.RecentForSymbolWithChain(symbolId, 50).Count;
            Assert.Equal(before + 1, after);   // exactly one feature_log row logged for this symbol
        }
        finally
        {
            await RestoreSpinBodyAsync();
        }
    }

    [Fact]
    public async Task RenameSymbol_InterfaceMember_DryRunReachesImplementersAndCrossProjectCallers()
    {
        var sym = Root(await GetSymbol("Sample.Lib.IWidget.Spin"));
        var root = Root(await RenameSymbolCall(
            sym.GetProperty("symbolId").GetString()!, "Rotate",
            sym.GetProperty("contentVersion").GetString()!));

        Assert.True(root.GetProperty("succeeded").GetBoolean(), root.GetRawText());
        Assert.False(root.GetProperty("applied").GetBoolean());   // dry run: nothing reaches disk

        var files = RenamedFiles(root);
        // The declaration and both implementers live in Widget.cs; the only call site is in a DIFFERENT
        // project, reached through interface dispatch — precisely what a text search cannot resolve.
        Assert.Contains(files.Keys, f => f.EndsWith("Lib/Widget.cs", StringComparison.Ordinal));
        Assert.Contains(files.Keys, f => f.EndsWith("App/Program.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenameSymbol_RenameInComments_EditsWhatTheDefaultLeavesAlone()
    {
        var sym = Root(await GetSymbol("Sample.Lib.IWidget.Spin"));
        var symbolId = sym.GetProperty("symbolId").GetString()!;
        var version = sym.GetProperty("contentVersion").GetString()!;

        // Program.cs carries the name in a comment and a string literal as well as in the call. Both
        // extra occurrences are textual guesses, so they must stay opt-in: the default run has to leave
        // them alone, and asking for comments has to actually reach one.
        var byDefault = RenamedFiles(Root(await RenameSymbolCall(symbolId, "Rotate", version)));
        var withComments = RenamedFiles(Root(await RenameSymbolCall(symbolId, "Rotate", version, renameInComments: true)));

        var program = byDefault.Keys.Single(f => f.EndsWith("App/Program.cs", StringComparison.Ordinal));
        Assert.True(withComments[program] > byDefault[program],
            $"renameInComments should add edits in Program.cs: {byDefault[program]} -> {withComments[program]}");
    }

    [Fact]
    public async Task RenameSymbol_AppliesRewritesEveryReferenceAndLogsOnce()
    {
        // This apply reaches DISK, and every test in this class shares one fixture copy, so the target has to
        // be one nothing else reads: RenameApplySample exists only for this test. Aimed at RenameSample, it
        // broke whichever sibling xUnit scheduled next -- an order that moves whenever the assembly changes,
        // so the failure looked unrelated and landed on a different test each build.
        var sym = Root(await GetSymbol("Sample.Lib.RenameApplySample.Seed"));
        var root = Root(await RenameSymbolCall(
            sym.GetProperty("symbolId").GetString()!, "Origin",
            sym.GetProperty("contentVersion").GetString()!,
            applyOnSuccess: true, intent: "rename the rename fixture's seed accessor"));

        Assert.True(root.GetProperty("succeeded").GetBoolean(), root.GetRawText());
        Assert.True(root.GetProperty("applied").GetBoolean(), root.GetRawText());

        var rename = root.GetProperty("rename");
        Assert.Equal("Origin", rename.GetProperty("newName").GetString());
        Assert.Equal(1, rename.GetProperty("filesChanged").GetInt32());
        // The declaration plus every reference: Doubled(), RenameApplySampleUser.Use, and the <see cref="Seed"/>.
        Assert.True(rename.GetProperty("occurrencesRewritten").GetInt32() >= 3, root.GetRawText());

        // The apply is only half the point — a rename that reaches disk without a development-log entry
        // is reasoning search_log can never recover.
        var newSymbolId = rename.GetProperty("newSymbolId").GetString()!;
        Assert.NotEmpty(_f.FeatureLog.RecentForSymbolWithChain(newSymbolId, 50));
    }

    [Fact]
    public async Task RenameSymbol_RenamingAType_ReportsANewSymbolIdAndTheFileRenameHint()
    {
        var sym = Root(await GetSymbol("Sample.Lib.RenameSampleUser"));
        var oldSymbolId = sym.GetProperty("symbolId").GetString()!;
        var root = Root(await RenameSymbolCall(
            oldSymbolId, "RenameSampleConsumer", sym.GetProperty("contentVersion").GetString()!));

        Assert.True(root.GetProperty("succeeded").GetBoolean(), root.GetRawText());

        // ChangeClassifier pairs a renamed METHOD by its name-stripped signature but has no such key for a
        // type, so a renamed type arrives as an unpaired removed+added pair whose removed half still
        // carries the old id. Deriving newSymbolId from the old id therefore echoed the old id straight
        // back, and the method-rename test above could never catch it.
        var rename = root.GetProperty("rename");
        Assert.Equal("Type", rename.GetProperty("kind").GetString());
        Assert.NotEqual(oldSymbolId, rename.GetProperty("newSymbolId").GetString());
    }

    /// <summary>
    /// A symbolId encodes its containing type, so renaming a type re-keys every member it declares and the
    /// classifier reports each as a removed+added pair tagged breaking-public — for members whose own names
    /// never changed. On an 8-member type that was 16 of 24 entries and ~65% of the response.
    /// </summary>
    [Fact]
    public async Task RenameSymbol_TypeRename_CollapsesItsMembersMechanicalRekeys()
    {
        var sym = Root(await GetSymbol("Sample.Lib.RenameSample"));
        var root = Root(await RenameSymbolCall(
            sym.GetProperty("symbolId").GetString()!, "RenamedSample",
            sym.GetProperty("contentVersion").GetString()!));

        Assert.True(root.GetProperty("succeeded").GetBoolean(), root.GetRawText());

        // Seed and Doubled keep their own names; only their ids move. The TYPE's own removed+added pair is
        // not collapsed -- its name really did change -- so detectedChanges is not expected to be empty.
        Assert.Equal(2, root.GetProperty("membersRekeyed").GetInt32());
    }

    [Fact]
    public async Task RenameSymbol_CollidingMemberName_OmitsTheAliasedSymbolId()
    {
        var sym = Root(await GetSymbol("Sample.Lib.RenameSample.Seed"));
        var root = Root(await RenameSymbolCall(
            sym.GetProperty("symbolId").GetString()!, "Doubled",
            sym.GetProperty("contentVersion").GetString()!));

        Assert.False(root.GetProperty("succeeded").GetBoolean(), root.GetRawText());

        // Renaming Seed to Doubled collides with the pre-existing Doubled() of the same signature. A
        // symbol id is a hash of its fully-qualified name, so the rewritten symbol's id is IDENTICAL
        // to the pre-existing one it collided with - exposing it would silently point a caller at the
        // wrong member (self-eval finding, 2026-08-10).
        Assert.False(root.GetProperty("rename").TryGetProperty("newSymbolId", out _));
        foreach (var change in root.GetProperty("detectedChanges").EnumerateArray())
            Assert.False(change.TryGetProperty("symbolId", out _));
    }


    /// <summary>
    /// The tool's advice is to put every term in one call, so one call has to answer for each of them:
    /// each term takes a floor share of limit before the globally ranked union spends the remainder.
    /// </summary>
    [Fact]
    public async Task SearchIndex_MultiTermQuery_AnswersForEveryTermAtASmallLimit()
    {
        var root = Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry,
            query: "Widget Pipeline HighGear Overloads", limit: 4, groupBy: "none"));

        var names = root.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString() ?? "").ToList();

        // "Widget" alone matches Widget, IWidget, TurboWidget and WidgetExtensions - enough to take every
        // slot of a purely global ranked union and leave the other three terms unanswered.
        Assert.Contains(names, n => n.Contains("Widget", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("Pipeline", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("HighGear", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("Overloads", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenameSymbol_TypeNamedAfterItsFile_SaysTheFileWasNotRenamed()
    {
        var sym = Root(await GetSymbol("Sample.Lib.Widget"));
        var root = Root(await RenameSymbolCall(
            sym.GetProperty("symbolId").GetString()!, "Sprocket",
            sym.GetProperty("contentVersion").GetString()!));

        // Renaming the file is deliberately out of scope, so the response has to say so rather than leave
        // Widget.cs quietly holding a type called Sprocket.
        var hint = root.GetProperty("fileRenameHint").GetString()!;
        Assert.Contains("Lib/Widget.cs", hint, StringComparison.Ordinal);
        Assert.Contains("Sprocket.cs", hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenameSymbol_KeywordName_IsRejectedBeforeAnyValidation()
    {
        var sym = Root(await GetSymbol("Sample.Lib.RenameSample.Doubled"));
        var root = Root(await RenameSymbolCall(
            sym.GetProperty("symbolId").GetString()!, "class",
            sym.GetProperty("contentVersion").GetString()!));

        Assert.Equal("invalid_name", root.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RenameSymbol_RequestedLevelTypo_SurfacesUnhonoredHint()
        {
            var version = Root(await GetSymbol("Sample.Lib.BodyOutlineFixture.TooShortForOutline")).GetProperty("contentVersion").GetString()!;

            var root = Root(await RenameTools.RenameSymbol(_f.Workspace, _f.Locator, _f.Symbols, _f.FeatureLog, _f.Builder, _f.TargetedTests, _f.Telemetry,
                "Sample.Lib.BodyOutlineFixture.TooShortForOutline", "RenamedForHintTestOnly", version, applyOnSuccess: false, intent: null,
                renameOverloads: false, renameInComments: false, renameInStrings: false,
                requestedLevel: "solutionvalidate", tags: null, taskId: null));

            Assert.True(root.TryGetProperty("ladder", out var ladderEl), root.GetRawText());
                var hint = ladderEl.GetProperty("requestedLevelHint").GetString();
            Assert.Contains("solution_validate", hint, StringComparison.Ordinal);
        }

        [Fact]
        public async Task RenameSymbol_StaleBaseVersion_IsRejected()
    {
        var sym = Root(await GetSymbol("Sample.Lib.RenameSample.Doubled"));
        var root = Root(await RenameSymbolCall(
            sym.GetProperty("symbolId").GetString()!, "Trebled", "decl:deadbeefdead"));

        Assert.Equal("stale_base", root.GetProperty("error").GetString());
    }

    /// <summary>Relative file path -> edit count, from a rename response's files array.</summary>
    private static Dictionary<string, int> RenamedFiles(JsonElement root) =>
        root.GetProperty("files").EnumerateArray()
            .ToDictionary(f => f.GetProperty("file").GetString()!, f => f.GetProperty("occurrences").GetInt32(), StringComparer.Ordinal);

    private Task<string> RenameSymbolCall(
        string symbol, string newName, string? baseVersion, bool applyOnSuccess = false, string? intent = null,
        bool renameInComments = false) =>
        RenameTools.RenameSymbol(_f.Workspace, _f.Locator, _f.Symbols, _f.FeatureLog, _f.Builder, _f.TargetedTests, _f.Telemetry,
            symbol, newName, baseVersion, applyOnSuccess, intent,
            renameOverloads: false, renameInComments: renameInComments, renameInStrings: false,
            requestedLevel: null, tags: null, taskId: null);

    /// <summary>Puts Widget.Spin's body back to its fixture text after the one test that applies a change to it.</summary>
    /// <remarks>
    /// Every test in this class shares one fixture copy, and ValidatePatch_FindReplace_* search Widget.Spin's
    /// body for its original text. Unlike a rename, the symbol's NAME is unchanged by that apply, so the undo
    /// can resolve its target normally and go back through validate_patch -- which keeps disk, the MSBuild
    /// workspace and the symbol index in agreement, where rewriting the file alone would not.
    /// </remarks>

    /// <summary>Puts Widget.Spin's body back to its fixture text, for the same reason as <see cref="RestoreRenameAsync"/>.</summary>
    private async Task RestoreSpinBodyAsync()
    {
        var patched = Root(await GetSymbol("Sample.Lib.Widget.Spin", "all"));
        var restore = Root(await ContextToolsValidate(
            new Dictionary<string, string>
            {
                [patched.GetProperty("symbolId").GetString()!] = patched.GetProperty("contentVersion").GetString()!,
            },
            [new PatchEditInput(File: "Lib/Widget.cs", Lines: "12-12", NewText: "    public int Spin(int turns) => turns * 2;")],
            applyOnSuccess: true, intent: "restore the shared fixture for sibling tests"));

        Assert.True(restore.GetProperty("applied").GetBoolean(),
            $"fixture restore failed; every sibling test reading Widget.Spin will now fail too: {restore.GetRawText()}");
    }

    /// <summary>
    /// An identity edit (newText identical to what's already on disk) makes ChangeClassifier report no
    /// Change for the touched symbol, since nothing differs -- that used to mean the stale-base check
    /// (which only iterated detected changes) had nothing to compare a bogus baseVersions token against,
    /// so it silently passed. Now DetectAsync also reports the current version of every touched-but-
    /// unchanged symbol, so a bogus token is still caught even though the edit is a genuine no-op.
    /// </summary>
    [Fact]
    public async Task ValidatePatch_IdentityEdit_WithBogusBaseVersion_ReturnsStaleBase()
    {
        var sym = Root(await GetSymbol("Sample.Lib.Widget.Spin", "all"));
        var symbolId = sym.GetProperty("symbolId").GetString()!;

        var path = _f.Locator.AbsPath("Lib/Widget.cs");
        var currentLine = (await File.ReadAllLinesAsync(path))[11]; // line 12, whatever it currently reads

        var edits = new[] { new PatchEditInput(File: "Lib/Widget.cs", Lines: "12-12", NewText: currentLine) };
        var root = Root(await PatchTools.ValidatePatch(_f.Workspace, _f.Locator, _f.Symbols, _f.FeatureLog, _f.Builder, _f.TargetedTests, _f.Telemetry,
            new PatchDraftStore(TimeProvider.System),
            new Dictionary<string, string> { [symbolId] = "decl:0000deadbeef|body:0000deadbeef" }, edits,
            requestedLevel: null, applyOnSuccess: true, intent: "should never apply", tags: null));

        Assert.Equal("stale_base", root.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ValidatePatch_ChangesTextButNoSymbol_StillRequiresACompile()
    {
        // A using directive sits outside every declaration, so the classifier attributes this edit to no
        // symbol at all and the escalation table's max-over-symbols floors at parse. The directive is
        // syntactically perfect and cannot bind, so a parse-only gate would report succeeded and write a
        // file whose project no longer compiles. Line 1 is kept verbatim and the bogus using prepended:
        // replacing line 1 would rewrite the file's namespace and re-attribute every symbol in it, which
        // is the opposite of the no-symbol case under test.
        var path = _f.Locator.AbsPath("Lib/Widget.cs");
        var firstLine = (await File.ReadAllLinesAsync(path))[0];
        var edits = new[] { new PatchEditInput(File: "Lib/Widget.cs", Lines: "1-1", NewText: $"using Sample.ThisNamespaceDoesNotExist;\n{firstLine}") };
        var root = Root(await PatchTools.ValidatePatch(_f.Workspace, _f.Locator, _f.Symbols, _f.FeatureLog, _f.Builder, _f.TargetedTests, _f.Telemetry,
            new PatchDraftStore(TimeProvider.System),
            new Dictionary<string, string>(), edits,
            requestedLevel: null, applyOnSuccess: true, intent: "should never apply", tags: null));

        Assert.True(root.TryGetProperty("detectedChanges", out var dc), root.GetRawText());
        Assert.Empty(dc.EnumerateArray());
        Assert.Equal("project_compile", root.GetProperty("ladder").GetProperty("requiredLevel").GetString());
        Assert.False(root.GetProperty("succeeded").GetBoolean(), root.GetRawText());
        Assert.False(root.GetProperty("applied").GetBoolean(), root.GetRawText());
    }

    /// <summary>
    /// A search hit carries where it was found, so "search, then go there" is one call rather than two.
    /// The line is checked against the file's actual content, not just asserted non-null — a location
    /// that points at the wrong line is worse than none, since a caller has no reason to doubt it.
    /// The span is the @from-to read selector, so the same string pastes into get_symbol's source include;
    /// its end can never come before its start, which is cheap enough to check here too.
    /// </summary>
    [Fact]
    public async Task SearchIndex_HitCarriesTheFileAndLineItWasFoundAt()
    {
        var hit = TableRows(Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "SpinTwice", groupBy: "none")).GetProperty("items")).First();

        var file = hit["file"].GetString()!;
        var span = hit["lines"].GetString()!.TrimStart('@').Split('-');
        var line = int.Parse(span[0]);
        var endLine = int.Parse(span[^1]);

        var text = await File.ReadAllLinesAsync(_f.Locator.AbsPath(file));
        Assert.Contains("SpinTwice", text[line - 1]);
        Assert.True(endLine >= line);
    }

    /// <summary>
    /// The shape column describes every hit rather than firing above a threshold, so the fixture's small,
    /// one-line-documented declarations must each report their own real counts — where the gated design
    /// left all of them with no column at all.
    /// </summary>
    /// <remarks>
    /// The legend is asserted by value rather than by presence on purpose. It is the caller's only key to
    /// the column, and the only thing stating that an absent letter means none, so a reword that quietly
    /// drops that distinction has to fail somewhere — here.
    /// </remarks>
    [Fact]
    public async Task SearchIndex_DescribesEveryHitRatherThanOnlyLargeOnes()
    {
        var root = Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Spin Widget Undocumented",
            limit: 20, groupBy: "none"));

        Assert.Equal(SymbolShape.Legend, root.GetProperty("shape").GetString());

        var shapes = TableRows(root.GetProperty("items"))
            .Select(hit => hit.TryGetValue("shape", out var s) ? s.GetString() : null)
            .ToList();

        Assert.NotEmpty(shapes);
        Assert.All(shapes, s => Assert.NotNull(s));
        Assert.Contains(shapes, s => s!.Contains('L'));
        Assert.Contains(shapes, s => s!.Contains('M'));
        Assert.Contains(shapes, s => s!.Contains('D'));
    }

    /// <summary>
    /// summary:"has" is a cheap presence check — a documented symbol reports hasSummary:true with no
    /// summary text sent, so a caller can spot "is this documented" without paying for the extracted text.
    /// </summary>
    [Fact]
    public async Task SearchIndex_SummaryHas_ReportsPresenceWithoutText()
    {
        var hit = TableRows(Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Spin", include: "summary", groupBy: "none")).GetProperty("items"))
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
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Spin", include: "summary:full", groupBy: "none")).GetProperty("items"))
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
    /// xmlDoc filters on which XML doc sections a hit's declaration carries, beyond plain summary
    /// presence. Bare tokens AND (a declaration must carry every included section); a '-'-prefixed
    /// token excludes and combines with the included tokens — same grammar as modifiers.
    /// </summary>
    [Fact]
    public async Task SearchIndex_XmlDocFilter_AndsIncludedTokensAndCombinesExcludes()
    {
        var withReturns = TableRows(Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Full ReturnsOnly Undocumented",
            kinds: "method", xmlDoc: "returns", groupBy: "none")).GetProperty("items"))
            .Select(h => h["name"].GetString()).ToList();

        Assert.Contains(withReturns, n => n!.EndsWith(".Full()", StringComparison.Ordinal));
        Assert.Contains(withReturns, n => n!.EndsWith(".ReturnsOnly()", StringComparison.Ordinal));
        Assert.DoesNotContain(withReturns, n => n!.EndsWith(".Undocumented()", StringComparison.Ordinal));

        var returnsWithoutRemarks = TableRows(Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Full ReturnsOnly Undocumented",
            kinds: "method", xmlDoc: "returns -remarks", groupBy: "none")).GetProperty("items"))
            .Select(h => h["name"].GetString()).ToList();

        Assert.DoesNotContain(returnsWithoutRemarks, n => n!.EndsWith(".Full()", StringComparison.Ordinal));
        Assert.Contains(returnsWithoutRemarks, n => n!.EndsWith(".ReturnsOnly()", StringComparison.Ordinal));
    }

    /// <summary>
    /// The index keys members without their parameter lists, so overloads collapse onto one name. The
    /// requested name's parameter COUNT picks the right declaration back out, so each overload reports
    /// its own line instead of both being dropped as ambiguous.
    /// </summary>
    [Fact]
    public async Task SearchIndex_LocatesEachOverloadByItsParameterCount()
    {
        var rows = TableRows(Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Ambiguous", groupBy: "none")).GetProperty("items"));

        var overloads = rows
            .Where(row => row["name"].GetString()!.Contains("Ambiguous", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, overloads.Count);
        Assert.All(overloads, hit => Assert.True(hit.ContainsKey("file") && hit.ContainsKey("lines")));
        Assert.Equal(2, overloads.Select(hit => hit["lines"].GetString()).Distinct().Count());
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
            var edits = new[] { new PatchEditInput(File: "Lib/Widget.cs", Lines: "12-12", NewText: "    public int Spin(int turns) => turns * 9;") };
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
    /// groupBy:"namespace" collapses straight to flat namespace/file header fields plus one
    /// symbols table when the whole result set shares a single namespace and a single file — no wrapper
    /// arrays for the common single-file search. A leaf's kind column also hoists to a header field
    /// when every hit in that leaf shares one kind. limit:1 isolates SpinTwice on its own — the bare
    /// query also fuzzy-matches Spin, which spans a second file and would not collapse. limitedBy is
    /// omitted entirely (not printed as null) when nothing limited the answer, same as the flat
    /// groupBy:"none" shape. Passed explicitly here (rather than relying on the omitted-groupBy default,
    /// which now auto-picks whichever of flat/namespace-grouped is cheaper) since this test is about the
    /// grouped shape itself.
    /// </summary>
    [Fact]
    public async Task SearchIndex_CollapsesToFlatHeader_WhenResultsShareOneNamespaceAndFile()
    {
        var root = Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "SpinTwice", limit: 1, groupBy: "namespace"));

        Assert.False(root.TryGetProperty("limitedBy", out _));
        Assert.Equal("Sample.Lib", root.GetProperty("namespace").GetString());
        Assert.EndsWith("Pipeline.cs", root.GetProperty("file").GetString());
        Assert.Equal("Method", root.GetProperty("kind").GetString());
        var symbols = TableRows(root.GetProperty("symbols"));
        var symbol = Assert.Single(symbols);
        Assert.False(symbol.ContainsKey("kind"));
        Assert.Equal("WidgetExtensions.SpinTwice(IWidget, int)", symbol["name"].GetString());
    }


    /// <summary>
    /// A query spanning several files under one namespace nests namespaces[] -> files[] -> symbols[]
    /// rather than collapsing, since the file axis still varies — the wrapper array stays even though
    /// there is only one namespace, matching the file-grouped shape's own per-group array discipline.
    /// groupBy:"namespace" passed explicitly, since the omitted default now auto-picks whichever of
    /// flat/namespace-grouped is cheaper and this test is about the grouped shape itself.
    /// </summary>
    [Fact]
    public async Task SearchIndex_GroupsByNamespaceByDefault_NestingMultipleFilesUnderOneNamespace()
    {
        var root = Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Widget", kinds: "class", limit: 10, groupBy: "namespace"));

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

    /// <summary>
    /// A source symbol carries NO origin field: absent means "declared in this solution", which is almost
    /// every response, so stating it cost a field on all of them to carry information on nearly none. Only
    /// "external" is worth saying, because only it explains empty declarationSites/source/xmlDoc.
    /// </summary>
    [Fact]
    public async Task GetSymbol_SourceSymbol_OmitsOriginEntirely()
    {
        var root = Root(await GetSymbol("Sample.Lib.Widget"));
        Assert.False(root.GetProperty("content").TryGetProperty("origin", out _));
    }

    [Fact]
    public async Task GetProjectGraph_ReportsProjectsAndTotalCount()
    {
        var root = Root(await GraphTools.GetProjectGraph(_f.Workspace, _f.Telemetry));

        var projects = root.GetProperty("projects").EnumerateArray().ToList();
        Assert.NotEmpty(projects);
        Assert.Equal(projects.Count, root.GetProperty("totalProjectsInSolution").GetInt32());
        Assert.True(projects[0].TryGetProperty("references", out _));
        Assert.True(projects[0].TryGetProperty("referencedBy", out _));
    }

    [Fact]
    public async Task GetProjectGraph_ScopedToUnknownProject_ReportsProjectNotFound()
    {
        var root = Root(await GraphTools.GetProjectGraph(_f.Workspace, _f.Telemetry, project: "NoSuchProject"));

        Assert.Equal("project_not_found", root.GetProperty("error").GetString());
            }

            [Fact]
            public async Task GetProjectGraph_ScopedToUnknownProject_SurfacesCandidatesAndDidYouMean()
            {
                var root = Root(await GraphTools.GetProjectGraph(_f.Workspace, _f.Telemetry, project: "Li"));

                Assert.Equal("project_not_found", root.GetProperty("error").GetString());
                var projects = root.GetProperty("projects").EnumerateArray().Select(p => p.GetString()).ToList();
                Assert.Contains("Lib", projects);
                Assert.Equal("Lib", root.GetProperty("didYouMean").GetString());
            }

    [Fact]
    public async Task DetectCircularDependencies_AcyclicSample_ReportsNoCycles()
    {
        var root = Root(await GraphTools.DetectCircularDependencies(_f.Workspace, _f.Telemetry));

        Assert.Equal("project", root.GetProperty("scope").GetString());
        Assert.Equal(0, root.GetProperty("totalCycles").GetInt32());
        Assert.Empty(root.GetProperty("cycles").EnumerateArray());
    }

    [Fact]
    public async Task DetectCircularDependencies_UnsupportedScope_ReturnsError()
    {
        var root = Root(await GraphTools.DetectCircularDependencies(_f.Workspace, _f.Telemetry, scope: "type"));

        Assert.Equal("unsupported_scope", root.GetProperty("error").GetString());
            }

            [Fact]
            public async Task DetectCircularDependencies_ScopeTypo_SurfacesDidYouMeanNotUnsupportedMessage()
            {
                var root = Root(await GraphTools.DetectCircularDependencies(_f.Workspace, _f.Telemetry, scope: "projects"));

                Assert.Equal("unsupported_scope", root.GetProperty("error").GetString());
                var message = root.GetProperty("message").GetString();
                Assert.DoesNotContain("not yet implemented", message, StringComparison.Ordinal);
                Assert.Contains("project", message, StringComparison.Ordinal);
            }

    [Fact]
    public async Task GetTypeHierarchy_Interface_ListsDirectImplementers()
    {
        var root = Root(await FlowTools.GetTypeHierarchy(_f.Workspace, _f.Symbols, _f.Telemetry, "Sample.Lib.IWidget"));

        var derivedNames = root.GetProperty("derived").GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("displayString").GetString() ?? "").ToList();
        Assert.Contains(derivedNames, d => d.Contains("Widget"));
        Assert.Contains(derivedNames, d => d.Contains("TurboWidget"));
    }

    /// <summary>
    /// A derived list mixes abstract intermediates in with concrete leaves, so "which of these can I
    /// actually instantiate" is unanswerable from the list alone unless each row says so. Flags are emitted
    /// only when true, which is what keeps an all-concrete hierarchy from paying for them.
    /// </summary>
    [Fact]
    public async Task GetTypeHierarchy_DerivedTypes_FlagAbstractAndSealed()
    {
        var derived = TableRows(Root(await FlowTools.GetTypeHierarchy(
                _f.Workspace, _f.Symbols, _f.Telemetry, "Sample.Lib.GearBase"))
            .GetProperty("derived").GetProperty("items"))
            .ToDictionary(r => r["displayString"].GetString() ?? "");

        Assert.True(derived["MidGear"]["isAbstract"].GetBoolean());
        Assert.False(derived["MidGear"].ContainsKey("isSealed"));

        Assert.True(derived["HighGear"]["isSealed"].GetBoolean());
        Assert.False(derived["HighGear"].ContainsKey("isAbstract"));
    }

    /// <summary>
    /// include:"refs" answers "is anything using this" inside the search call. A zero MUST survive into the
    /// code as R0 rather than being dropped: that zero is the dead-code answer the column exists to give, and
    /// a caller who cannot tell it from an absent column has to spend a second call to find out.
    /// </summary>
    [Fact]
    public async Task SearchIndex_RefsCounts_EmitsCallersIncludingZero()
    {
        var uncalled = TableRows(Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Undocumented", include: "refs", groupBy: "none"))
            .GetProperty("items")).First();

        Assert.Equal("R0", uncalled["refs"].GetString());

        var withoutRefs = TableRows(Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Undocumented", include: "shape", groupBy: "none"))
            .GetProperty("items")).First();

        Assert.False(withoutRefs.ContainsKey("refs"));
    }

    /// <summary>
    /// A named type reports IMPLEMENTATIONS, never callers. Call edges bind to members, so a type's caller
    /// count would be a structural 0 rather than a measured one, and on the commonest kind of search hit that
    /// reads as "nothing uses this" for a type used everywhere. RefCode picks the letter set from the kind,
    /// which is the rule the member-only test above cannot see.
    /// </summary>
    [Fact]
    public async Task SearchIndex_Refs_ReportsImplementationsForNamedTypesNotCallers()
    {
        var root = Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Widget", kinds: "class", include: "refs", groupBy: "none"));
        var rows = TableRows(root.GetProperty("items"));

        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.StartsWith("I", r["refs"].GetString()!, StringComparison.Ordinal));
        Assert.All(rows, r => Assert.DoesNotContain("R", r["refs"].GetString()!, StringComparison.Ordinal));
    }

    /// <summary>
    /// The batched count path must omit an id whose project the edge cache never covered, exactly as the
    /// single-symbol path does through HasEdgeCoverageFor. A project that fails to load in MSBuild contributes
    /// no edges, and reporting that absence as "0 callers" states something the store cannot know.
    /// </summary>
    [Fact]
    public async Task ReferenceCountsFor_OmitsIdsWithNoEdgeCoverage()
    {
        // An id belonging to no indexed project at all: coverage cannot be established for it.
        Assert.Null(_f.Symbols.ReferenceCountsFor(["sym_not_a_real_symbol"]));

        // A real member's project does have edges, so it comes back measured — present, carrying its count.
        var root = Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Spin", kinds: "Method", groupBy: "none"));
        var id = TableRows(root.GetProperty("items")).First()["symbolId"].GetString()!;

        var counts = _f.Symbols.ReferenceCountsFor([id]);
        Assert.NotNull(counts);
        Assert.True(counts!.ContainsKey(id), "a covered id must be present, carrying 0 when it has no callers");
    }

    [Fact]
    public async Task GetTypeHierarchy_Class_ReportsBaseChainAndDerived()
    {
        var root = Root(await FlowTools.GetTypeHierarchy(_f.Workspace, _f.Symbols, _f.Telemetry, "Sample.Lib.GearBase"));

        Assert.Contains(root.GetProperty("baseChain").EnumerateArray(),
            b => (b.GetProperty("displayString").GetString() ?? "").Contains("object"));
        var derivedNames = root.GetProperty("derived").GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("displayString").GetString() ?? "").ToList();
        Assert.Contains(derivedNames, d => d.Contains("HighGear"));
    }

    [Fact]
    public async Task GetTypeHierarchy_UnknownSymbol_ReportsSymbolNotFound()
    {
        var root = Root(await FlowTools.GetTypeHierarchy(_f.Workspace, _f.Symbols, _f.Telemetry, "Sample.Lib.NoSuchType"));

        Assert.Equal("symbol_not_found", root.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetCallHierarchy_Callees_ReachesSpinAndReportsBlastRadius()
    {
        var root = Root(await FlowTools.GetCallHierarchy(
            _f.Workspace, _f.Symbols, _f.Index, _f.Builder, _f.Telemetry, "Sample.Lib.Pipeline.Start", direction: "callees"));

        Assert.Equal("callees", root.GetProperty("direction").GetString());
        Assert.True(root.GetProperty("blastRadius").GetProperty("totalUniqueNodes").GetInt32() > 0);

        static bool ContainsSpin(JsonElement node)
        {
            if ((node.GetProperty("displayString").GetString() ?? "").Contains("Spin"))
                return true;
            return node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array
                && children.EnumerateArray().Any(ContainsSpin);
        }
        Assert.True(ContainsSpin(root.GetProperty("tree")));
    }

    [Fact]
    public async Task GetCallHierarchy_IncludeTreeFalse_OmitsTreeButKeepsBlastRadius()
    {
        var root = Root(await FlowTools.GetCallHierarchy(
            _f.Workspace, _f.Symbols, _f.Index, _f.Builder, _f.Telemetry, "Sample.Lib.Pipeline.Start",
            direction: "callees", includeTree: false));

        Assert.False(root.TryGetProperty("tree", out _));
        Assert.True(root.GetProperty("blastRadius").GetProperty("totalUniqueNodes").GetInt32() > 0);
    }

    /// <summary>
    /// A child the per-node cap left unexpanded is still part of the blast radius, so at maxDepth 1 -- where
    /// every reached node is a child of the root -- the cap cannot change the count. It does shrink the total
    /// at greater depths, since an unexpanded node's own callers are never visited; what is asserted here is
    /// the countable part, plus that the truncation is reported in the summary-only shape, which is the only
    /// shape the caller who asked "how much does changing this ripple" ever sees.
    /// </summary>
    [Fact]
    public async Task GetCallHierarchy_BlastRadius_CountsWhatThePerNodeCapLeftOut()
    {
        var capped = Root(await FlowTools.GetCallHierarchy(
            _f.Workspace, _f.Symbols, _f.Index, _f.Builder, _f.Telemetry, "Sample.Lib.Widget.Spin",
            maxDepth: 1, maxChildrenPerNode: 1, includeTree: false)).GetProperty("blastRadius");
        var uncapped = Root(await FlowTools.GetCallHierarchy(
            _f.Workspace, _f.Symbols, _f.Index, _f.Builder, _f.Telemetry, "Sample.Lib.Widget.Spin",
            maxDepth: 1, maxChildrenPerNode: 200, includeTree: false)).GetProperty("blastRadius");

        // Two callers in the fixture: Pipeline.Deep and Program.cs's top-level statements.
        Assert.True(uncapped.GetProperty("totalUniqueNodes").GetInt32() >= 3);
        Assert.Equal(
            uncapped.GetProperty("totalUniqueNodes").GetInt32(),
            capped.GetProperty("totalUniqueNodes").GetInt32());
        Assert.True(capped.GetProperty("truncated").GetBoolean());
        Assert.True(capped.GetProperty("omittedChildren").GetInt32() > 0);
        Assert.False(uncapped.TryGetProperty("truncated", out _));
    }

    /// <summary>
    /// A node the edge cache reached but the symbol table cannot name used to render its own symbolId as its
    /// displayString -- one string under two keys, naming nothing. The default row is exactly what the
    /// projection's failed RowsFor lookup hands it, and the store is real, so both misses are genuine.
    /// </summary>
    /// <remarks>
    /// Asserted against the helper rather than a rendered tree because every symbol the fixture solution
    /// reaches is one the index can name -- external BCL members included -- so no call reaches this branch.
    /// </remarks>
    [Fact]
    public void CallTreeDisplay_ForAnIdTheIndexCannotName_IsNullRatherThanTheIdItself()
    {
        Assert.Null(FlowTools.DisplayOf(_f.Symbols, "sym_notarealsymbolid", default, wantSignature: false));
        Assert.Null(FlowTools.DisplayOf(_f.Symbols, "sym_notarealsymbolid", default, wantSignature: true));

        var named = FlowTools.DisplayOf(_f.Symbols, "sym_notarealsymbolid", ("Sample.Lib.Widget.Spin", "Method", null), wantSignature: false);
        Assert.Equal("Widget.Spin", named);
    }

    /// <summary>
    /// An empty multi-term result is the response carrying no other evidence, so it is the one that most
    /// needs the missed terms named - the gate that skipped it made silence mean two different things.
    /// </summary>
    [Fact]
    public async Task SearchIndex_EmptyMultiTermResult_StillNamesTheTermsThatMissed()
    {
        var root = Root(await ContextTools.SearchIndex(_f.Symbols, _f.Index, _f.Workspace, _f.Telemetry,
            "Zzqqvv Wwxxyy", limit: 10, groupBy: "none"));

        Assert.Empty(TableRows(root.GetProperty("items")));

        var missed = root.GetProperty("termsWithNoHits").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Contains("Zzqqvv", missed);
        Assert.Contains("Wwxxyy", missed);
    }

    /// <summary>
    /// The common miss is a right name under a wrong qualification, which the index can answer directly
    /// rather than sending the caller off to search_index for it.
    /// </summary>
    [Fact]
    public async Task GetReferences_UnresolvedName_OffersNearMissCandidates()
    {
        var root = Root(await GetReferences("Nowhere.Widget", "callers"));

        Assert.Equal("symbol_not_found", root.GetProperty("error").GetString());

        var names = root.GetProperty("didYouMean").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString() ?? "").ToList();
        Assert.Contains(names, n => n.EndsWith("Widget", StringComparison.Ordinal));
    }

    /// <summary>
    /// A class has no call sites of its own, so there is no dispatch to describe; a method still has one.
    /// </summary>
    [Fact]
    public async Task GetReferences_ClassRoot_OmitsDispatchKindItCannotDescribe()
    {
        var typeRoot = Root(await GetReferences("Sample.Lib.Widget", "callers"));
        var methodRoot = Root(await GetReferences("Sample.Lib.Widget.Spin", "callers"));

        Assert.False(typeRoot.TryGetProperty("dispatchKind", out _));
        Assert.True(methodRoot.TryGetProperty("dispatchKind", out _));
    }

    /// <summary>
    /// A member ROW serves a name, a location and a shape - never a body - so the token it hands over must
    /// not lease one. Leasing what was never served is exactly what unleased_body exists to prevent.
    /// </summary>
    [Fact]
    public async Task GetSymbol_Members_ContentVersionOnlyUnderIncludeAll()
    {
        // A member row's contentVersion is narrowed to decl and is only worth its tokens when the caller
        // is about to edit (include:"all"): a plain members-only fetch never patches from the row directly
        // (self-eval finding, 2026-08-10).
        var membersOnly = TableRows(Root(await GetSymbol("Sample.Lib.Widget", include: "members"))
            .GetProperty("content").GetProperty("members"));
        Assert.NotEmpty(membersOnly);
        Assert.All(membersOnly, row => Assert.False(row.ContainsKey("contentVersion")));

        // A member row only appears under include:"all" when the large-source guard substitutes members for
        // source -- otherwise source is served and members are suppressed, so content.members is absent and
        // there is no lease to check. Nothing in the fixture is within two orders of magnitude of the
        // 500-line default, so the threshold is lowered for this call instead. Reset() clears any earlier
        // acknowledgement, since a repeat of the same fetch is exactly how a caller overrides the guard.
        var originalThreshold = ResponseGuard.LineThreshold;
        ResponseGuard.LineThreshold = 1;
        ResponseGuard.Reset();
        try
        {
            var all = TableRows(Root(await GetSymbol("Sample.Lib.Widget", include: "all"))
                .GetProperty("content").GetProperty("members"));
            Assert.NotEmpty(all);
            Assert.All(all, row => Assert.DoesNotContain(
                "body:", row["contentVersion"].GetString() ?? "", StringComparison.Ordinal));
        }
        finally
        {
            ResponseGuard.LineThreshold = originalThreshold;
            ResponseGuard.Reset();
        }
    }


    /// <summary>
    /// A branch the per-node cap left unexpanded hides the depths past it exactly as maxDepth does, so it
    /// sets depthCapped too - reporting false there read as "complete to maxDepth".
    /// </summary>
    [Fact]
    public async Task GetCallHierarchy_ChildCapHidingBranches_ReportsDepthCapped()
    {
        var capped = Root(await FlowTools.GetCallHierarchy(
            _f.Workspace, _f.Symbols, _f.Index, _f.Builder, _f.Telemetry, "Sample.Lib.Widget.Spin",
            maxDepth: 8, maxChildrenPerNode: 1, includeTree: false)).GetProperty("blastRadius");
        var whole = Root(await FlowTools.GetCallHierarchy(
            _f.Workspace, _f.Symbols, _f.Index, _f.Builder, _f.Telemetry, "Sample.Lib.Widget.Spin",
            maxDepth: 8, maxChildrenPerNode: 200, includeTree: false)).GetProperty("blastRadius");

        Assert.True(capped.GetProperty("depthCapped").GetBoolean());
        Assert.True(!whole.TryGetProperty("depthCapped", out var flag) || !flag.GetBoolean());

    }

    /// get_symbol narrows its contentVersion to the layers it actually served, so the default fetch hands
    /// out no body layer -- and a body-rewriting patch built on one was never checked against the body it
    /// overwrites, which is exactly the concurrent-edit case baseVersions exists to reject.
    /// </summary>
    [Fact]
    public async Task ValidatePatch_BodyChangeWithoutABodyLayer_ReturnsUnleasedBody()
    {
        var sym = Root(await GetSymbol("Sample.Lib.Widget.Spin"));
        var symbolId = sym.GetProperty("symbolId").GetString()!;
        var withoutBody = sym.GetProperty("contentVersion").GetString()!;
        Assert.DoesNotContain("body:", withoutBody);

        var edits = new[] { new PatchEditInput(File: "Lib/Widget.cs", Lines: "12-12", NewText: "    public int Spin(int turns) => turns * 3;") };
        var root = Root(await PatchTools.ValidatePatch(_f.Workspace, _f.Locator, _f.Symbols, _f.FeatureLog, _f.Builder, _f.TargetedTests, _f.Telemetry,
            new PatchDraftStore(TimeProvider.System),
            new Dictionary<string, string> { [symbolId] = withoutBody }, edits,
            requestedLevel: null, applyOnSuccess: true, intent: "should never apply", tags: null));

        Assert.Equal("unleased_body", root.GetProperty("error").GetString());
        Assert.Equal(symbolId, root.GetProperty("current")[0].GetProperty("symbolId").GetString());
        Assert.Contains("body:", root.GetProperty("current")[0].GetProperty("currentVersion").GetString()!);
        Assert.True(root.TryGetProperty("draft", out _));
        Assert.Equal(
            "    public int Spin(int turns) => turns * 2;",
            (await File.ReadAllLinesAsync(_f.Locator.AbsPath("Lib/Widget.cs")))[11]);
    }

    /// <summary>
    /// current[] is the set of baseVersions entries to resend, not a list of changes: two members added to
    /// one type are two detected changes anchored to that same type, and naming it twice asked the caller
    /// to send one map entry twice.
    /// </summary>
    [Fact]
    public async Task ValidatePatch_TwoMembersAddedToOneType_NameThatTypeOnce()
    {
        var spin = Root(await GetSymbol("Sample.Lib.Widget.Spin", "all"));
        var widgetId = Root(await GetSymbol("Sample.Lib.Widget")).GetProperty("symbolId").GetString()!;

        var edits = new[]
        {
            new PatchEditInput(File: "Lib/Widget.cs", Lines: "12-12", NewText:
                "    public int Spin(int turns) => turns * 2;\n\n    public int AddedOne() => 1;\n\n    public int AddedTwo() => 2;"),
        };
        var root = Root(await PatchTools.ValidatePatch(_f.Workspace, _f.Locator, _f.Symbols, _f.FeatureLog, _f.Builder, _f.TargetedTests, _f.Telemetry,
            new PatchDraftStore(TimeProvider.System),
            new Dictionary<string, string>
            {
                [spin.GetProperty("symbolId").GetString()!] = spin.GetProperty("contentVersion").GetString()!,
            },
            edits, requestedLevel: null, applyOnSuccess: false, intent: "should never apply", tags: null));

        Assert.Equal("unheld_symbol", root.GetProperty("error").GetString());
        var current = Assert.Single(root.GetProperty("current").EnumerateArray().ToList());
        Assert.Equal(widgetId, current.GetProperty("symbolId").GetString());
    }

    /// <summary>
    /// A filter-only call searches for nothing, and answering it with an empty item list reads as "no such
    /// symbols exist" -- the silent under-report termsWithNoHits exists to prevent, reached through the
    /// arguments rather than the index. Null is unreachable through the MCP host, which refuses an omitted
    /// required argument itself; it is covered here because this in-process caller bypasses that schema.
    /// </summary>
    [Fact]
    public async Task SearchIndex_WithoutAQuery_ReturnsAStructuredErrorRatherThanThrowing()
    {
        foreach (var absent in new string?[] { null, "", "   " })
        {
            var root = Root(await ContextTools.SearchIndex(_f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, absent!));

            Assert.Equal("missing_query", root.GetProperty("error").GetString());
            Assert.Contains("narrow a search", root.GetProperty("message").GetString());
        }
    }

    /// <summary>
    /// <summary>
        /// A query built only from words that read as a kind/modifier keyword is the one failure
        /// termsWithNoHits does not cover -- query matches identifier text, so a bare structural word
        /// almost never names a real symbol. hint is the self-correcting nudge toward kinds/modifiers that
        /// an empty items list alone does not carry.
        /// </summary>
        [Fact]
        public async Task SearchIndex_StructuralWordOnlyZeroHitQuery_ReturnsCorrectiveHint()
        {
            var root = Root(await ContextTools.SearchIndex(
                _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "asyncdisposable initonly", groupBy: "none"));

            Assert.Empty(TableRows(root.GetProperty("items")));
            var hint = root.GetProperty("hint").GetString();
            Assert.Contains("kinds", hint, StringComparison.Ordinal);
            Assert.Contains("modifiers", hint, StringComparison.Ordinal);
        }

        /// <summary>
        /// The hint is scoped to queries built entirely from structural words: a real term mixed in means the
        /// caller is searching for an actual identifier, not misusing query as a structural filter, so a
        /// zero-hit result there carries no hint -- exactly like any other zero-hit search.
        /// </summary>
        [Fact]
        public async Task SearchIndex_ZeroHitQueryWithARealTerm_CarriesNoHint()
        {
            var root = Root(await ContextTools.SearchIndex(
                _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "asyncdisposable ZzzNoSuchSymbolAnywhereXyz123", groupBy: "none"));

            Assert.Empty(TableRows(root.GetProperty("items")));
            Assert.False(root.TryGetProperty("hint", out _));
        }

        [Fact]
        public async Task SearchIndex_KindsTypoZeroHits_ReturnsCorrectiveHint()
        {
            var root = Root(await ContextTools.SearchIndex(
                _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "FeatureLogStore", kinds: "clas", groupBy: "none"));

            Assert.Empty(TableRows(root.GetProperty("items")));
            var hint = root.GetProperty("kindsHint").GetString();
            Assert.Contains("class", hint, StringComparison.Ordinal);
        }

        [Fact]
        public async Task SearchIndex_KindsValid_CarriesNoHint()
        {
            var root = Root(await ContextTools.SearchIndex(
                _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "FeatureLogStore", kinds: "class", groupBy: "none"));

            Assert.False(root.TryGetProperty("kindsHint", out _));
        }

        [Fact]
        public async Task SearchIndex_ModifiersTypoZeroHits_ReturnsCorrectiveHint()
        {
            var root = Root(await ContextTools.SearchIndex(
                _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "FeatureLogStore", modifiers: "publi", groupBy: "none"));

            Assert.Empty(TableRows(root.GetProperty("items")));
            var hint = root.GetProperty("modifiersHint").GetString();
            Assert.Contains("public", hint, StringComparison.Ordinal);
        }

        [Fact]
        public async Task SearchIndex_ModifiersValid_CarriesNoHint()
        {
            var root = Root(await ContextTools.SearchIndex(
                _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "FeatureLogStore", modifiers: "public", groupBy: "none"));

            Assert.False(root.TryGetProperty("modifiersHint", out _));
        }

        [Fact]
        public async Task SearchIndex_OriginTypo_ReturnsCorrectiveHint()
        {
            var root = Root(await ContextTools.SearchIndex(
                _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "FeatureLogStore", origin: "extern", groupBy: "none"));

            var hint = root.GetProperty("originHint").GetString();
            Assert.Contains("external", hint, StringComparison.Ordinal);
        }

        [Fact]
        public async Task SearchIndex_OriginValid_CarriesNoHint()
        {
            var root = Root(await ContextTools.SearchIndex(
                _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "FeatureLogStore", origin: "source", groupBy: "none"));

            Assert.False(root.TryGetProperty("originHint", out _));
        }

        [Fact]
        public async Task SearchIndex_SummaryTypo_ReturnsCorrectiveHint()
        {
            var root = Root(await ContextTools.SearchIndex(
                _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "FeatureLogStore", include: "shapes", groupBy: "none"));

            var hint = root.GetProperty("includeHint").GetString();
            Assert.Contains("shape", hint, StringComparison.Ordinal);
        }

        [Fact]
        public async Task SearchIndex_SummaryValid_CarriesNoHint()
        {
            var root = Root(await ContextTools.SearchIndex(
                _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "FeatureLogStore", include: "summary", groupBy: "none"));

            Assert.False(root.TryGetProperty("includeHint", out _));
        }

        [Fact]
        public async Task SearchIndex_GroupByTypo_ReturnsCorrectiveHint()
        {
            var root = Root(await ContextTools.SearchIndex(
                _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "FeatureLogStore", groupBy: "fil"));

            var hint = root.GetProperty("groupByHint").GetString();
            Assert.Contains("file", hint, StringComparison.Ordinal);
        }

        [Fact]
        public async Task SearchIndex_GroupByValid_CarriesNoHint()
        {
            var root = Root(await ContextTools.SearchIndex(
                _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "FeatureLogStore", groupBy: "file"));

            Assert.False(root.TryGetProperty("groupByHint", out _));
        }

        [Fact]
        public async Task SearchIndex_IntentTypo_ReturnsCorrectiveHint()
        {
            var root = Root(await ContextTools.SearchIndex(
                _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "FeatureLogStore", intent: "logicc", groupBy: "none"));

            var hint = root.GetProperty("intentHint").GetString();
            Assert.Contains("logic", hint, StringComparison.Ordinal);
        }

        [Fact]
        public async Task SearchIndex_IntentValid_CarriesNoHint()
        {
            var root = Root(await ContextTools.SearchIndex(
                _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "FeatureLogStore", intent: "logic", groupBy: "none"));

            Assert.False(root.TryGetProperty("intentHint", out _));
        }

        /// <summary>
        /// receiver names something that HAS a type; a method name is the common miss. Reporting the bare error
    /// code left the caller with a correct diagnosis and nothing to do about it.
    /// </summary>
    [Fact]
    public async Task GetScope_FilterSingularTypo_FallsBackToAllWithHint()
        {
            var root = Root(await FlowTools.GetScope(_f.Workspace, _f.Locator, _f.Telemetry, "Lib/Widget.cs", 12, filter: "method"));

            var hint = root.GetProperty("filterHint").GetString();
            Assert.Contains("methods", hint, StringComparison.Ordinal);
        }

        [Fact]
        public async Task GetScope_FilterAll_CarriesNoHint()
        {
            var root = Root(await FlowTools.GetScope(_f.Workspace, _f.Locator, _f.Telemetry, "Lib/Widget.cs", 12, filter: "all"));

            Assert.False(root.TryGetProperty("filterHint", out _));
        }

        [Fact]
        public async Task GetScope_ReceiverNamingAMethod_SaysWhatAReceiverIsAndHowToProceed()
    {
        var root = Root(await FlowTools.GetScope(
            _f.Workspace, _f.Locator, _f.Telemetry, "Lib/Widget.cs", 12, receiver: "Spin"));

        Assert.Equal("receiver_not_resolved", root.GetProperty("error").GetString());
        var message = root.GetProperty("message").GetString()!;
        Assert.Contains("Spin", message);
        Assert.Contains("Omit receiver", message);
    }

    /// <summary>
    /// A degraded workspace reports errors the change did not cause, so the default advice sends the caller
    /// to rewrite correct code -- which is exactly how a self-evaluation read a legitimate bind failure as a
    /// defect in validate_patch.
    /// </summary>
    [Fact]
    public void WriteVerdicts_OnADegradedWorkspace_NameTheWorkspaceRatherThanBlamingTheChange()
    {
        var failed = new DotnetToolkit.McpServer.Validation.ValidationLadder.LadderResult(
            DotnetToolkit.McpServer.Validation.ValidationLevel.SemanticBind, Succeeded: false, [], []);
        var required = DotnetToolkit.McpServer.Validation.ValidationLevel.ProjectCompile;

        var (_, degradedPatch) = PatchTools.Verdict(failed, required, isSufficient: false, degraded: true);
        var (_, healthyPatch) = PatchTools.Verdict(failed, required, isSufficient: false, degraded: false);
        Assert.Contains("workspace_status", degradedPatch);
        Assert.DoesNotContain("workspace_status", healthyPatch);

        Assert.Contains("workspace_status", RenameTools.NextAction(failed, required, degraded: true));
        Assert.DoesNotContain("workspace_status", RenameTools.NextAction(failed, required, degraded: false));
        Assert.Contains("DEGRADED", RenameTools.Reason(failed, required, degraded: true));
        Assert.DoesNotContain("DEGRADED", RenameTools.Reason(failed, required, degraded: false));
    }

    /// <summary>
    /// A comment-only rewrite produces no semantic change, so keying the lease on the classifier's output
    /// let it through — while it overwrites body TEXT exactly as a semantic rewrite does, which is the
    /// concurrent-edit case the lease exists to catch, and a second agent editing comments is precisely
    /// the likely case.
    /// </summary>
    [Fact]
    public async Task ValidatePatch_CommentOnlyBodyEditWithoutABodyLayer_ReturnsUnleasedBody()
    {
        var sym = Root(await GetSymbol("Sample.Lib.BodyOutlineFixture.Classify"));
        var symbolId = sym.GetProperty("symbolId").GetString()!;
        var withoutBody = sym.GetProperty("contentVersion").GetString()!;
        Assert.DoesNotContain("body:", withoutBody);

        var edits = new[]
        {
            new PatchEditInput(File: "Lib/BodyOutlineFixture.cs", Lines: "12-12", NewText: "        var result = \"\"; // lease probe"),
        };
        var root = Root(await PatchTools.ValidatePatch(_f.Workspace, _f.Locator, _f.Symbols, _f.FeatureLog, _f.Builder, _f.TargetedTests, _f.Telemetry,
            new PatchDraftStore(TimeProvider.System),
            new Dictionary<string, string> { [symbolId] = withoutBody }, edits,
            requestedLevel: null, applyOnSuccess: true, intent: "should never apply", tags: null));

        Assert.Equal("unleased_body", root.GetProperty("error").GetString());
        Assert.Equal(symbolId, root.GetProperty("current")[0].GetProperty("symbolId").GetString());
        Assert.Equal(
            "        var result = \"\";",
            (await File.ReadAllLinesAsync(_f.Locator.AbsPath("Lib/BodyOutlineFixture.cs")))[11]);
    }

    [Fact]
    public async Task GetCallHierarchy_DirectionTypoOfCallees_FallsBackToCallersWithHint()
        {
            var root = Root(await FlowTools.GetCallHierarchy(
                _f.Workspace, _f.Symbols, _f.Index, _f.Builder, _f.Telemetry, "Sample.Lib.Widget.Spin", direction: "callee"));

            Assert.Equal("callers", root.GetProperty("direction").GetString());
            var hint = root.GetProperty("directionHint").GetString();
            Assert.Contains("callees", hint, StringComparison.Ordinal);
        }

        [Fact]
        public async Task GetCallHierarchy_DirectionCallers_CarriesNoHint()
        {
            var root = Root(await FlowTools.GetCallHierarchy(
                _f.Workspace, _f.Symbols, _f.Index, _f.Builder, _f.Telemetry, "Sample.Lib.Widget.Spin", direction: "callers"));

            Assert.False(root.TryGetProperty("directionHint", out _));
        }

        [Fact]
        public async Task GetCallHierarchy_UnknownSymbol_ReportsSymbolNotFound()
    {
        var root = Root(await FlowTools.GetCallHierarchy(
            _f.Workspace, _f.Symbols, _f.Index, _f.Builder, _f.Telemetry, "Sample.Lib.NoSuchMethod"));

        Assert.Equal("symbol_not_found", root.GetProperty("error").GetString());
    }

    /// <summary>
    /// A method states its own type parameters in the index's signature but not in its indexed name, so the
    /// symbol store's <c>Pick&lt;T&gt;</c> form matched no key and the hit carried no file or line at all.
    /// </summary>
    [Fact]
    public async Task SearchIndex_LocatesAMethodDeclaringItsOwnTypeParameters()
    {
        var rows = TableRows(Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Pick", kinds: "method", groupBy: "none")).GetProperty("items"));

        var generic = Assert.Single(rows, row => row["name"].GetString()!.Contains("Pick<T>", StringComparison.Ordinal));
        Assert.True(generic.ContainsKey("file") && generic.ContainsKey("lines"), "a generic method must carry its location");
        Assert.EndsWith("GenericSample.cs", generic["file"].GetString()!, StringComparison.Ordinal);
        Assert.StartsWith("@11", generic["lines"].GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The body-layer demand cannot depend on the member's shape: rewriting a generic method's body against
    /// a declaration-only token leaves exactly the same overwrite unverified as a non-generic one.
    /// </summary>
    [Fact]
    public async Task ValidatePatch_BodyChangeToAGenericMethodWithoutABodyLayer_ReturnsUnleasedBody()
    {
        var sym = Root(await GetSymbol("Sample.Lib.GenericSample.Scale"));
        var symbolId = sym.GetProperty("symbolId").GetString()!;
        var withoutBody = sym.GetProperty("contentVersion").GetString()!;
        Assert.DoesNotContain("body:", withoutBody);

        var edits = new[] { new PatchEditInput(File: "Lib/GenericSample.cs", Lines: "17-17", NewText: "        return value * 3;") };
        var root = Root(await ContextToolsValidate(
            new Dictionary<string, string> { [symbolId] = withoutBody }, edits, applyOnSuccess: true, intent: "should never apply"));

        Assert.Equal("unleased_body", root.GetProperty("error").GetString());
        Assert.Equal(symbolId, root.GetProperty("current")[0].GetProperty("symbolId").GetString());
        Assert.Contains("body:", root.GetProperty("current")[0].GetProperty("currentVersion").GetString()!);
        Assert.Equal(
            "        return value * 2;",
            (await File.ReadAllLinesAsync(_f.Locator.AbsPath("Lib/GenericSample.cs")))[16]);
    }

    /// <summary>A line-range edit's symbolId is cross-checked against that symbol's own live declaration span, and a correct match does not block validation.</summary>
    [Fact]
    public async Task ValidatePatch_LinesWithSymbolId_MatchingSpanValidatesNormally()
    {
        var sym = Root(await GetSymbol("Sample.Lib.Widget.Spin", "all"));
        var symbolId = sym.GetProperty("symbolId").GetString()!;
        var version = sym.GetProperty("contentVersion").GetString()!;

        var edits = new[] { new PatchEditInput(File: "Lib/Widget.cs", SymbolId: symbolId, Lines: "12-12", NewText: "    public int Spin(int turns) => turns * 3;") };
        var root = Root(await ContextToolsValidate(new Dictionary<string, string> { [symbolId] = version }, edits,
            applyOnSuccess: false, intent: "cross-checked edit"));

        Assert.False(root.TryGetProperty("error", out _), root.GetRawText());
        Assert.True(root.GetProperty("succeeded").GetBoolean(), root.GetRawText());
    }

    /// <summary>A line-range edit's symbolId cross-check rejects lines that fall outside that symbol's own declaration span, before any classification runs.</summary>
    [Fact]
    public async Task ValidatePatch_LinesWithSymbolId_SpanMismatchReturnsEditOutsideSymbol()
    {
        var sym = Root(await GetSymbol("Sample.Lib.Widget.Spin", "all"));
        var symbolId = sym.GetProperty("symbolId").GetString()!;
        var version = sym.GetProperty("contentVersion").GetString()!;

        var edits = new[] { new PatchEditInput(File: "Lib/Widget.cs", SymbolId: symbolId, Lines: "1-1", NewText: "// not Spin's line") };
        var root = Root(await ContextToolsValidate(new Dictionary<string, string> { [symbolId] = version }, edits,
            applyOnSuccess: false, intent: "should be rejected"));

        Assert.Equal("edit_outside_symbol", root.GetProperty("error").GetString());
    }

    /// <summary>Find/replace mode locates the unique match inside the symbol's own span and resolves it into an ordinary line-range edit, with no line numbers supplied by the caller.</summary>
    [Fact]
    public async Task ValidatePatch_RequestedLevelTypo_SurfacesUnhonoredHint()
        {
            var sym = Root(await GetSymbol("Sample.Lib.BodyOutlineFixture.TooShortForOutline", "all"));
            var symbolId = sym.GetProperty("symbolId").GetString()!;
            var version = sym.GetProperty("contentVersion").GetString()!;

            var edits = new[] { new PatchEditInput(SymbolId: symbolId, Find: "x + 1", Replace: "x + 1") };
            var root = Root(await PatchTools.ValidatePatch(_f.Workspace, _f.Locator, _f.Symbols, _f.FeatureLog, _f.Builder, _f.TargetedTests, _f.Telemetry,
                new PatchDraftStore(TimeProvider.System),
                new Dictionary<string, string> { [symbolId] = version }, edits, requestedLevel: "solutionvalidate",
                applyOnSuccess: false, intent: "requestedLevel typo test", tags: null, draftId: null));

            Assert.True(root.TryGetProperty("ladder", out var ladderEl), root.GetRawText());
                var hint = ladderEl.GetProperty("requestedLevelHint").GetString();
                Assert.Contains("solution_validate", hint, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ValidatePatch_RequestedLevelValid_CarriesNoHint()
        {
            var sym = Root(await GetSymbol("Sample.Lib.BodyOutlineFixture.TooShortForOutline", "all"));
            var symbolId = sym.GetProperty("symbolId").GetString()!;
            var version = sym.GetProperty("contentVersion").GetString()!;

            var edits = new[] { new PatchEditInput(SymbolId: symbolId, Find: "x + 1", Replace: "x + 1") };
            var root = Root(await PatchTools.ValidatePatch(_f.Workspace, _f.Locator, _f.Symbols, _f.FeatureLog, _f.Builder, _f.TargetedTests, _f.Telemetry,
                new PatchDraftStore(TimeProvider.System),
                new Dictionary<string, string> { [symbolId] = version }, edits, requestedLevel: "project_compile",
                applyOnSuccess: false, intent: "requestedLevel valid test", tags: null, draftId: null));

            Assert.True(root.TryGetProperty("ladder", out var ladderEl), root.GetRawText());
                Assert.False(ladderEl.TryGetProperty("requestedLevelHint", out _));
        }

        [Fact]
        public async Task ValidatePatch_FindReplace_UniqueMatchResolvesAndValidatesNormally()
    {
        var sym = Root(await GetSymbol("Sample.Lib.Widget.Spin", "all"));
        var symbolId = sym.GetProperty("symbolId").GetString()!;
        var version = sym.GetProperty("contentVersion").GetString()!;

        var edits = new[] { new PatchEditInput(SymbolId: symbolId, Find: "turns * 2", Replace: "turns * 3") };
        var root = Root(await ContextToolsValidate(new Dictionary<string, string> { [symbolId] = version }, edits,
            applyOnSuccess: false, intent: "find/replace"));

        Assert.False(root.TryGetProperty("error", out _), root.GetRawText());
        Assert.True(root.GetProperty("succeeded").GetBoolean(), root.GetRawText());
    }

    /// <summary>find/replace errors when the text does not occur anywhere inside the symbol's own span.</summary>
    [Fact]
    public async Task ValidatePatch_FindReplace_NoMatchReturnsFindNotFound()
    {
        var sym = Root(await GetSymbol("Sample.Lib.Widget.Spin", "all"));
        var symbolId = sym.GetProperty("symbolId").GetString()!;
        var version = sym.GetProperty("contentVersion").GetString()!;

        var edits = new[] { new PatchEditInput(SymbolId: symbolId, Find: "this text is not present anywhere", Replace: "x") };
        var root = Root(await ContextToolsValidate(new Dictionary<string, string> { [symbolId] = version }, edits,
            applyOnSuccess: false, intent: "should not resolve"));

        Assert.Equal("find_not_found", root.GetProperty("error").GetString());
    }

    /// <summary>find/replace errors on more than one match unless replaceAll is set, rather than guessing which occurrence was meant.</summary>
    [Fact]
    public async Task ValidatePatch_FindReplace_MultipleMatchesWithoutReplaceAllReturnsAmbiguousFindMatch()
    {
        var sym = Root(await GetSymbol("Sample.Lib.Widget", "all"));
        var symbolId = sym.GetProperty("symbolId").GetString()!;
        var version = sym.GetProperty("contentVersion").GetString()!;

        var edits = new[] { new PatchEditInput(SymbolId: symbolId, Find: "public", Replace: "internal") };
        var root = Root(await ContextToolsValidate(new Dictionary<string, string> { [symbolId] = version }, edits,
            applyOnSuccess: false, intent: "should be ambiguous"));

        Assert.Equal("ambiguous_find_match", root.GetProperty("error").GetString());
    }

    /// <summary>replaceAll: true replaces every occurrence inside the symbol's span instead of requiring exactly one -- including a nested member's own "public", which is why both symbols' versions are held.</summary>
    [Fact]
    public async Task ValidatePatch_FindReplace_ReplaceAllResolvesMultipleMatches()
    {
        var type = Root(await GetSymbol("Sample.Lib.Widget", "all"));
        var typeId = type.GetProperty("symbolId").GetString()!;
        var spin = Root(await GetSymbol("Sample.Lib.Widget.Spin", "all"));
        var spinId = spin.GetProperty("symbolId").GetString()!;

        var edits = new[] { new PatchEditInput(SymbolId: typeId, Find: "public", Replace: "internal", ReplaceAll: true) };
        var root = Root(await ContextToolsValidate(new Dictionary<string, string>
        {
            [typeId] = type.GetProperty("contentVersion").GetString()!,
            [spinId] = spin.GetProperty("contentVersion").GetString()!,
        }, edits, applyOnSuccess: false, intent: "replace every occurrence"));

        Assert.False(root.TryGetProperty("error", out _), root.GetRawText());
    }

    /// <summary>
    /// Each find/replace edit resolves to a rewrite of the symbol's WHOLE declaration span, so two of them
    /// naming one symbol handed the sandbox two rewrites of identical line spans and corrupted the file.
    /// </summary>
    [Fact]
    public async Task ValidatePatch_TwoFindReplacesOnOneSymbol_FoldIntoOneRewrite()
    {
        var type = Root(await GetSymbol("Sample.Lib.Widget", "all"));
        var typeId = type.GetProperty("symbolId").GetString()!;
        var spin = Root(await GetSymbol("Sample.Lib.Widget.Spin", "all"));
        var spinId = spin.GetProperty("symbolId").GetString()!;

        var edits = new[]
        {
            new PatchEditInput(SymbolId: typeId, Find: "public", Replace: "internal", ReplaceAll: true),
            new PatchEditInput(SymbolId: typeId, Find: "internal", Replace: "public", ReplaceAll: true),
        };
        var root = Root(await ContextToolsValidate(new Dictionary<string, string>
        {
            [typeId] = type.GetProperty("contentVersion").GetString()!,
            [spinId] = spin.GetProperty("contentVersion").GetString()!,
        }, edits, applyOnSuccess: false, intent: "two corrections to one symbol"));

        Assert.False(root.TryGetProperty("error", out _), root.GetRawText());
        Assert.True(root.GetProperty("succeeded").GetBoolean(), root.GetRawText());
    }

    /// <summary>
    /// Overlapping spans cannot both be honoured -- the second addresses line numbers the first has already
    /// moved -- so they are refused rather than spliced together and reported as a success.
    /// </summary>
    [Fact]
    public async Task ValidatePatch_OverlappingLineRangeEdits_AreRefused()
    {
        var spin = Root(await GetSymbol("Sample.Lib.Widget.Spin", "all"));
        var spinId = spin.GetProperty("symbolId").GetString()!;
        var site = spin.GetProperty("content").GetProperty("declarationSites")[0];
        var file = site.GetProperty("file").GetString()!;
        var span = $"{site.GetProperty("startLine").GetInt32()}-{site.GetProperty("endLine").GetInt32()}";

        var edits = new[]
        {
            new PatchEditInput(File: file, Lines: span, NewText: "        // replaced"),
            new PatchEditInput(File: file, Lines: span, NewText: "        // replaced again"),
        };
        var root = Root(await ContextToolsValidate(new Dictionary<string, string>
        {
            [spinId] = spin.GetProperty("contentVersion").GetString()!,
        }, edits, applyOnSuccess: false, intent: "overlapping spans"));

        Assert.Equal("invalid_edit", root.GetProperty("error").GetString());
        Assert.Contains("overlapping", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>find/replace resolves against the live workspace only -- amending a draft with one is rejected rather than resolved against stale coordinates.</summary>
    [Fact]
    public async Task ValidatePatch_FindReplaceUnderAmend_ReturnsFindReplaceRequiresFreshPatch()
    {
        var sym = Root(await GetSymbol("Sample.Lib.Widget.Spin", "all"));
        var symbolId = sym.GetProperty("symbolId").GetString()!;
        var version = sym.GetProperty("contentVersion").GetString()!;
        var drafts = new PatchDraftStore(TimeProvider.System);

        var firstEdits = new[] { new PatchEditInput(File: "Lib/Widget.cs", Lines: "12-12", NewText: "    public int Spin(int turns) => turns * 4;") };
        var first = Root(await ContextToolsValidate(new Dictionary<string, string> { [symbolId] = version }, firstEdits,
            applyOnSuccess: false, intent: "first pass", drafts: drafts));
        var draftId = first.GetProperty("draft").GetProperty("draftId").GetString()!;

        var amendEdits = new[] { new PatchEditInput(SymbolId: symbolId, Find: "turns * 4", Replace: "turns * 5") };
        var amended = Root(await ContextToolsValidate(new Dictionary<string, string>(), amendEdits,
            applyOnSuccess: false, intent: "should be rejected", drafts: drafts, draftId: draftId));

        Assert.Equal("find_replace_requires_fresh_patch", amended.GetProperty("error").GetString());
    }

    /// <summary>A malformed lines string is rejected before any classification runs, rather than being silently misparsed.</summary>
    [Fact]
    public async Task ValidatePatch_MalformedLinesString_ReturnsInvalidEdit()
    {
        var edits = new[] { new PatchEditInput(File: "Lib/Widget.cs", Lines: "not-a-range", NewText: "// irrelevant") };
        var root = Root(await ContextToolsValidate(new Dictionary<string, string>(), edits,
            applyOnSuccess: false, intent: "should be rejected"));

        Assert.Equal("invalid_edit", root.GetProperty("error").GetString());
    }


    private Task<string> ContextToolsValidate(Dictionary<string, string> baseVersions, PatchEditInput[] edits, bool applyOnSuccess, string? intent, PatchDraftStore? drafts = null, string? draftId = null) =>
        PatchTools.ValidatePatch(_f.Workspace, _f.Locator, _f.Symbols, _f.FeatureLog, _f.Builder, _f.TargetedTests, _f.Telemetry,
            drafts ?? new PatchDraftStore(TimeProvider.System),
            baseVersions, edits, requestedLevel: null, applyOnSuccess: applyOnSuccess, intent: intent, tags: null, draftId: draftId);

    [Fact]
    public async Task SearchIndex_FindsTopLevelGenericAndNestedDelegates()
    {
        var rows = TableRows(Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Transform Projector Progress",
            kinds: "delegate", groupBy: "none")).GetProperty("items"));

        var names = rows.Select(row => row["name"].GetString()!).ToList();
        Assert.Contains(names, name => name.EndsWith("Transform", StringComparison.Ordinal));
        Assert.Contains(names, name => name.Contains("Projector<TInput, TResult>", StringComparison.Ordinal));
        Assert.Contains(names, name => name.Contains("DelegateSample.Progress", StringComparison.Ordinal));
        Assert.All(rows, row => Assert.True(row.ContainsKey("file") && row.ContainsKey("lines"),
            "a delegate hit must carry its location"));
    }

    [Fact]
    public async Task GetSymbol_OnADelegate_ReportsDelegateKindAndItsDocs()
    {
        var root = Root(await GetSymbol("Sample.Lib.Transform"));
        Assert.Equal("Delegate", root.GetProperty("content").GetProperty("kind").GetString());

        var documented = Root(await GetSymbol("Sample.Lib.Transform", "xmlDoc"));
        Assert.Contains("Transforms an integer",
            documented.GetProperty("content").GetProperty("xmlDoc").GetProperty("summary").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReferences_OnADelegateType_FindsTheMembersDeclaredWithIt()
    {
        var root = Root(await GetReferences("Sample.Lib.Transform", "callers"));
        var displays = TableRows(root.GetProperty("items"))
            .Select(item => item["displayString"].GetString() ?? "").ToList();

        Assert.Equal("delegate", root.GetProperty("dispatchKind").GetString());
        Assert.Contains(displays, d => d.Contains("Apply", StringComparison.Ordinal));
        Assert.Contains(displays, d => d.Contains("Applied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetReferences_OnAMethodUsedAsADelegateTarget_FindsTheConversionSite()
    {
        var root = Root(await GetReferences("Sample.Lib.DelegateSample.Double", "callers"));
        var displays = TableRows(root.GetProperty("items"))
            .Select(item => item["displayString"].GetString() ?? "").ToList();

        Assert.Contains(displays, d => d.Contains("ApplyDouble", StringComparison.Ordinal));
    }
}
