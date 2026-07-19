namespace DotnetToolkit.McpServer.Validation;

/// <summary>
/// The required-level rules of spec §13.2 as a pure, table-driven function of the detected change
/// kinds — not a classifier. The ladder's routing is therefore a deterministic lookup, and the server
/// MAY run beyond the required level but MUST NOT report below it (Conformance C2).
/// </summary>
public static class EscalationTable
{
    /// <summary>Minimum level for a single change kind, per the §13.2 table.</summary>
    public static ValidationLevel LevelFor(ChangeKind kind) => kind switch
    {
        ChangeKind.Trivia => ValidationLevel.Parse,
        ChangeKind.Body => ValidationLevel.ProjectCompile,
        // Signature, accessibility, inheritance, interface, attribute, generic-constraint and public
        // nullability changes are all breaking to dependents → dependent_compile.
        _ => ValidationLevel.DependentCompile,
    };

    /// <summary>
    /// Required level for one changed symbol: the max over its change kinds, escalated to
    /// <see cref="ValidationLevel.TargetedTests"/> when the symbol is referenced by test projects and
    /// there is an actual (non-trivia) change.
    /// </summary>
    public static ValidationLevel RequiredFor(IReadOnlyCollection<ChangeKind> kinds, bool referencedByTests)
    {
        if (kinds.Count == 0)
            return ValidationLevel.Parse;

        var level = kinds.Max(LevelFor);
        if (referencedByTests && level >= ValidationLevel.ProjectCompile)
            level = (ValidationLevel)Math.Max((int)level, (int)ValidationLevel.TargetedTests);
        return level;
    }

    /// <summary>Required level for a whole patch: the max over all changed symbols (spec §13.2).</summary>
    public static ValidationLevel RequiredForPatch(IEnumerable<(IReadOnlyCollection<ChangeKind> Kinds, bool ReferencedByTests)> symbols)
    {
        var level = ValidationLevel.Parse;
        foreach (var (kinds, referencedByTests) in symbols)
            level = (ValidationLevel)Math.Max((int)level, (int)RequiredFor(kinds, referencedByTests));
        return level;
    }
}
