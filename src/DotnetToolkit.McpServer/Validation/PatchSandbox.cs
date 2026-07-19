using DotnetToolkit.McpServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotnetToolkit.McpServer.Validation;

/// <summary>One line-span text replacement in a file (spec §13.3 edits[]). Lines are 1-based, inclusive.</summary>
public sealed record PatchEdit(string File, int StartLine, int EndLine, string NewText);

/// <summary>
/// Applies proposed edits to a forked, in-memory <see cref="Solution"/> snapshot via
/// <see cref="Solution.WithDocumentText(DocumentId, SourceText, PreservationMode)"/> — disk is never
/// touched here (spec §13). Validation and change detection then run against the fork; only an
/// explicit apply step writes to disk.
/// </summary>
public static class PatchSandbox
{
    public sealed record Result(Solution Forked, IReadOnlyList<DocumentId> ChangedDocuments, string? Error);

    public static async Task<Result> ApplyAsync(Solution solution, SolutionLocator locator, IReadOnlyList<PatchEdit> edits)
    {
        var forked = solution;
        var changed = new List<DocumentId>();

        foreach (var group in edits.GroupBy(e => locator.AbsPath(e.File)))
        {
            var docIds = forked.GetDocumentIdsWithFilePath(group.Key);
            if (docIds.IsEmpty)
                return new Result(solution, [], $"file is not part of the loaded solution: {group.First().File}");

            var docId = docIds[0];
            var document = forked.GetDocument(docId)!;
            var text = await document.GetTextAsync();

            var newText = ApplyToText(text, group.OrderByDescending(e => e.StartLine).ToList(), out var error);
            if (error is not null)
                return new Result(solution, [], error);

            forked = forked.WithDocumentText(docId, newText!, PreservationMode.PreserveIdentity);
            changed.Add(docId);
        }

        return new Result(forked, changed, null);
    }

    private static SourceText? ApplyToText(SourceText text, IReadOnlyList<PatchEdit> descendingEdits, out string? error)
    {
        error = null;
        var current = text;
        foreach (var edit in descendingEdits)
        {
            if (edit.StartLine < 1 || edit.EndLine < edit.StartLine || edit.EndLine > current.Lines.Count)
            {
                error = $"edit span out of range for {edit.File}: lines {edit.StartLine}-{edit.EndLine}";
                return null;
            }
            var start = current.Lines[edit.StartLine - 1].Start;
            var end = current.Lines[edit.EndLine - 1].End;
            current = current.WithChanges(new TextChange(TextSpan.FromBounds(start, end), edit.NewText));
        }
        return current;
    }
}
