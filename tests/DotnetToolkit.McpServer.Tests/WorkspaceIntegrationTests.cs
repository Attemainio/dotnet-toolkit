using System.Diagnostics;
using System.Text.Json;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Output;
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
    /// <summary>How long the .NET CLI gets to exit before the fixture gives up on it.</summary>
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(3);

    /// <summary>How long to keep draining a finished process's pipes before moving on without them.</summary>
    /// <remarks>
    /// Short on purpose. The drain only enriches a failure message, and the pipe may be held open
    /// indefinitely by an MSBuild node the CLI left running, so a generous bound would spend that whole
    /// budget on every successful run.
    /// </remarks>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    public SolutionLocator Locator { get; private set; } = null!;
    public ProjectIndex Index { get; private set; } = null!;
    public WorkspaceHost Workspace { get; private set; } = null!;
    public SymbolStore Symbols { get; private set; } = null!;
    public SymbolIndexBuilder Builder { get; private set; } = null!;
    public CallSlice CallSlice { get; private set; } = null!;
    public TelemetryRecorder Telemetry { get; private set; } = null!;
    public KnowledgeStore Store => _store;

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
        // isolated so rename_symbol's disk writes never pollute the repo/bin fixture).
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
        Builder = new SymbolIndexBuilder(Workspace, Symbols, _store, Locator, NullLogger<SymbolIndexBuilder>.Instance, Index);
        await Builder.RebuildAsync();
        Assert.True(Builder.Ready);
        Telemetry = new TelemetryRecorder(_store, NullLogger<TelemetryRecorder>.Instance);
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

    /// <summary>Runs the .NET CLI to completion and asserts it succeeded.</summary>
    /// <param name="args">The CLI arguments.</param>
    /// <param name="workingDir">The directory to run in.</param>
    /// <remarks>
    /// <para>
    /// <strong>Process exit is the completion signal here; EOF on its pipes is not.</strong>
    /// <c>dotnet restore</c> spawns MSBuild worker nodes that inherit the redirected write-ends and
    /// outlive it — measured on this fixture, restore returns in under a second and leaves ten of them
    /// running, with <c>MSBUILDDISABLENODEREUSE=1</c> and <c>DOTNET_CLI_USE_MSBUILD_SERVER=0</c> both set.
    /// Those switches reduce the behavior but do not remove it on every SDK, so an earlier version that
    /// waited for EOF hung until its own timeout and then failed the entire collection — 205 tests — over
    /// output nobody reads unless the command failed.
    /// </para>
    /// <para>
    /// The two pipes are still drained concurrently rather than one after the other, because that part of
    /// the hazard is real: reading one to completion first deadlocks the moment the child fills the other
    /// pipe's buffer. What changed is that the drain is now best-effort and short — it decorates a failure
    /// message, it does not decide the verdict.
    /// </para>
    /// </remarks>
    private static async Task RunDotnet(string args, string workingDir)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var psi = new ProcessStartInfo(dotnet, args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Fewer surviving nodes is still better than more, even though it is not a guarantee.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        // InitializeAsync registered an SDK before this call, and the child would otherwise inherit it.
        // Measured here as a 10.0.111 host handed 10.0.301's MSBuild.dll: exit 1, nothing written to
        // either stream, and the whole collection failed on what looked like a hung pipe.
        MSBuildRegistration.ScrubInheritedRegistration(psi);

        using var process = Process.Start(psi)!;

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(ProcessTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            Assert.Fail($"dotnet {args} did not exit within {ProcessTimeout.TotalMinutes:0} minute(s)");
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(DrainGrace);
        }
        catch (TimeoutException)
        {
            // A surviving MSBuild node still holds the write-end. The exit code is the contract.
        }

        Assert.True(
            process.ExitCode == 0,
            $"dotnet {args} failed with exit code {process.ExitCode}:\n{Captured(stdoutTask)}\n{Captured(stderrTask)}");
    }

    /// <summary>Reads a stream drain that may not have finished.</summary>
    /// <param name="read">The drain task.</param>
    /// <returns>What was read, or a note distinguishing why it is absent.</returns>
    /// <remarks>
    /// The two absent cases are told apart deliberately. A drain still running means a build node holds
    /// the write-end and the output is merely late; a faulted one means the read itself broke, which is a
    /// different problem with a different fix — and collapsing them into one message sends the reader
    /// hunting for a stray process that was never there.
    /// </remarks>
    private static string Captured(Task<string> read) => read.Status switch
    {
        TaskStatus.RanToCompletion => read.Result,
        TaskStatus.Faulted => $"(read failed: {read.Exception?.GetBaseException().Message})",
        _ => "(not captured: a surviving build node still held the pipe open)",
    };
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

    private Task<string> GetSymbol(string symbol, string? include = null) =>
        ContextTools.GetSymbol(_f.Workspace, _f.Locator, _f.Index, _f.Symbols, _f.Builder, _f.Telemetry,
            symbol, include);

    private Task<string> GetSymbols(string[] symbols, string? include = null) =>
        ContextTools.GetSymbol(_f.Workspace, _f.Locator, _f.Index, _f.Symbols, _f.Builder, _f.Telemetry,
            symbol: null, include, symbols: symbols);

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
            _f.Workspace, _f.Locator, _f.Index, _f.Symbols, _f.Builder, _f.Telemetry,
            "Lib.TurboWidget");

        var root = Root(json);
        Assert.False(root.TryGetProperty("error", out _));
        Assert.True(root.TryGetProperty("symbolId", out _));
    }

    /// <summary>
    /// A project count is not actionable when one project of a solution fails to load: the caller
    /// cannot tell which results are degraded. Status must name the projects it actually loaded.
    /// </summary>
    [Fact]
    public void WorkspaceStatus_NamesLoadedProjects()
    {
        var status = ServerTools.WorkspaceStatus(_f.Locator, _f.Index, _f.Workspace, _f.Store, _f.Builder);

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
    /// A batch entry that fails to resolve has no symbolId/content to offer — its result is
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
        Assert.Equal("symbol_not_found", rows[1]["error"].GetString());
    }

    [Fact]
    public async Task GetSymbol_MissingBothSymbolAndSymbolsIsAnError()
    {
        var result = Root(await ContextTools.GetSymbol(_f.Workspace, _f.Locator, _f.Index, _f.Symbols,
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
        // The standard default is gone: an explicit list is exactly what was asked for, nothing implied --
        // except xmlDoc, which a single-symbol lookup carries whatever include said (next test).
        Assert.False(withMembers.GetProperty("content").TryGetProperty("referenceCounts", out _));
        Assert.False(withMembers.GetProperty("content").TryGetProperty("source", out _));
    }

    /// <summary>
    /// A single-symbol lookup always carries the doc comment, even when include named other components:
    /// it is the one line the caller is about to act on, and the 2026-09-20 FluidScript run had every
    /// route delete a method whose remarks said not to, the MCP route because include:"members" had
    /// dropped xmlDoc. A batch keeps include literal, since a doc block per row is real cost there.
    /// </summary>
    [Fact]
    public async Task GetSymbol_SingleLookupAlwaysCarriesXmlDoc_BatchKeepsIncludeLiteral()
    {
        var single = Root(await GetSymbol("Sample.Lib.Widget", include: "members")).GetProperty("content");
        Assert.Equal("A spinning widget.", single.GetProperty("xmlDoc").GetProperty("summary").GetString());
        Assert.True(single.TryGetProperty("members", out _));

        var batch = Root(await GetSymbols(["Sample.Lib.Widget"], include: "members"));
        var row = TableRows(batch.GetProperty("results"))[0]["content"];
        Assert.False(row.TryGetProperty("xmlDoc", out _));
        Assert.True(row.TryGetProperty("members", out _));
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
    /// literal, which silently omitted containingType. One build path means a component
    /// appears whenever it is asked for, regardless of which other components were requested alongside it.
    /// </summary>
    [Fact]
    public async Task GetSymbol_MembersRequestCarriesTheSameSkeletonAsEveryOtherRequest()
    {
        var outline = Root(await GetSymbol("Sample.Lib.Widget", "xmlDoc,referenceCounts,members"));
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
    /// A type declaring more members than the cap is truncated rather than served whole: BigMemberFixture
    /// declares 45 auto-properties, well past MembersCap, so the list stops at 40 and says so.
    /// </summary>
    [Fact]
    public async Task GetSymbol_Members_CapsAndReportsTruncationOnAManyMemberType()
    {
        var content = Root(await GetSymbol("Sample.Lib.BigMemberFixture", include: "members")).GetProperty("content");

        Assert.Equal(40, content.GetProperty("members").GetArrayLength());
        Assert.Equal(45, content.GetProperty("totalMembers").GetInt32());
        Assert.True(content.GetProperty("truncated").GetBoolean());
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
    /// <summary>
    /// A caller that binds to the interface member reaches an implementation only by dispatch. The row is
    /// marked indirect and names the member it actually binds to in via, and the envelope says so once in
    /// dispatchNote -- on the 2026-09-20 FluidScript run a column of indirect:true cells was read past and
    /// eight callers reported where three had the implementing type as their receiver.
    /// </summary>
    [Fact]
    public async Task GetReferences_IndirectCallerNamesTheMemberItBindsTo()
    {
        var root = Root(await GetReferences("Sample.Lib.Widget.Spin", "callers"));
        var rows = TableRows(root.GetProperty("items"));

        var viaInterface = Assert.Single(rows, r => r["displayString"].GetString()!.Contains("SpinTwice"));
        Assert.True(viaInterface["indirect"].GetBoolean());
        Assert.Equal("IWidget.Spin/1", viaInterface["via"].GetString());
        Assert.All(rows.Where(r => !r.ContainsKey("indirect")), r => Assert.False(r.ContainsKey("via")));

        Assert.Equal(2, root.GetProperty("directItems").GetInt32());
        Assert.Equal(1, root.GetProperty("indirectItems").GetInt32());
        Assert.Contains("1 of 3 items are indirect", root.GetProperty("dispatchNote").GetString());
    }

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
    public async Task GetReferences_EntryPointHint_AppearsForZeroCallerTestMethod()
    {
        var root = Root(await GetReferences("Sample.Lib.OrphanTestSample.NeverCalledDirectly", "callers"));

        Assert.Equal(0, root.GetProperty("totalItems").GetInt32());
        Assert.True(root.TryGetProperty("entryPointHint", out var hint));
        Assert.Contains("reflection", hint.GetString());
    }

    [Fact]
    public async Task GetReferences_EntryPointHint_AbsentForOrdinaryZeroCallerMethod()
    {
        var root = Root(await GetReferences("Sample.Lib.DocSectionsFixture.Undocumented", "callers"));

        Assert.Equal(0, root.GetProperty("totalItems").GetInt32());
        Assert.False(root.TryGetProperty("entryPointHint", out _));
    }

    [Fact]
    public async Task GetReferences_EntryPointHint_AppearsForZeroCallerMcpToolMethod()
    {
        var root = Root(await GetReferences("Sample.Lib.OrphanToolSample.NeverCalledDirectly", "callers"));

        Assert.Equal(0, root.GetProperty("totalItems").GetInt32());
        Assert.True(root.TryGetProperty("entryPointHint", out var hint));
        Assert.Contains("McpServerTool", hint.GetString());
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

    // A sym_... id handed out by any response is itself a valid retrieval target, so
    // suggestedInspection / search hits / reference items round-trip without name guessing.
    [Fact]
    public async Task GetSymbol_AcceptsSymbolIdHandle()
    {
        var byName = Root(await GetSymbol("Sample.Lib.Widget"));
        var symbolId = byName.GetProperty("symbolId").GetString()!;

        var byId = Root(await GetSymbol(symbolId));

        Assert.Equal(symbolId, byId.GetProperty("symbolId").GetString());
        Assert.Equal(byName.GetProperty("content").GetProperty("displayString").GetString(), byId.GetProperty("content").GetProperty("displayString").GetString());
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
    /// One item can carry several sites, so totalItems is not the call-site count and adding the rows up by
    /// hand is what loses one. totalSites answers it, over the whole result rather than the page.
    /// </summary>
    [Fact]
    public async Task GetReferences_ItemWithTwoCallSites_ReportsTotalSitesBesideTotalItems()
    {
        var root = Root(await GetReferences("Sample.Lib.CallSiteSample.Tick", "callers"));

        Assert.Equal(1, root.GetProperty("totalItems").GetInt32());
        Assert.Equal(2, root.GetProperty("totalSites").GetInt32());
    }

    /// <summary>One site per item leaves totalSites nothing to say that totalItems has not already said.</summary>
    [Fact]
    public async Task GetReferences_OneSitePerItem_OmitsTotalSites()
    {
        var root = Root(await GetReferences("Sample.Lib.RenameSample.Seed", "callers"));

        Assert.True(root.GetProperty("totalItems").GetInt32() > 1);
        Assert.False(root.TryGetProperty("totalSites", out _));
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
    /// members' file:"" convention (this type's own primary declaration file) gets a legend, the same as
    /// shape/refs/read/modifiers -- only when the table actually mixes a blank and a named file, which a
    /// partial split across two files does (2026-08-13 self-eval finding 11).
    /// </summary>
    [Fact]
    public async Task GetSymbol_PartialClass_MembersCarriesAFileLegendWhenFilesMix()
    {
        var content = Root(await GetSymbol("Sample.Lib.Gadget", include: "members")).GetProperty("content");
        var members = content.GetProperty("members").EnumerateArray().ToList();

        Assert.Contains(members, m => !m.TryGetProperty("file", out var f) || f.GetString() == "");
        Assert.Contains(members, m => m.TryGetProperty("file", out var f) && f.GetString()!.EndsWith("Gadget.Extra.cs", StringComparison.Ordinal));
        Assert.Contains("primary declaration file", content.GetProperty("fileLegend").GetString());
    }

        /// <summary>
        /// Widget.Spin has a /// doc comment on the line directly above its signature. declarationSites
        /// must start AT the comment, not at the signature — otherwise a ranged Read built from the span
        /// never shows the comment, and an Edit anchored on that read cannot touch it.
        /// </summary>
        [Fact]
        public async Task GetSymbol_DeclarationSpanIncludesTheLeadingDocComment_AndEndsAtTheClosingBrace()
        {
            // The line contract a caller's Read(file, offset, limit) depends on: startLine is where a
            // leading /// comment begins, signatureLine where the declaration itself does, endLine the
            // closing brace (or the last line of an expression-bodied member). Nothing else states it.
            var root = Root(await GetSymbol("Sample.Lib.Widget"));
            var site = root.GetProperty("content").GetProperty("declarationSites")[0];

            var startLine = site.GetProperty("startLine").GetInt32();
            var endLine = site.GetProperty("endLine").GetInt32();
            var signatureLine = site.GetProperty("signatureLine").GetInt32();
            var fileLines = await File.ReadAllLinesAsync(_f.Locator.AbsPath(site.GetProperty("file").GetString()!));

            Assert.Contains("///", fileLines[startLine - 1]);
            Assert.Contains("class Widget", fileLines[signatureLine - 1]);
            Assert.Equal("}", fileLines[endLine - 1].Trim());
            Assert.True(signatureLine > startLine);

            // Always present, even when nothing widened the span: an undocumented member reports its own
            // line twice rather than an absent field a table renders as an empty cell.
            var undocumented = Root(await GetSymbol("Sample.Lib.TurboWidget.Spin")).GetProperty("content")
                .GetProperty("declarationSites")[0];
            Assert.Equal(undocumented.GetProperty("startLine").GetInt32(), undocumented.GetProperty("signatureLine").GetInt32());

            // No source text rides along: the text comes from a Read over exactly that range.
            Assert.False(root.GetProperty("content").TryGetProperty("source", out _));
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

    [Fact]
    public async Task RenameSymbol_InterfaceMember_DryRunReachesImplementersAndCrossProjectCallers()
    {
        var sym = Root(await GetSymbol("Sample.Lib.IWidget.Spin"));
        var root = Root(await RenameSymbolCall(sym.GetProperty("symbolId").GetString()!, "Rotate"));

        Assert.False(root.TryGetProperty("error", out _), root.GetRawText());
        Assert.False(root.GetProperty("applied").GetBoolean());   // dry run: nothing reaches disk
        Assert.Contains("Dry run", root.GetProperty("notChecked").GetString());

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

        // Program.cs carries the name in a comment and a string literal as well as in the call. Both
        // extra occurrences are textual guesses, so they must stay opt-in: the default run has to leave
        // them alone, and asking for comments has to actually reach one.
        var byDefault = RenamedFiles(Root(await RenameSymbolCall(symbolId, "Rotate")));
        var withComments = RenamedFiles(Root(await RenameSymbolCall(symbolId, "Rotate", renameInComments: true)));

        var program = byDefault.Keys.Single(f => f.EndsWith("App/Program.cs", StringComparison.Ordinal));
        Assert.True(withComments[program] > byDefault[program],
            $"renameInComments should add edits in Program.cs: {byDefault[program]} -> {withComments[program]}");
    }

    [Fact]
    public async Task RenameSymbol_AppliesRewritesEveryReferenceAndRefreshesTheIndex()
    {
        // This apply reaches DISK, and every test in this class shares one fixture copy, so the target has to
        // be one nothing else reads: RenameApplySample exists only for this test. Aimed at RenameSample, it
        // broke whichever sibling xUnit scheduled next -- an order that moves whenever the assembly changes,
        // so the failure looked unrelated and landed on a different test each build.
        var sym = Root(await GetSymbol("Sample.Lib.RenameApplySample.Seed"));
        var root = Root(await RenameSymbolCall(sym.GetProperty("symbolId").GetString()!, "Origin", apply: true));

        Assert.False(root.TryGetProperty("error", out _), root.GetRawText());
        Assert.True(root.GetProperty("applied").GetBoolean(), root.GetRawText());
        Assert.Contains("dotnet build", root.GetProperty("notChecked").GetString());

        var rename = root.GetProperty("rename");
        Assert.Equal("Origin", rename.GetProperty("newName").GetString());
        Assert.Equal(1, rename.GetProperty("filesChanged").GetInt32());
        // The declaration plus every reference: Doubled(), RenameApplySampleUser.Use, and the <see cref="Seed"/>.
        Assert.True(rename.GetProperty("occurrencesRewritten").GetInt32() >= 3, root.GetRawText());

        // The apply refreshed the store in the same call: the new id resolves and the new name is findable
        // without a reload, and the old id is gone.
        var newSymbolId = rename.GetProperty("newSymbolId").GetString()!;
        Assert.NotEqual(sym.GetProperty("symbolId").GetString(), newSymbolId);
        Assert.Equal("Sample.Lib.RenameApplySample.Origin()", _f.Symbols.FqNameFor(newSymbolId));
        Assert.Null(_f.Symbols.FqNameFor(sym.GetProperty("symbolId").GetString()!));
    }

    [Fact]
    public async Task RenameSymbol_RenamingAType_ReportsANewSymbolIdAndTheFileRenameHint()
    {
        var sym = Root(await GetSymbol("Sample.Lib.RenameSampleUser"));
        var oldSymbolId = sym.GetProperty("symbolId").GetString()!;
        var root = Root(await RenameSymbolCall(oldSymbolId, "RenameSampleConsumer"));

        Assert.False(root.TryGetProperty("error", out _), root.GetRawText());

        // A symbol id hashes the fully-qualified name, so a renamed type's id has to differ from the old
        // one - and the tool has to find the renamed symbol by its declaration, not by looking the old id up.
        var rename = root.GetProperty("rename");
        Assert.Equal("Type", rename.GetProperty("kind").GetString());
        Assert.NotEqual(oldSymbolId, rename.GetProperty("newSymbolId").GetString());
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
        var root = Root(await RenameSymbolCall(sym.GetProperty("symbolId").GetString()!, "Sprocket"));

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
        var root = Root(await RenameSymbolCall(sym.GetProperty("symbolId").GetString()!, "class"));

        Assert.Equal("invalid_name", root.GetProperty("error").GetString());
    }

    /// <summary>Relative file path -> edit count, from a rename response's files array.</summary>
    private static Dictionary<string, int> RenamedFiles(JsonElement root) =>
        root.GetProperty("files").EnumerateArray()
            .ToDictionary(f => f.GetProperty("file").GetString()!, f => f.GetProperty("occurrences").GetInt32(), StringComparer.Ordinal);

    private Task<string> RenameSymbolCall(
        string symbol, string newName, bool apply = false, bool renameInComments = false) =>
        RenameTools.RenameSymbol(_f.Workspace, _f.Locator, _f.Symbols, _f.Builder, _f.Telemetry,
            symbol, newName, apply,
            renameOverloads: false, renameInComments: renameInComments, renameInStrings: false, taskId: null);

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

    /// <summary>An implements: name that resolves to no interface carries implementsHint, distinct from a resolved interface with zero implementers.</summary>
    [Fact]
    public async Task SearchIndex_ImplementsFilter_UnresolvedName_CarriesImplementsHint()
    {
        var root = Root(await ContextTools.SearchIndex(_f.Symbols, _f.Index, _f.Workspace, _f.Telemetry,
            "Widget", implements: "NotARealInterfaceZzz", limit: 10, groupBy: "none"));

        Assert.Empty(TableRows(root.GetProperty("items")));
        Assert.Contains("NotARealInterfaceZzz", root.GetProperty("implementsHint").GetString());
    }

    /// <summary>
    /// A resolved interface whose implementers the query excluded says so in implementsHint — the
    /// empty page is the filter working, not "nothing implements it" — and names the listing call.
    /// </summary>
    [Fact]
    public async Task SearchIndex_ImplementsFilter_QueryExcludingEveryImplementer_SaysSo()
    {
        var root = Root(await ContextTools.SearchIndex(_f.Symbols, _f.Index, _f.Workspace, _f.Telemetry,
            "Gear", kinds: "class", implements: "IWidget", limit: 10, groupBy: "none"));

        var hint = root.GetProperty("implementsHint").GetString()!;
        Assert.Contains("direct implementer", hint);
        Assert.DoesNotContain("did not resolve", hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// query:"IWidget" implements:"IWidget" lists the implementers — the obvious call shape for "who
    /// implements IWidget", which ranking the query could never answer (the interface's own name shares
    /// no token with its implementers' names).
    /// </summary>
    [Theory]
    [InlineData("IWidget")]
    [InlineData("Sample.Lib.IWidget")]
    [InlineData("iwidget")]
    public async Task SearchIndex_ImplementsFilter_QueryNamingTheInterface_ListsItsImplementers(string query)
    {
        var root = Root(await ContextTools.SearchIndex(_f.Symbols, _f.Index, _f.Workspace, _f.Telemetry,
            query, implements: "IWidget", limit: 10, groupBy: "none"));

        var names = TableRows(root.GetProperty("items")).Select(i => i["name"].GetString()).ToList();
        Assert.Contains("Sample.Lib.Widget", names);
        Assert.Contains("Sample.Lib.TurboWidget", names);
        Assert.DoesNotContain("Sample.Lib.IWidget", names);
        Assert.False(root.TryGetProperty("implementsHint", out _));
        Assert.False(root.TryGetProperty("termsWithNoHits", out _));
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

    [Fact]
    public async Task GetProjectGraph_ReportsProjectsAndTotalCount()
    {
        var root = Root(await GraphTools.GetProjectGraph(_f.Workspace, _f.Telemetry));

        var projects = root.GetProperty("projects").EnumerateArray().ToList();
        Assert.NotEmpty(projects);
        Assert.Equal(projects.Count, root.GetProperty("totalProjectsInSolution").GetInt32());
        Assert.True(projects[0].TryGetProperty("references", out _));
        Assert.True(projects[0].TryGetProperty("referencedBy", out _));

        // App declares Lib in its .csproj, so the edge is direct in both directions and the transitive
        // lists -- for references the SDK flowed in that no file names -- are absent, not empty.
        var app = Assert.Single(projects, p => p.GetProperty("name").GetString() == "App");
        Assert.Equal(["Lib"], app.GetProperty("references").EnumerateArray().Select(r => r.GetString()!).ToArray());
        Assert.False(app.TryGetProperty("transitiveReferences", out _));
        var lib = Assert.Single(projects, p => p.GetProperty("name").GetString() == "Lib");
        Assert.Equal(["App"], lib.GetProperty("referencedBy").EnumerateArray().Select(r => r.GetString()!).ToArray());
        Assert.False(lib.TryGetProperty("transitivelyReferencedBy", out _));
    }

    /// <summary>
    /// The declared-reference reader is what separates a .csproj's own ProjectReference items from the
    /// ones the SDK flows in transitively; it reads includes as written and gives up on a file it cannot
    /// parse rather than guessing.
    /// </summary>
    [Fact]
    public void DeclaredProjectReferenceIncludes_ReadsIncludesAsWritten()
    {
        var dir = Directory.CreateTempSubdirectory("csproj-refs-").FullName;
        try
        {
            var path = Path.Combine(dir, "X.csproj");
            File.WriteAllText(path, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <ProjectReference Include="..\Lib\Lib.csproj" />
                    <ProjectReference Include="../Other/Other.csproj" Condition="'$(A)'=='b'" />
                    <PackageReference Include="xunit" Version="2" />
                  </ItemGroup>
                </Project>
                """);
            Assert.Equal(["..\\Lib\\Lib.csproj", "../Other/Other.csproj"], GraphTools.DeclaredProjectReferenceIncludes(path));

            File.WriteAllText(path, "<Project><ItemGroup>");
            Assert.Null(GraphTools.DeclaredProjectReferenceIncludes(path));
            Assert.Null(GraphTools.DeclaredProjectReferenceIncludes(Path.Combine(dir, "missing.csproj")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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

    /// <summary>A zero-hit page carries no refsHint: there are no rows for the column to have annotated, and refs is on by default rather than explicitly requested.</summary>
    [Fact]
    public async Task SearchIndex_ZeroHits_CarriesNoRefsHint()
    {
        var root = Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "ZzzNoSuchSymbolAnywhere", groupBy: "none"));

        Assert.False(root.TryGetProperty("refsHint", out _));
    }

    /// <summary>
    /// A partial type's fragments collapse to one hit, so that hit carries parts -- how many files the
    /// declaration is split across -- and the response names the call that enumerates them. Without it a
    /// ranked page reads as an enumeration, which is how a 13-file partial was answered as 5 files
    /// (2026-08-17 perf benchmark, Q4).
    /// </summary>
    [Fact]
    public async Task SearchIndex_PartialType_CarriesPartsAndNamesTheEnumeratingCall()
    {
        var root = Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Gadget", kinds: "class", groupBy: "none"));

        var gadget = TableRows(root.GetProperty("items")).Select(MergedRow)
            .Single(r => r["name"].GetString()!.EndsWith("Gadget", StringComparison.Ordinal));

        Assert.Equal(2, gadget["parts"].GetInt32());
        Assert.Contains("declarationSites", root.GetProperty("partsHint").GetString());
    }

    /// <summary>A one-file declaration carries neither column: the row's own file is the whole answer.</summary>
    [Fact]
    public async Task SearchIndex_SingleFileType_CarriesNoParts()
    {
        var root = Root(await ContextTools.SearchIndex(
            _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Pipeline", kinds: "class", groupBy: "none"));

        Assert.False(root.TryGetProperty("partsHint", out _));
        Assert.All(TableRows(root.GetProperty("items")), row => Assert.False(row.ContainsKey("parts")));
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
    /// With the tree included, a single truncated node (the root, at maxDepth 1) already states
    /// truncated/omittedChildren -- blastRadius does not repeat the identical numbers a second time
    /// (2026-08-13 self-eval finding 8).
    /// </summary>
    [Fact]
    public async Task GetCallHierarchy_IncludeTreeTrue_SuppressesBlastRadiusDuplicateWhenRootCarriesTheWholeTotal()
    {
        var root = Root(await FlowTools.GetCallHierarchy(
            _f.Workspace, _f.Symbols, _f.Index, _f.Builder, _f.Telemetry, "Sample.Lib.Widget.Spin",
            maxDepth: 1, maxChildrenPerNode: 1, includeTree: true));

        var tree = root.GetProperty("tree");
        Assert.True(tree.GetProperty("truncated").GetBoolean());
        Assert.True(tree.GetProperty("omittedChildren").GetInt32() > 0);

        var blastRadius = root.GetProperty("blastRadius");
        Assert.False(blastRadius.TryGetProperty("truncated", out _));
        Assert.False(blastRadius.TryGetProperty("omittedChildren", out _));
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
        /// The structural hint is scoped to queries built entirely from structural words. Any other empty
        /// page gets the names-only hint instead: the index holds identifiers and doc summaries, never
        /// bodies, and an empty list alone does not say so -- a caller searching for a string literal
        /// otherwise re-phrases the same unindexed text two or three times before giving up.
        /// </summary>
        [Fact]
        public async Task SearchIndex_ZeroHitQueryWithARealTerm_SaysNamesOnly()
        {
            var root = Root(await ContextTools.SearchIndex(
                _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "asyncdisposable ZzzNoSuchSymbolAnywhereXyz123", groupBy: "none"));

            Assert.Empty(TableRows(root.GetProperty("items")));
            var hint = root.GetProperty("hint").GetString();
            Assert.Contains("string literals", hint, StringComparison.Ordinal);
            Assert.Contains("Grep", hint, StringComparison.Ordinal);
            Assert.DoesNotContain("structural filter", hint, StringComparison.Ordinal);
        }

        /// <summary>A page with hits carries no hint at all; the hint is a zero-hit explanation, not a banner.</summary>
        [Fact]
        public async Task SearchIndex_QueryWithHits_CarriesNoHint()
        {
            var root = Root(await ContextTools.SearchIndex(
                _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Widget", groupBy: "none"));

            Assert.NotEmpty(TableRows(root.GetProperty("items")));
            Assert.False(root.TryGetProperty("hint", out _));
        }

        /// <summary>
        /// A caller that read kinds as a list sends it as a serialised JSON array. That is the same filter
        /// with brackets and quotes around it, so it is parsed as one rather than answered with a kindsHint
        /// about a token named '["method"' -- measured as a live failure on FluidScript (2026-09-19).
        /// </summary>
        [Theory]
        [InlineData("[\"method\", \"property\"]")]
        [InlineData("['method','property']")]
        [InlineData("method property")]
        public async Task SearchIndex_KindsAsAJsonArrayString_IsParsedAsTheSameFilter(string kinds)
        {
            var root = Root(await ContextTools.SearchIndex(
                _f.Symbols, _f.Index, _f.Workspace, _f.Telemetry, "Spin", kinds: kinds, groupBy: "none"));

            var rows = TableRows(root.GetProperty("items"));
            Assert.NotEmpty(rows);
            Assert.All(rows, r => Assert.Equal("Method", r["kind"].GetString()));
            Assert.False(root.TryGetProperty("kindsHint", out _));
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
