using System.Diagnostics;
using Microsoft.CodeAnalysis;

namespace DotnetToolkit.McpServer.Validation;

/// <summary>
/// Runs the validation ladder against a forked solution (spec §13.1). MVP implements levels 1–4
/// (parse → semantic_bind → project_compile → dependent_compile); levels 5–6 arrive in later phases.
/// It runs each level in order up to the target, stopping at the first failing level; the honest
/// (completedLevel, succeeded) pair the tool reports comes straight from this result.
/// </summary>
public static class ValidationLadder
{
    /// <summary>
    /// Highest level this build can execute. Level 6 compiles every project; the full test set at that
    /// level is the caller's targeted-test runner widened by the escalation table, not a separate rung.
    /// </summary>
    public static readonly ValidationLevel MaxSupported = ValidationLevel.SolutionValidate;

    /// <summary>
    /// Runs the tests that semantically reference the changed symbols. Supplied by the caller because
    /// resolving "which tests" needs the edge cache, and executing them needs a process runner — neither
    /// belongs in the ladder itself. Returns the failure output, or null when the run passed.
    /// </summary>
    public delegate Task<string?> TargetedTestRunner(CancellationToken cancellationToken);

    /// <summary>One rung's outcome, including what it actually covered.</summary>
    /// <param name="Level">The rung that ran.</param>
    /// <param name="Succeeded">Whether the rung produced no error-severity diagnostics.</param>
    /// <param name="DurationMs">Wall-clock cost of the rung.</param>
    /// <param name="Scope">
    /// What the rung examined, in counts and names — the difference between "clean" and "never looked".
    /// A caller reporting a passing run has to be able to say <em>over what</em>, or the silence reads as
    /// an assurance the run never actually made.
    /// </param>
    public sealed record LevelResult(ValidationLevel Level, bool Succeeded, long DurationMs, string Scope = "");

    /// <summary>The whole run: how far it got, whether it passed, and everything checked or left unchecked.</summary>
    /// <param name="Completed">Highest rung that ran to completion.</param>
    /// <param name="Succeeded">Whether the run passed, analyzer errors included.</param>
    /// <param name="Levels">Per-rung outcomes, in the order they ran.</param>
    /// <param name="FailingDiagnostics">Error-severity diagnostics that stopped the run — compiler or analyzer.</param>
    /// <param name="TestFailureOutput">Targeted-test output when the test rung failed.</param>
    /// <param name="Analyzers">
    /// The analyzer pass's outcome. Null only for a result constructed without one; the ladder itself always
    /// supplies a value, since a pass that did not run still has to report why.
    /// </param>
    public sealed record LadderResult(
        ValidationLevel Completed, bool Succeeded, IReadOnlyList<LevelResult> Levels,
        IReadOnlyList<Diagnostic> FailingDiagnostics, string? TestFailureOutput = null,
        AnalyzerRunner.AnalyzerOutcome? Analyzers = null);

    /// <summary>
    /// Runs the ladder up to <paramref name="target"/> (capped to <see cref="ValidationLevel.DependentCompile"/>
    /// when no <paramref name="testRunner"/> is supplied), stopping at the first level that fails, then runs
    /// the projects' referenced analyzers over the changed documents.
    /// </summary>
    /// <param name="forked">The forked in-memory solution to validate.</param>
    /// <param name="changedDocs">Documents touched by the patch.</param>
    /// <param name="target">The highest level the caller wants run.</param>
    /// <param name="original">The solution the fork was taken from, forwarded to the analyzer pass so it can tell the patch's own suggestions from the ones already in the file.</param>
    /// <param name="testRunner">Runs tests referencing the changed symbols; required to reach <see cref="ValidationLevel.TargetedTests"/> or higher.</param>
    /// <param name="runAnalyzers">Whether to run the analyzer pass once the compile rungs are clean.</param>
    /// <param name="cancellationToken">Cancels the run; observed by every level, including compiles and test runs.</param>
    /// <returns>The highest level completed, whether it succeeded, and everything checked or left unchecked.</returns>
    public static async Task<LadderResult> RunAsync(
        Solution forked, IReadOnlyList<DocumentId> changedDocs, ValidationLevel target,
        Solution? original = null, TargetedTestRunner? testRunner = null, bool runAnalyzers = true,
        CancellationToken cancellationToken = default)
    {
        // Level 5 needs a runner; without one the ladder cannot honestly claim to have run tests, so it
        // stops at level 4 and the caller reports the shortfall through isSufficient.
        var ceiling = testRunner is null ? ValidationLevel.DependentCompile : MaxSupported;
        var capped = (ValidationLevel)Math.Min((int)target, (int)ceiling);
        var results = new List<LevelResult>();
        var completed = ValidationLevel.Parse;

        for (var level = ValidationLevel.Parse; level <= capped; level++)
        {
            var sw = Stopwatch.StartNew();

            if (level == ValidationLevel.TargetedTests)
            {
                var failure = await testRunner!(cancellationToken);
                sw.Stop();
                results.Add(new LevelResult(level, failure is null, sw.ElapsedMilliseconds,
                    "tests semantically referencing the changed symbols"));
                completed = level;
                if (failure is not null)
                    return new LadderResult(completed, false, results, [], failure, NotReached);
                continue;
            }

            var (errors, scope) = await RunLevelAsync(level, forked, changedDocs, cancellationToken);
            sw.Stop();

            results.Add(new LevelResult(level, errors.Count == 0, sw.ElapsedMilliseconds, scope));
            completed = level;

            if (errors.Count > 0)
                return new LadderResult(completed, false, results, errors, null, NotReached);
        }

        // Analyzers run only once the compile rungs are clean, and are graded exactly as dotnet build grades
        // them: effective severity Error fails the run (an .editorconfig `severity = error`, or warnaserror
        // promoting a warning), while warnings and suggestions are reported without blocking. Analyzing code
        // that does not bind would bury the real compile error under cascades of downstream findings.
        var analyzers = runAnalyzers
            ? await AnalyzerRunner.RunAsync(forked, changedDocs, original, cancellationToken)
            : AnalyzerRunner.AnalyzerOutcome.Skipped("the caller disabled the analyzer pass");

        return analyzers.Errors.Count > 0
            ? new LadderResult(completed, false, results, analyzers.Errors, null, analyzers)
            : new LadderResult(completed, true, results, [], null, analyzers);
    }

