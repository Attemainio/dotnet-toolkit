using DotnetToolkit.McpServer.Workspace;
using Microsoft.CodeAnalysis;

namespace DotnetToolkit.McpServer.Validation;

/// <summary>
/// Renders what a validation run actually checked — including, explicitly, what it did not.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed, a passing <c>validate_patch</c> reported <c>succeeded: true</c> and a null
/// <c>diagnostics</c> block, and nothing else. That is silence, and silence is ambiguous in the one
/// direction that matters: it reads identically whether a check ran and found nothing or never ran at all.
/// A caller cannot act on "clean" without knowing the scope it was clean over.
/// </para>
/// <para>
/// So every run emits this block, pass or fail. <c>levels</c> says which rungs ran and over how much,
/// <c>analyzers</c> says whether the analyzer pass happened and what it found at each severity, and
/// <c>notAssessed</c> enumerates the gaps in plain language — rungs above the one reached, a skipped or
/// timed-out analyzer pass, analyzers that threw, and the standing limitation that analyzer findings in
/// files the patch did not touch are never looked at. This is the same not-assessed-over-clean rule the
/// review agent already follows, applied to the write path.
/// </para>
/// </remarks>
public static class CheckReport
{
    /// <summary>Advisory findings reported per severity before truncation.</summary>
    /// <remarks>
    /// Warnings are informational here — they never block — so the full list is rarely worth its tokens.
    /// The untruncated count travels alongside, so a caller can always tell how much was elided.
    /// </remarks>
    private const int MaxAdvisoriesPerSeverity = 15;

    /// <summary>Builds the checks block for one validation run.</summary>
    /// <param name="ladder">The completed run.</param>
    /// <param name="locator">Resolves absolute diagnostic paths to repo-relative ones.</param>
    /// <returns>An anonymous object shaped for the tool response.</returns>
    public static object Build(ValidationLadder.LadderResult ladder, SolutionLocator locator)
    {
        var analyzers = ladder.Analyzers;
        var notAssessed = new List<string>();

        if ((int)ladder.Completed < (int)ValidationLadder.MaxSupported)
        {
            var skipped = Enumerable
                .Range((int)ladder.Completed + 1, (int)ValidationLadder.MaxSupported - (int)ladder.Completed)
                .Select(i => ((ValidationLevel)i).Wire());
            notAssessed.Add($"levels not run: {string.Join(", ", skipped)}");
        }

        if (analyzers is null || !analyzers.Ran)
        {
            notAssessed.Add(
                $"analyzers: {analyzers?.SkipReason ?? "not attempted"} — CA/IDE rules, and any .editorconfig " +
                "severity configured for them, are unassessed");
        }
        else
        {
            notAssessed.Add($"analyzers covered {analyzers.Scope}");
            foreach (var failed in analyzers.FailedAnalyzers)
                notAssessed.Add($"analyzer {failed} threw and was dropped; its rules are unassessed");
        }

        return new
        {
            levels = ladder.Levels.Select(l => new
            {
                level = l.Level.Wire(),
                succeeded = l.Succeeded,
                durationMs = l.DurationMs,
                scope = l.Scope,
            }),
            analyzers = analyzers is null ? null : new
            {
                ran = analyzers.Ran,
                skipReason = analyzers.SkipReason,
                analyzerCount = analyzers.AnalyzerCount,
                documentCount = analyzers.DocumentCount,
                durationMs = analyzers.DurationMs,
                clean = analyzers.IsClean,
                // Errors are not repeated here: they are the run's FailingDiagnostics and already arrive
                // distilled under `diagnostics`. Only the advisory tiers, which nothing else reports, do.
                errorCount = analyzers.Errors.Count,
                warnings = Advisories(analyzers.Warnings, locator),
                suggestions = Advisories(analyzers.Suggestions, locator),
            },
            notAssessed,
        };
    }

    private static object Advisories(IReadOnlyList<Diagnostic> diagnostics, SolutionLocator locator)
    {
        var items = diagnostics
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .Take(MaxAdvisoriesPerSeverity)
            .Select(d =>
            {
                var tree = d.Location.SourceTree;
                var hasFile = tree is not null && !string.IsNullOrEmpty(tree.FilePath);
                // Roslyn reports 0-based positions; every line number on this tool's surface is 1-based.
                var position = d.Location.GetLineSpan().StartLinePosition;
                return new
                {
                    id = d.Id,
                    message = d.GetMessage(),
                    file = hasFile ? locator.RelPath(tree!.FilePath) : null,
                    line = hasFile ? position.Line + 1 : 0,
                    column = hasFile ? position.Character + 1 : 0,
                };
            })
            .ToList();

        return new
        {
            count = diagnostics.Count,
            truncated = diagnostics.Count - items.Count,
            items,
        };
    }
}
