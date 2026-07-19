using DotnetToolkit.McpServer.Workspace;
using Microsoft.CodeAnalysis;

namespace DotnetToolkit.McpServer.Validation;

/// <summary>
/// Distils a failing level's raw diagnostics to one root cause per originating diagnostic id
/// (spec §13.5). Every root cause carries a non-empty <c>suggestedInspection</c> whose entries are
/// valid get_symbol targets (Conformance C5); <c>totalRaw</c>/<c>totalSuppressed</c> are always
/// reported so distillation quality is itself measurable.
/// </summary>
public static class DiagnosticDistiller
{
    public sealed record Inspection(string SymbolId, string DisplayString, string Why);

    public sealed record RootCause(
        string Diagnostic, string Summary, string? AffectedSymbolId, string FixHint,
        IReadOnlyList<Inspection> SuggestedInspection, int SuppressedDiagnostics);

    public sealed record Distillation(IReadOnlyList<RootCause> RootCauses, int TotalRaw, int TotalSuppressed);

    public static async Task<Distillation> DistillAsync(
        Solution forked, IReadOnlyList<Diagnostic> errors,
        IReadOnlyList<(string SymbolId, string DisplayString)> changedSymbols)
    {
        var causes = new List<RootCause>();
        var totalRaw = errors.Count;

        foreach (var group in errors.GroupBy(e => e.Id))
        {
            var inspections = new List<Inspection>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var diagnostic in group)
            {
                var enclosing = await EnclosingSymbolAsync(forked, diagnostic.Location);
                if (enclosing is not null && seen.Add(enclosing.Value.SymbolId))
                    inspections.Add(new Inspection(enclosing.Value.SymbolId, enclosing.Value.DisplayString, "diagnostic origin"));
            }

            // C5: never emit a root cause without an inspection target. Fall back to the changed symbols.
            if (inspections.Count == 0)
                inspections.AddRange(changedSymbols.Select(s => new Inspection(s.SymbolId, s.DisplayString, "changed by this patch")));
            if (inspections.Count == 0)
                continue;

            var count = group.Count();
            causes.Add(new RootCause(
                group.Key,
                $"{group.Key}: {count} occurrence(s) — {group.First().GetMessage()}",
                changedSymbols.Count > 0 ? changedSymbols[0].SymbolId : inspections[0].SymbolId,
                FixHintFor(group.Key),
                inspections,
                Math.Max(0, count - inspections.Count)));
        }

        var totalSuppressed = totalRaw - causes.Count;
        return new Distillation(causes, totalRaw, Math.Max(0, totalSuppressed));
    }

    private static async Task<(string SymbolId, string DisplayString)?> EnclosingSymbolAsync(Solution forked, Location location)
    {
        if (location.SourceTree is null)
            return null;
        var document = forked.GetDocument(location.SourceTree);
        if (document is null)
            return null;
        var model = await document.GetSemanticModelAsync();
        var symbol = model?.GetEnclosingSymbol(location.SourceSpan.Start);
        if (symbol is null || symbol.Kind == SymbolKind.Namespace)
            return null;
        return (SymbolKey.IdOf(symbol), symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
    }

    private static string FixHintFor(string diagnosticId) => diagnosticId switch
    {
        "CS7036" => "A required argument is missing at each call site; supply it (prefer flowing an existing value over a default).",
        "CS1501" => "No overload takes this argument count; update the call sites to the new signature.",
        "CS0246" => "A type name could not be resolved; add the using or fix the type reference.",
        "CS0103" => "A name is not in scope here; check the identifier or add the missing member.",
        "CS1061" => "The type has no such member; the member was renamed or removed.",
        "CS0535" => "An interface member is not implemented; add the missing implementation.",
        _ => "Inspect the listed symbols and reconcile them with the change.",
    };
}
