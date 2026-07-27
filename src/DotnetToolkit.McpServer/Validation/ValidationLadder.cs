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

    public sealed record LevelResult(ValidationLevel Level, bool Succeeded, long DurationMs);

    public sealed record LadderResult(
        ValidationLevel Completed, bool Succeeded, IReadOnlyList<LevelResult> Levels,
        IReadOnlyList<Diagnostic> FailingDiagnostics, string? TestFailureOutput = null);

    /// <summary>
    /// Runs the ladder up to <paramref name="target"/> (capped to <see cref="ValidationLevel.DependentCompile"/>
    /// when no <paramref name="testRunner"/> is supplied), stopping at the first level that fails.
    /// </summary>
    /// <param name="forked">The forked in-memory solution to validate.</param>
    /// <param name="changedDocs">Documents touched by the patch.</param>
    /// <param name="target">The highest level the caller wants run.</param>
    /// <param name="testRunner">Runs tests referencing the changed symbols; required to reach <see cref="ValidationLevel.TargetedTests"/> or higher.</param>
    /// <param name="cancellationToken">Cancels the run; observed by every level, including compiles and test runs.</param>
    /// <returns>The highest level completed, whether it succeeded, and any failing diagnostics or test output.</returns>
    public static async Task<LadderResult> RunAsync(
        Solution forked, IReadOnlyList<DocumentId> changedDocs, ValidationLevel target,
        TargetedTestRunner? testRunner = null, CancellationToken cancellationToken = default)
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
                results.Add(new LevelResult(level, failure is null, sw.ElapsedMilliseconds));
                completed = level;
                if (failure is not null)
                    return new LadderResult(completed, false, results, [], failure);
                continue;
            }

            var errors = await RunLevelAsync(level, forked, changedDocs, cancellationToken);
            sw.Stop();

            results.Add(new LevelResult(level, errors.Count == 0, sw.ElapsedMilliseconds));
            completed = level;

            if (errors.Count > 0)
                return new LadderResult(completed, false, results, errors);
        }

        return new LadderResult(completed, true, results, []);
    }

    private static async Task<IReadOnlyList<Diagnostic>> RunLevelAsync(
        ValidationLevel level, Solution forked, IReadOnlyList<DocumentId> changedDocs, CancellationToken cancellationToken)
    {
        return level switch
        {
            ValidationLevel.Parse => await ParseAsync(forked, changedDocs, cancellationToken),
            ValidationLevel.SemanticBind => await SemanticBindAsync(forked, changedDocs, cancellationToken),
            ValidationLevel.ProjectCompile => await CompileAsync(forked, ContainingProjects(forked, changedDocs), cancellationToken),
            ValidationLevel.DependentCompile => await CompileAsync(forked, DependentProjects(forked, changedDocs), cancellationToken),
            // Level 6: every project in the solution, not just those reachable from the change.
            ValidationLevel.SolutionValidate => await CompileAsync(forked, forked.ProjectIds, cancellationToken),
            _ => [],
        };
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
