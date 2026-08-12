using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotnetToolkit.McpServer.Validation;

/// <summary>
/// Runs the analyzers a project already references (Microsoft.CodeAnalysis.NetAnalyzers, any NuGet
/// analyzer package, StyleCop, …) over the documents a patch touched, at the severities the repo's
/// .editorconfig configures.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists at all: <c>Compilation.GetDiagnostics()</c> — what <see cref="ValidationLadder"/>'s
/// compile levels call — returns only the *compiler's* own diagnostics. It never runs a single
/// <see cref="DiagnosticAnalyzer"/>, even though MSBuildWorkspace faithfully populates
/// <c>Project.AnalyzerReferences</c> (eight of them on a bare net10 project). So every <c>CA</c>/<c>IDE</c>
/// rule — the bulk of what an .editorconfig actually configures and what Visual Studio shows in the error
/// list — was invisible to validation, and a patch could pass the ladder and then fail <c>dotnet build</c>.
/// </para>
/// <para>
/// Severity, by contrast, needed no work: MSBuildWorkspace wires a <c>SyntaxTreeOptionsProvider</c> from
/// the .editorconfig chain and maps <c>TreatWarningsAsErrors</c> onto
/// <see cref="CompilationOptions.GeneralDiagnosticOption"/>, so <see cref="Diagnostic.Severity"/> is
/// already the *effective* severity. Passing <see cref="Project.AnalyzerOptions"/> below is what extends
/// that same configuration to analyzer diagnostics; nothing here re-implements severity resolution.
/// </para>
/// <para>
/// Scope is deliberately the changed documents only, via the per-document
/// <see cref="CompilationWithAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(SemanticModel, Microsoft.CodeAnalysis.Text.TextSpan?, System.Threading.CancellationToken)"/>
/// rather than a whole-compilation analysis, which costs seconds per project. The consequence is real and
/// is reported rather than hidden: an analyzer diagnostic that this change provokes in an *untouched*
/// file is not seen here. <see cref="AnalyzerOutcome.Scope"/> carries that limitation into the response.
/// </para>
/// </remarks>
public static class AnalyzerRunner
{
    /// <summary>How long the whole analyzer pass may take before it is abandoned as a skip.</summary>
    /// <remarks>
    /// A third-party analyzer that hangs must degrade validation to "not assessed", never wedge a
    /// validate_patch call. The pass is advisory infrastructure; the compile levels are the gate.
    /// </remarks>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    /// <summary>What one analyzer pass found, and what it could not look at.</summary>
    /// <param name="Ran">False when the pass was skipped entirely; <paramref name="SkipReason"/> says why.</param>
    /// <param name="SkipReason">Human-readable reason the pass did not run, or null when it did.</param>
    /// <param name="AnalyzerCount">How many <see cref="DiagnosticAnalyzer"/> instances were executed.</param>
    /// <param name="DocumentCount">How many changed documents were analyzed.</param>
    /// <param name="DurationMs">Wall-clock cost of the pass.</param>
    /// <param name="Errors">Effective-severity <see cref="DiagnosticSeverity.Error"/> results — these block a patch.</param>
    /// <param name="Warnings">Effective-severity <see cref="DiagnosticSeverity.Warning"/> results — advisory only.</param>
    /// <param name="Suggestions">Effective-severity <see cref="DiagnosticSeverity.Info"/> results — advisory only, and reported only where the patch actually changed text.</param>
    /// <param name="Scope">What the pass covered, stated so a caller can tell clean from unexamined.</param>
    /// <param name="FailedAnalyzers">Analyzers that threw and were dropped; their rules are unassessed.</param>
    /// <param name="PreexistingSuggestions">How many suggestions were withheld because they sit on lines this patch did not touch. Counted rather than listed: a rename of one method reported five findings from elsewhere in the same file, and the caller had to decide per finding whether it was theirs.</param>
    public sealed record AnalyzerOutcome(
        bool Ran,
        string? SkipReason,
        int AnalyzerCount,
        int DocumentCount,
        long DurationMs,
        IReadOnlyList<Diagnostic> Errors,
        IReadOnlyList<Diagnostic> Warnings,
        IReadOnlyList<Diagnostic> Suggestions,
        string Scope,
        IReadOnlyList<string> FailedAnalyzers,
        int PreexistingSuggestions = 0)
    {
        /// <summary>An outcome for a pass that never ran, carrying the reason.</summary>
        public static AnalyzerOutcome Skipped(string reason) =>
            new(false, reason, 0, 0, 0, [], [], [], "nothing analyzed", []);

        /// <summary>True when the pass ran and found nothing at any reported severity.</summary>
        public bool IsClean => Ran && Errors.Count == 0 && Warnings.Count == 0 && Suggestions.Count == 0;
    }

    /// <summary>
    /// Analyzes <paramref name="changedDocs"/> with their projects' referenced analyzers.
    /// </summary>
    /// <param name="forked">The forked solution holding the proposed text.</param>
    /// <param name="changedDocs">Documents the patch touched.</param>
    /// <param name="original">The solution the fork was taken from, used to tell the patch's own suggestions from the ones already in the file. Null disables that, and every suggestion in a changed document is reported.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What was found, or a skip carrying the reason it was not.</returns>
    public static async Task<AnalyzerOutcome> RunAsync(
        Solution forked, IReadOnlyList<DocumentId> changedDocs, Solution? original = null,
        CancellationToken cancellationToken = default)
    {
        if (changedDocs.Count == 0)
            return AnalyzerOutcome.Skipped("no documents changed");

        var sw = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);

        var errors = new List<Diagnostic>();
        var warnings = new List<Diagnostic>();
        var suggestions = new List<Diagnostic>();
        var failed = new SortedSet<string>(StringComparer.Ordinal);
        var analyzerCount = 0;
        var documentCount = 0;
        var preexisting = 0;

