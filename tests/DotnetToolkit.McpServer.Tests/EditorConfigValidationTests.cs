using System.Text.Json;
using DotnetToolkit.McpServer.Tools;
using DotnetToolkit.McpServer.Validation;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>Lets every integration test class share one loaded sample solution.</summary>
[CollectionDefinition("SampleSolution")]
public sealed class SampleSolutionCollection : ICollectionFixture<SampleSolutionFixture>;

/// <summary>
/// Covers what the repo's own .editorconfig is allowed to decide about a patch, and the checks block
/// that reports what a run examined.
/// </summary>
/// <remarks>
/// The severity plumbing these tests exercise is Roslyn's, not this repo's: MSBuildWorkspace builds a
/// SyntaxTreeOptionsProvider from the .editorconfig chain and maps TreatWarningsAsErrors onto
/// GeneralDiagnosticOption, so <c>Diagnostic.Severity</c> is already effective severity. What this repo
/// adds — and what these tests are really guarding — is that analyzers are executed at all, since
/// <c>Compilation.GetDiagnostics()</c> runs none of them, and that a passing run says what it covered.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("SampleSolution")]
public sealed class EditorConfigValidationTests(SampleSolutionFixture f)
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    private Task<string> GetSymbol(string symbol) =>
        ContextTools.GetSymbol(f.Workspace, f.Locator, f.Index, f.Symbols, f.FeatureLog, f.Builder, f.Telemetry, symbol, "all");

    private async Task<JsonElement> Patch(string symbol, string file, int startLine, int endLine, string newText)
    {
        var sym = Root(await GetSymbol(symbol));
        var edits = new[] { new PatchEditInput(File: file, Lines: $"{startLine}-{endLine}", NewText: newText) };
        return Root(await PatchTools.ValidatePatch(
            f.Workspace, f.Locator, f.Symbols, f.FeatureLog, f.Builder, f.TargetedTests, f.Telemetry,
            new PatchDraftStore(TimeProvider.System),
            new Dictionary<string, string>
            {
                [sym.GetProperty("symbolId").GetString()!] = sym.GetProperty("contentVersion").GetString()!,
            },
            edits, requestedLevel: null, applyOnSuccess: false, intent: null, tags: null));
    }

    private static List<string> DiagnosticIds(JsonElement root) =>
        root.TryGetProperty("diagnostics", out var d) && d.ValueKind is not JsonValueKind.Null
            ? [.. d.GetProperty("rootCauses").EnumerateArray().Select(rc => rc.GetProperty("diagnostic").GetString()!)]
            : [];

    private static List<string> AdvisoryIds(JsonElement root, string severity) =>
        [.. root.GetProperty("checks").GetProperty("analyzers").GetProperty(severity)
            .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!)];

    [Fact]
    public async Task EditorConfigPromotingACompilerWarningToError_BlocksThePatch()
    {
        // CS0219 is a warning by default and Lib sets TreatWarningsAsErrors=false; only the .editorconfig
        // section for this file makes it fatal.
        var root = await Patch(
            "Lib.SeveritySample.PromotedWarning", "Lib/SeveritySample.cs", 10, 10,
            "    public static int PromotedWarning() { int unused = 42; return 0; }");

        Assert.False(root.GetProperty("succeeded").GetBoolean(), root.GetRawText());
        Assert.Contains("CS0219", DiagnosticIds(root));
    }

    [Fact]
    public async Task EditorConfigSilencingACompilerWarning_LetsThePatchThrough()
    {
        var root = await Patch(
            "Lib.SeveritySample.SilencedWarning", "Lib/SeveritySample.cs", 13, 13,
            "    public static int SilencedWarning() { int neverUsed; return 0; }");

        Assert.True(root.GetProperty("succeeded").GetBoolean(), root.GetRawText());
        Assert.DoesNotContain("CS0168", DiagnosticIds(root));
    }

    [Fact]
    public async Task AnalyzerRuleAtErrorSeverity_BlocksThePatch()
    {
        // Nothing the compile rungs do can see this: CA1822 comes from an analyzer, and
        // Compilation.GetDiagnostics() never runs one.
        var root = await Patch(
            "Lib.AnalyzerBlockSample.Doubled", "Lib/AnalyzerBlockSample.cs", 16, 16,
            "    internal int Doubled() => 6;");

        Assert.False(root.GetProperty("succeeded").GetBoolean(), root.GetRawText());
        Assert.Contains("CA1822", DiagnosticIds(root));
        Assert.False(root.GetProperty("applied").GetBoolean(), root.GetRawText());
    }

    [Fact]
    public async Task AnalyzerRuleAtWarningSeverity_IsReportedButDoesNotBlock()
    {
        var root = await Patch(
            "Lib.AnalyzerAdvisorySample.Doubled", "Lib/AnalyzerAdvisorySample.cs", 15, 15,
            "    internal int Doubled() => 10;");

        Assert.True(root.GetProperty("succeeded").GetBoolean(), root.GetRawText());
        Assert.Contains("CA1822", AdvisoryIds(root, "warnings"));
        Assert.Empty(DiagnosticIds(root));
    }

    [Fact]
    public async Task APassingRunReportsWhatItCheckedAndWhatItDidNot()
    {
        var root = await Patch(
            "Sample.Lib.Widget.Spin", "Lib/Widget.cs", 12, 12, "    public int Spin(int turns) => turns * 4;");

        Assert.True(root.GetProperty("succeeded").GetBoolean(), root.GetRawText());
        var checks = root.GetProperty("checks");

        // Every rung that ran says what it ran over, so "clean" is never reported without a scope.
        var levels = checks.GetProperty("levels").EnumerateArray().ToList();
        Assert.NotEmpty(levels);
        Assert.All(levels, l => Assert.False(string.IsNullOrWhiteSpace(l.GetProperty("scope").GetString())));

        var analyzers = checks.GetProperty("analyzers");
        Assert.True(analyzers.GetProperty("ran").GetBoolean(), analyzers.GetRawText());
        Assert.True(analyzers.GetProperty("analyzerCount").GetInt32() > 0, analyzers.GetRawText());

        // The gaps are stated rather than left to silence — including the rungs above the one reached and
        // the standing limit that untouched files are not analyzed.
        var notAssessed = checks.GetProperty("notAssessed").EnumerateArray()
            .Select(x => x.GetString()!).ToList();
        Assert.NotEmpty(notAssessed);
        Assert.Contains(notAssessed, s => s.Contains("analyzers covered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedRunReportsTheAnalyzerPassAsNotReachedRatherThanClean()
    {
        var root = await Patch(
            "Lib.SeveritySample.PromotedWarning", "Lib/SeveritySample.cs", 10, 10,
            "    public static int PromotedWarning() { return \"not an int\"; }");

        Assert.False(root.GetProperty("succeeded").GetBoolean(), root.GetRawText());
        var analyzers = root.GetProperty("checks").GetProperty("analyzers");
        Assert.False(analyzers.GetProperty("ran").GetBoolean(), analyzers.GetRawText());
        Assert.Contains("failed", analyzers.GetProperty("skipReason").GetString()!, StringComparison.Ordinal);
    }
}
