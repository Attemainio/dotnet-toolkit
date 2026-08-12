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

        // Which rungs above ladder.Completed didn't run is already derivable from `levels` below (the fixed
        // parse→semantic_bind→project_compile→dependent_compile→targeted_tests→solution_validate order is
        // documented on validate_patch itself) - restating it here cost ~20 tokens on every response for
        // information the caller already held (self-eval finding, 2026-08-10). The analyzer-scope caveat
        // below stays: which DOCUMENTS were analyzed is not otherwise stated anywhere in the response.

        if (analyzers is null || !analyzers.Ran)
        {
            notAssessed.Add(
                $"analyzers: {analyzers?.SkipReason ?? "not attempted"} — CA/IDE rules, and any .editorconfig " +
                "severity configured for them, are unassessed");
        }
        else
        {
            notAssessed.Add($"analyzers covered {analyzers.Scope}");
            // Withheld, not hidden. Suggestions are filtered to the lines the patch rewrote, so saying nothing
            // here would leave a caller believing the file is clean where it merely was not this patch's
            // business -- the same silence-read-as-zero mistake limitedBy exists to prevent elsewhere.
            if (analyzers.PreexistingSuggestions > 0)
            {
                notAssessed.Add($"{analyzers.PreexistingSuggestions} analyzer suggestion(s) sit on lines this patch "
                    + "did not change and are not reported; they are pre-existing, not consequences of it");
            }
            foreach (var failed in analyzers.FailedAnalyzers)
                notAssessed.Add($"analyzer {failed} threw and was dropped; its rules are unassessed");
        }

        // Once ran is false, every remaining field is a constant by construction — zero analyzers over
        // zero documents in zero milliseconds, not clean, nothing found — so the block collapses to the
        // two fields that carry the answer and notAssessed states the consequence in words. What stays
        // under a run that DID happen is the scope the verdict covers, which is this block's whole point.
        object? analyzerBlock = null;
        if (analyzers is { Ran: true })
        {
            analyzerBlock = new
            {
                ran = true,
                analyzerCount = analyzers.AnalyzerCount,
                documentCount = analyzers.DocumentCount,
                durationMs = analyzers.DurationMs,
                clean = analyzers.IsClean,
                // Errors are not repeated here: they are the run's FailingDiagnostics and already arrive
                // distilled under `diagnostics`. Only the advisory tiers, which nothing else reports, do —
                // and each of the three is omitted at zero, since `clean` already states that case once.
                errorCount = analyzers.Errors.Count == 0 ? (int?)null : analyzers.Errors.Count,
                warnings = Advisories(analyzers.Warnings, locator),
                suggestions = Advisories(analyzers.Suggestions, locator),
            };
        }
        else if (analyzers is not null)
        {
            analyzerBlock = new { ran = false, skipReason = analyzers.SkipReason };
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
            analyzers = analyzerBlock,
            notAssessed,
        };
    }

    /// <summary>Renders one severity's advisory findings, capped.</summary>
    /// <param name="diagnostics">Every diagnostic reported at this severity.</param>
    /// <param name="locator">Resolves absolute diagnostic paths to repo-relative ones.</param>
    /// <returns>The capped items with their counts, or null when this severity found nothing.</returns>
    private static object? Advisories(IReadOnlyList<Diagnostic> diagnostics, SolutionLocator locator)
    {
        if (diagnostics.Count == 0)
            return null;

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
