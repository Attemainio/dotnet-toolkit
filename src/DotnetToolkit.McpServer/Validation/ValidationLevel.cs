namespace DotnetToolkit.McpServer.Validation;

/// <summary>The validation ladder (spec §13.1). Ordinals are the escalation order.</summary>
public enum ValidationLevel
{
    Parse = 1,
    SemanticBind = 2,
    ProjectCompile = 3,
    DependentCompile = 4,
    TargetedTests = 5,
    SolutionValidate = 6,
}

/// <summary>
/// The mechanical change kinds derived from a symbol's declaration delta (spec §13.2). These key the
/// escalation table; they are detected from syntax, never inferred by a classifier model.
/// </summary>
public enum ChangeKind
{
    /// <summary>Trivia only — no version-token layer moved.</summary>
    Trivia,

    /// <summary>Body/initializer changed, declaration unchanged.</summary>
    Body,

    Signature,
    Accessibility,
    Inheritance,
    Interface,
    Attribute,
    GenericConstraint,
    Nullability,

    /// <summary>A new declaration with no prior counterpart in the base — a pure addition.</summary>
    Added,

    /// <summary>An old declaration with no counterpart in the new tree — a pure removal.</summary>
    Removed,
}

public static class ValidationLevelExtensions
{
    /// <summary>The lowercase snake-ish name used in responses and telemetry (spec §13.1).</summary>
    public static string Wire(this ValidationLevel level) => level switch
    {
        ValidationLevel.Parse => "parse",
        ValidationLevel.SemanticBind => "semantic_bind",
        ValidationLevel.ProjectCompile => "project_compile",
        ValidationLevel.DependentCompile => "dependent_compile",
        ValidationLevel.TargetedTests => "targeted_tests",
        ValidationLevel.SolutionValidate => "solution_validate",
        _ => level.ToString().ToLowerInvariant(),
    };

    public static string Wire(this ChangeKind kind) => kind.ToString().ToLowerInvariant();
}
