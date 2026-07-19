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
    /// <summary>Highest level this phase can actually execute.</summary>
    public static readonly ValidationLevel MaxSupported = ValidationLevel.DependentCompile;

    public sealed record LevelResult(ValidationLevel Level, bool Succeeded, long DurationMs);

    public sealed record LadderResult(
        ValidationLevel Completed, bool Succeeded, IReadOnlyList<LevelResult> Levels,
        IReadOnlyList<Diagnostic> FailingDiagnostics);

    public static async Task<LadderResult> RunAsync(Solution forked, IReadOnlyList<DocumentId> changedDocs, ValidationLevel target)
    {
        var capped = (ValidationLevel)Math.Min((int)target, (int)MaxSupported);
        var results = new List<LevelResult>();
        var completed = ValidationLevel.Parse;

        for (var level = ValidationLevel.Parse; level <= capped; level++)
        {
            var sw = Stopwatch.StartNew();
            var errors = await RunLevelAsync(level, forked, changedDocs);
            sw.Stop();

            results.Add(new LevelResult(level, errors.Count == 0, sw.ElapsedMilliseconds));
            completed = level;

            if (errors.Count > 0)
                return new LadderResult(completed, false, results, errors);
        }

        return new LadderResult(completed, true, results, []);
    }

    private static async Task<IReadOnlyList<Diagnostic>> RunLevelAsync(
        ValidationLevel level, Solution forked, IReadOnlyList<DocumentId> changedDocs)
    {
        return level switch
        {
            ValidationLevel.Parse => await ParseAsync(forked, changedDocs),
            ValidationLevel.SemanticBind => await SemanticBindAsync(forked, changedDocs),
            ValidationLevel.ProjectCompile => await CompileAsync(forked, ContainingProjects(forked, changedDocs)),
            ValidationLevel.DependentCompile => await CompileAsync(forked, DependentProjects(forked, changedDocs)),
            _ => [],
        };
    }

    private static async Task<IReadOnlyList<Diagnostic>> ParseAsync(Solution forked, IReadOnlyList<DocumentId> changedDocs)
    {
        var errors = new List<Diagnostic>();
        foreach (var docId in changedDocs)
        {
            var tree = await forked.GetDocument(docId)!.GetSyntaxTreeAsync();
            if (tree is not null)
                errors.AddRange(tree.GetDiagnostics().Where(IsError));
        }
        return errors;
    }

    private static async Task<IReadOnlyList<Diagnostic>> SemanticBindAsync(Solution forked, IReadOnlyList<DocumentId> changedDocs)
    {
        var errors = new List<Diagnostic>();
        foreach (var docId in changedDocs)
        {
            var model = await forked.GetDocument(docId)!.GetSemanticModelAsync();
            if (model is not null)
                errors.AddRange(model.GetDiagnostics().Where(IsError));
        }
        return errors;
    }

    private static async Task<IReadOnlyList<Diagnostic>> CompileAsync(Solution forked, IEnumerable<ProjectId> projectIds)
    {
        var errors = new List<Diagnostic>();
        foreach (var projectId in projectIds.Distinct())
        {
            var compilation = await forked.GetProject(projectId)!.GetCompilationAsync();
            if (compilation is not null)
                errors.AddRange(compilation.GetDiagnostics().Where(IsError));
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