    /// <summary>The analyzer outcome reported when a rung failed before the pass could be reached.</summary>
    private static AnalyzerRunner.AnalyzerOutcome NotReached =>
        AnalyzerRunner.AnalyzerOutcome.Skipped("a validation level failed before the analyzer pass");

    private static async Task<(IReadOnlyList<Diagnostic> Errors, string Scope)> RunLevelAsync(
        ValidationLevel level, Solution forked, IReadOnlyList<DocumentId> changedDocs, CancellationToken cancellationToken)
    {
        switch (level)
        {
            case ValidationLevel.Parse:
                return (await ParseAsync(forked, changedDocs, cancellationToken), Docs(changedDocs.Count));
            case ValidationLevel.SemanticBind:
                return (await SemanticBindAsync(forked, changedDocs, cancellationToken), Docs(changedDocs.Count));
            case ValidationLevel.ProjectCompile:
            {
                var projects = ContainingProjects(forked, changedDocs).ToList();
                return (await CompileAsync(forked, projects, cancellationToken), Named(forked, projects));
            }
            case ValidationLevel.DependentCompile:
            {
                var projects = DependentProjects(forked, changedDocs).ToList();
                return (await CompileAsync(forked, projects, cancellationToken), Named(forked, projects));
            }
            // Level 6: every project in the solution, not just those reachable from the change.
            case ValidationLevel.SolutionValidate:
                return (await CompileAsync(forked, forked.ProjectIds, cancellationToken),
                    Named(forked, forked.ProjectIds));
            default:
                return ([], "nothing");
        }

        static string Docs(int count) => $"{count} changed document(s)";

        static string Named(Solution solution, IEnumerable<ProjectId> projectIds) =>
            string.Join(", ", projectIds.Distinct().Select(id => solution.GetProject(id)?.Name ?? "<unknown>").Order());
    }

    private static async Task<IReadOnlyList<Diagnostic>> ParseAsync(Solution forked, IReadOnlyList<DocumentId> changedDocs, CancellationToken cancellationToken)
    {
        var errors = new List<Diagnostic>();
        foreach (var docId in changedDocs)
        {
            var tree = await forked.GetDocument(docId)!.GetSyntaxTreeAsync(cancellationToken);
            if (tree is not null)
                errors.AddRange(tree.GetDiagnostics(cancellationToken).Where(IsError));
        }
        return errors;
    }

    private static async Task<IReadOnlyList<Diagnostic>> SemanticBindAsync(Solution forked, IReadOnlyList<DocumentId> changedDocs, CancellationToken cancellationToken)
    {
        var errors = new List<Diagnostic>();
        foreach (var docId in changedDocs)
        {
            var model = await forked.GetDocument(docId)!.GetSemanticModelAsync(cancellationToken);
            if (model is not null)
                errors.AddRange(model.GetDiagnostics(cancellationToken: cancellationToken).Where(IsError));
        }
        return errors;
    }

    private static async Task<IReadOnlyList<Diagnostic>> CompileAsync(Solution forked, IEnumerable<ProjectId> projectIds, CancellationToken cancellationToken)
    {
        var errors = new List<Diagnostic>();
        foreach (var projectId in projectIds.Distinct())
        {
            var compilation = await forked.GetProject(projectId)!.GetCompilationAsync(cancellationToken);
            if (compilation is not null)
                errors.AddRange(compilation.GetDiagnostics(cancellationToken).Where(IsError));
        }
        return errors;
    }

    private static IEnumerable<ProjectId> ContainingProjects(Solution forked, IReadOnlyList<DocumentId> changedDocs) =>
        changedDocs.Select(d => d.ProjectId).Distinct();

    private static IEnumerable<ProjectId> DependentProjects(Solution forked, IReadOnlyList<DocumentId> changedDocs)
    {
        var graph = forked.GetProjectDependencyGraph();
        var projects = new HashSet<ProjectId>();
        foreach (var projectId in changedDocs.Select(d => d.ProjectId).Distinct())
        {
            projects.Add(projectId);
            foreach (var dependent in graph.GetProjectsThatTransitivelyDependOnThisProject(projectId))
                projects.Add(dependent);
        }
        return projects;
    }

    private static bool IsError(Diagnostic diagnostic) => diagnostic.Severity == DiagnosticSeverity.Error;
}
