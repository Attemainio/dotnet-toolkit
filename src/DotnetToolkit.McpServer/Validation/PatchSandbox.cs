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
    /// <summary>
    /// The outcome of forking. <paramref name="Stale"/> distinguishes the one failure the caller can
    /// actually act on — the workspace's copy of a file no longer matches disk — from an edit that was
    /// simply malformed.
    /// </summary>
    public sealed record Result(
        Solution Forked, IReadOnlyList<DocumentId> ChangedDocuments, string? Error, bool Stale = false);

    /// <summary>Applies edits to a forked solution's in-memory documents, without touching disk.</summary>
    /// <param name="solution">The solution to fork from.</param>
    /// <param name="locator">Resolves each edit's file to an absolute, root-contained path.</param>
    /// <param name="edits">The edits to apply, grouped and applied per file.</param>
    /// <returns>The forked solution with edits applied, the changed document ids, or an error when a file is unknown, drifted from disk, or an edit is malformed.</returns>
    /// <remarks>
    /// Rewrites each touched document's whole text, not just the edited span, so a fork whose copy has
    /// drifted from disk is refused outright (<see cref="Result.Stale"/>) rather than silently reverting
    /// the untouched remainder of the file.
    /// </remarks>
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

            // Refuse to fork from a copy that no longer matches disk. An apply writes the *whole*
            // document text back, not just the edited span, so a patch built on a lagging copy silently
            // reverts every change made to the rest of that file since the workspace last read it.
            // baseVersions does not cover this: it guards the symbols the classifier saw change, while
            // the untouched remainder of the file is what gets clobbered.
            //
            // Observed exactly that way in this repo: the workspace had missed a commit, a one-method
            // patch applied cleanly, and the commit's other edits to the same file were reverted with
            // no diagnostic. Line endings are normalised first so a CRLF checkout is not read as drift.
            if (await DiskDrift.DriftedAsync(group.Key, text))
                return new Result(solution, [], $"the workspace's copy of {group.First().File} is behind "
                    + "disk; reload_workspace, re-read the symbol, and rebuild the patch", Stale: true);

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