        try
        {
            foreach (var group in changedDocs.GroupBy(d => d.ProjectId))
            {
                var project = forked.GetProject(group.Key);
                if (project is null)
                    continue;

                var analyzers = project.AnalyzerReferences
                    .SelectMany(r => r.GetAnalyzers(project.Language))
                    .ToImmutableArray();
                if (analyzers.IsEmpty)
                    continue;

                var compilation = await project.GetCompilationAsync(budget.Token);
                if (compilation is null)
                    continue;

                // onAnalyzerException keeps one broken analyzer from failing the pass: it is recorded as
                // unassessed and the remaining analyzers still report. Without it the exception surfaces as
                // an AD0001 diagnostic that reads exactly like a finding about the user's code.
                var options = new CompilationWithAnalyzersOptions(
                    project.AnalyzerOptions,
                    onAnalyzerException: (_, analyzer, _) => failed.Add(analyzer.GetType().Name),
                    concurrentAnalysis: true,
                    logAnalyzerExecutionTime: false);
                var withAnalyzers = compilation.WithAnalyzers(analyzers, options);
                analyzerCount = Math.Max(analyzerCount, analyzers.Length);

                foreach (var docId in group)
                {
                    var document = project.GetDocument(docId);
                    if (document is null)
                        continue;

                    var tree = await document.GetSyntaxTreeAsync(budget.Token);
                    var model = await document.GetSemanticModelAsync(budget.Token);
                    if (tree is null || model is null)
                        continue;

                    documentCount++;
                    var touched = await ChangedLineSpansAsync(document, original?.GetDocument(docId), budget.Token);
                    var found = await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(tree, budget.Token);
                    found = found.AddRange(
                        await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, filterSpan: null, budget.Token));

                    foreach (var diagnostic in found)
                    {
                        // Effective severity, already resolved against .editorconfig and warnaserror by the
                        // options above. Hidden is dropped: it is the "not shown in the error list" tier that
                        // exists to drive IDE refactoring affordances, not to be reported as a finding.
                        switch (diagnostic.Severity)
                        {
                            case DiagnosticSeverity.Error: errors.Add(diagnostic); break;
                            case DiagnosticSeverity.Warning: warnings.Add(diagnostic); break;
                            // Suggestions -- and only suggestions -- are held to the lines the patch actually
                            // rewrote. They scale with the size of the changed FILE rather than of the change, so
                            // a one-method rename reported five findings from lines it never touched and left the
                            // caller to decide, per finding, which were theirs. Errors and warnings stay
                            // unfiltered: those are consequences worth hearing about wherever they land.
                            case DiagnosticSeverity.Info when touched is null || touched.Any(s =>
                                s.Start <= diagnostic.Location.SourceSpan.End
                                && diagnostic.Location.SourceSpan.Start <= s.End):
                                suggestions.Add(diagnostic);
                                break;
                            case DiagnosticSeverity.Info: preexisting++; break;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AnalyzerOutcome.Skipped($"analyzers exceeded the {Budget.TotalSeconds:F0}s budget");
        }

        sw.Stop();

        if (analyzerCount == 0)
            return AnalyzerOutcome.Skipped("no analyzers referenced by the changed documents' projects");

        var scope = $"{documentCount} changed document(s); analyzer findings in files this patch did not touch are not assessed";
        return new AnalyzerOutcome(
            true, null, analyzerCount, documentCount, sw.ElapsedMilliseconds,
            errors, warnings, suggestions, scope, [.. failed], preexisting);
    }

    /// <summary>
    /// The character ranges of <paramref name="document"/>, widened to whole lines, whose text differs from
    /// <paramref name="before"/> — or null when there is no previous version to compare against.
    /// </summary>
    /// <remarks>
    /// Widened to line boundaries because an analyzer anchors a finding at a token rather than at the edit: a
    /// rewritten fragment owns everything the compiler now reads on the lines it sits on.
    ///
    /// <para>
    /// Uses <see cref="Document.GetTextChangesAsync"/> rather than <c>SourceText.GetChangeRanges</c>, which
    /// only reports fine-grained ranges when the two texts share a change-tracking lineage. A renamed document
    /// is built from a fresh syntax tree and has none, so that call returned a single range covering the whole
    /// file and the filter passed everything through — failing safe, but not actually filtering, which is
    /// exactly the shape of the finding this exists to fix. This one computes a real diff either way.
    /// </para>
    /// </remarks>
    /// <param name="document">The changed document, as the fork holds it.</param>
    /// <param name="before">The same document in the solution the fork was taken from, or null.</param>
    /// <param name="cancellationToken">Cancels the diff.</param>
    /// <returns>Half-open character ranges into the new text, or null when nothing can be told apart.</returns>
    internal static async Task<List<(int Start, int End)>?> ChangedLineSpansAsync(
        Document document, Document? before, CancellationToken cancellationToken)
    {
        if (before is null)
            return null;

        var text = await document.GetTextAsync(cancellationToken);
        var spans = new List<(int Start, int End)>();
        var delta = 0;
        // Ordered by position in the OLD text and non-overlapping, so one running delta maps each one onto
        // the new text without re-diffing.
        foreach (var change in await document.GetTextChangesAsync(before, cancellationToken))
        {
            var newLength = change.NewText?.Length ?? 0;
            var start = Math.Clamp(change.Span.Start + delta, 0, text.Length);
            var end = Math.Clamp(start + newLength, 0, text.Length);
            spans.Add((text.Lines.GetLineFromPosition(start).Start,
                text.Lines.GetLineFromPosition(end).EndIncludingLineBreak));
            delta += newLength - change.Span.Length;
        }
        return spans;
    }
}

