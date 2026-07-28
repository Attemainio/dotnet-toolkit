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
    /// <summary>How many distinct source positions are reported per root cause.</summary>
    private const int MaxSitesPerCause = 3;

    /// <summary>How much of an offending line is echoed back before it is truncated.</summary>
    private const int MaxExcerptLength = 120;

    public sealed record Inspection(string SymbolId, string DisplayString, string Why);

    /// <summary>Where a diagnostic landed, in the coordinate space of the text the patch proposed.</summary>
    /// <param name="File">Repo-relative path of the file the diagnostic points into.</param>
    /// <param name="Line">1-based line number, in the same coordinate space a patch's edits use.</param>
    /// <param name="Column">1-based column number.</param>
    /// <param name="Excerpt">That line's text, trimmed and truncated, so a caller can aim a correction without refetching the symbol.</param>
    public sealed record DiagnosticSite(string File, int Line, int Column, string Excerpt);

    public sealed record RootCause(
        string Diagnostic, string Summary, string? AffectedSymbolId, string FixHint,
        IReadOnlyList<Inspection> SuggestedInspection, int SuppressedDiagnostics,
        IReadOnlyList<DiagnosticSite> Sites);

    public sealed record Distillation(IReadOnlyList<RootCause> RootCauses, int TotalRaw, int TotalSuppressed);

    /// <summary>Groups a failing level's raw diagnostics by diagnostic id into one root cause each.</summary>
    /// <param name="forked">The forked solution the diagnostics were produced against.</param>
    /// <param name="locator">Resolves each diagnostic's absolute file path to a repo-relative one.</param>
    /// <param name="errors">The raw compiler diagnostics to distil.</param>
    /// <param name="changedSymbols">Symbols the patch changed; used as the inspection fallback when a diagnostic's location can't be resolved to an enclosing symbol.</param>
    /// <param name="cancellationToken">Cancels the semantic model lookups this makes.</param>
    /// <returns>One <see cref="RootCause"/> per diagnostic id, plus the raw/suppressed diagnostic counts.</returns>
    public static async Task<Distillation> DistillAsync(
        Solution forked, SolutionLocator locator, IReadOnlyList<Diagnostic> errors,
        IReadOnlyList<(string SymbolId, string DisplayString)> changedSymbols,
        CancellationToken cancellationToken = default)
    {
        var causes = new List<RootCause>();
        var totalRaw = errors.Count;

        foreach (var group in errors.GroupBy(e => e.Id))
        {
            var inspections = new List<Inspection>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var diagnostic in group)
            {
                var enclosing = await EnclosingSymbolAsync(forked, diagnostic.Location, cancellationToken);
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
                inspections[0].SymbolId,
                FixHintFor(group.Key),
                inspections,
                Math.Max(0, count - inspections.Count),
                SitesOf(locator, group)));
        }

        var totalSuppressed = totalRaw - causes.Count;
        return new Distillation(causes, totalRaw, Math.Max(0, totalSuppressed));
    }

    /// <summary>Picks up to <see cref="MaxSitesPerCause"/> distinct positions for one root cause.</summary>
    /// <param name="locator">Resolves absolute tree paths to repo-relative ones.</param>
    /// <param name="group">The diagnostics sharing one diagnostic id.</param>
    /// <returns>Deduplicated by file and line, in the order the diagnostics were reported.</returns>
    private static IReadOnlyList<DiagnosticSite> SitesOf(SolutionLocator locator, IEnumerable<Diagnostic> group)
    {
        var sites = new List<DiagnosticSite>();
        var seen = new HashSet<(string File, int Line)>();

        foreach (var diagnostic in group)
        {
            var tree = diagnostic.Location.SourceTree;
            if (tree is null || string.IsNullOrEmpty(tree.FilePath))
                continue;

            // Roslyn reports 0-based line/character positions; every line number on this tool's surface
            // is 1-based, and an amend's edits are addressed in those same coordinates.
            var position = diagnostic.Location.GetLineSpan().StartLinePosition;
            var file = locator.RelPath(tree.FilePath);
            if (!seen.Add((file, position.Line + 1)))
                continue;

            sites.Add(new DiagnosticSite(file, position.Line + 1, position.Character + 1, ExcerptAt(tree, position.Line)));
            if (sites.Count == MaxSitesPerCause)
                break;
        }

        return sites;
    }

    /// <summary>Reads one line of a tree's text, trimmed and length-capped for the wire.</summary>
    /// <param name="tree">The syntax tree the diagnostic points into.</param>
    /// <param name="lineIndex">0-based line index, as Roslyn reports it.</param>
    /// <returns>The trimmed line, truncated to <see cref="MaxExcerptLength"/>, or empty when the index is out of range.</returns>
    private static string ExcerptAt(SyntaxTree tree, int lineIndex)
    {
        var lines = tree.GetText().Lines;
        if (lineIndex < 0 || lineIndex >= lines.Count)
            return "";

        var text = lines[lineIndex].ToString().Trim();
        return text.Length <= MaxExcerptLength ? text : string.Concat(text.AsSpan(0, MaxExcerptLength), "…");
    }

    private static async Task<(string SymbolId, string DisplayString)?> EnclosingSymbolAsync(Solution forked, Location location, CancellationToken cancellationToken)
    {
        if (location.SourceTree is null)
            return null;
        var document = forked.GetDocument(location.SourceTree);
        if (document is null)
            return null;
        var model = await document.GetSemanticModelAsync(cancellationToken);
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
