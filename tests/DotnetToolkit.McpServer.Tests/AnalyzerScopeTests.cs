using DotnetToolkit.McpServer.Validation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// The line-span diff that decides which analyzer suggestions a patch actually caused. Suggestions scale
/// with the size of the changed FILE rather than of the change, so without this a one-method rename
/// reported findings from lines it never touched and the caller had to triage them.
/// </summary>
public sealed class AnalyzerScopeTests
{
    private const string BaseSource = """
        namespace Demo;

        public sealed class Widget
        {
            public int First() => 1;

            public int Second() => 2;

            public int Third() => 3;

            public int Fourth() => 4;
        }
        """;

    private static (Solution Solution, DocumentId DocId) NewSolution()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Demo", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Widget.cs", SourceText.From(BaseSource));
        return (workspace.CurrentSolution, document.Id);
    }

    private static async Task<(List<(int Start, int End)> Spans, SourceText Text)> SpansOfAsync(
        Solution before, Solution after, DocumentId docId)
    {
        var spans = await AnalyzerRunner.ChangedLineSpansAsync(
            after.GetDocument(docId)!, before.GetDocument(docId), CancellationToken.None);
        return (Assert.IsType<List<(int Start, int End)>>(spans), await after.GetDocument(docId)!.GetTextAsync());
    }

    private static bool Covers(List<(int Start, int End)> spans, int offset) =>
        spans.Exists(s => s.Start <= offset && offset <= s.End);

    /// <summary>A text-lineage fork is the ordinary validate_patch shape.</summary>
    [Fact]
    public async Task AnEditedLineIsTheOnlyLineReportedAsChanged()
    {
        var (before, docId) = NewSolution();
        var after = before.WithDocumentText(
            docId, SourceText.From(BaseSource.Replace("public int Third() => 3;", "public int Third() => 33;")));

        var (spans, text) = await SpansOfAsync(before, after, docId);

        Assert.Contains("=> 33;", text.ToString()[spans[0].Start..spans[0].End]);
        Assert.True(Covers(spans, text.ToString().IndexOf("Third", StringComparison.Ordinal)));
        Assert.False(Covers(spans, text.ToString().IndexOf("First", StringComparison.Ordinal)));
        Assert.False(Covers(spans, text.ToString().IndexOf("Fourth", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The rename shape, and the regression this file exists for. Roslyn's rename replaces the document's
    /// syntax ROOT, so the new text shares no change-tracking lineage with the old one — and
    /// <c>SourceText.GetChangeRanges</c> answers that with a single range covering the whole file. The filter
    /// then passed every pre-existing suggestion through while reporting itself as working, which is a worse
    /// failure than not filtering at all: measured, a one-method rename still reported five findings from
    /// lines 824–1357 of a file it changed only at 2412–2521.
    /// </summary>
    [Fact]
    public async Task AFreshSyntaxRootStillNarrowsToTheChangedLine()
    {
        var (before, docId) = NewSolution();
        var renamed = CSharpSyntaxTree.ParseText(
            BaseSource.Replace("public int Third() => 3;", "public int Renamed() => 3;")).GetRoot();
        var after = before.WithDocumentSyntaxRoot(docId, renamed);

        var (spans, text) = await SpansOfAsync(before, after, docId);

        Assert.True(Covers(spans, text.ToString().IndexOf("Renamed", StringComparison.Ordinal)));
        Assert.False(Covers(spans, text.ToString().IndexOf("First", StringComparison.Ordinal)));
        Assert.False(Covers(spans, text.ToString().IndexOf("Fourth", StringComparison.Ordinal)));

        // The precise shape of the old bug: one span, and it was the entire document.
        Assert.True(spans.Sum(s => s.End - s.Start) < text.Length);
    }

    /// <summary>No previous version to compare against means no filtering, not empty filtering.</summary>
    [Fact]
    public async Task NoPreviousDocumentDisablesFilteringRatherThanSuppressingEverything()
    {
        var (before, docId) = NewSolution();

        Assert.Null(await AnalyzerRunner.ChangedLineSpansAsync(
            before.GetDocument(docId)!, null, CancellationToken.None));
    }
}
