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
    /// Why forking failed, when it did. Distinguishes the failures a caller can act on — the workspace's
    /// copy of a file no longer matches disk, or a draft no longer matches the workspace it forked from —
    /// from an edit that was simply malformed.
    /// </summary>
    public enum Failure
    {
        /// <summary>Forking succeeded.</summary>
        None,

        /// <summary>The edit named a file outside the solution, or a line span outside that file.</summary>
        InvalidEdit,

        /// <summary>The workspace's copy of an edited file is behind disk.</summary>
        StaleWorkspace,

        /// <summary>A file moved in the workspace since the draft being amended forked from it.</summary>
        DraftStale,
    }

    /// <summary>The outcome of forking: either a solution to validate, or a failure to report.</summary>
    public sealed record Result(
        Solution Forked, IReadOnlyList<DocumentId> ChangedDocuments, string? Error,
        Failure FailureKind = Failure.None);

    /// <summary>Applies proposed edits to a fork of <paramref name="solution"/>, leaving disk untouched.</summary>
    /// <param name="solution">The workspace solution to fork from; never mutated.</param>
    /// <param name="locator">Resolves each edit's repo-relative path to an absolute one.</param>
    /// <param name="edits">The edits to apply. May be empty when <paramref name="draft"/> is supplied, which re-forks that draft unchanged.</param>
    /// <param name="draft">When supplied, every file the draft covers is seeded with its proposed text, so the edits' line spans address that text rather than the copy the workspace holds.</param>
    /// <param name="cancellationToken">Cancels the document text reads this makes.</param>
    /// <returns>The forked solution and the documents it changed, or a <see cref="Failure"/> and a message explaining it.</returns>
    /// <remarks>
    /// Rewrites each touched document's whole text, not just the edited span, so a fork whose copy has
    /// drifted from disk is refused outright (<see cref="Failure.StaleWorkspace"/>) rather than silently
    /// reverting the untouched remainder of the file.
    /// </remarks>
    public static async Task<Result> ApplyAsync(
        Solution solution, SolutionLocator locator, IReadOnlyList<PatchEdit> edits, PatchDraft? draft = null,
        CancellationToken cancellationToken = default)
    {
        var forked = solution;
        var changed = new List<DocumentId>();
        // Path-keyed, so the filesystem's own case rules decide what "the same file" means. Ordinal
        // here would split src/Foo.cs and src/foo.cs into two groups on Windows, and the second would
        // fork from text the first had already edited - reported as stale_workspace, which describes
        // neither the cause nor the fix.
        var editsByPath = edits
            .GroupBy(e => locator.AbsPath(e.File), PathComparison.Comparer)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PatchEdit>)[.. g], PathComparison.Comparer);

        // A draft's files are re-forked even when this call edits none of them: an amend touching one file
        // must still carry the rest of the draft's proposed text into the fork, or the parts of the patch
        // it is not correcting would silently vanish.
        var paths = editsByPath.Keys.Union(draft?.Proposed.Keys ?? [], PathComparison.Comparer).ToList();

        foreach (var path in paths)
        {
            var docIds = forked.GetDocumentIdsWithFilePath(path);
            if (docIds.IsEmpty)
                return new Result(solution, [], $"file is not part of the loaded solution: {locator.RelPath(path)}",
                    Failure.InvalidEdit);

            // [0]: assumes one document per file path (no linked/multi-targeted files sharing this path
            // across projects) -- true for this repo's own project layout today.
            var docId = docIds[0];
            var document = forked.GetDocument(docId)!;
            var workspaceText = await document.GetTextAsync(cancellationToken);

            // Refuse to fork from a copy that no longer matches disk. An apply writes the *whole*
            // document text back, not just the edited span, so a patch built on a lagging copy silently
            // reverts every change made to the rest of that file since the workspace last read it.
            // baseVersions does not cover this: it guards the symbols the classifier saw change, while
            // the untouched remainder of the file is what gets clobbered.
            //
            // Observed exactly that way in this repo: the workspace had missed a commit, a one-method
            // patch applied cleanly, and the commit's other edits to the same file were reverted with
            // no diagnostic. Line endings are normalised first so a CRLF checkout is not read as drift.
            if (await DiskDrift.DriftedAsync(path, workspaceText))
                return new Result(solution, [], $"the workspace's copy of {locator.RelPath(path)} is behind "
                    + "disk; reload_workspace, re-read the symbol, and rebuild the patch", Failure.StaleWorkspace);

            var seed = workspaceText;
            if (draft is not null && draft.Proposed.TryGetValue(path, out var proposed))
            {
                // A draft's line numbers only mean anything against the text it forked from. If the
                // workspace has moved since, an amend would splice corrected lines into stale content.
                if (!draft.Baseline.TryGetValue(path, out var baseline) || !baseline.ContentEquals(workspaceText))
                    return new Result(solution, [], "the draft no longer matches the workspace's copy of "
                        + $"{locator.RelPath(path)}; re-read the symbol and rebuild the patch from scratch",
                        Failure.DraftStale);

                seed = proposed;
            }

            SourceText? edited = seed;
            if (editsByPath.TryGetValue(path, out var fileEdits))
            {
                edited = ApplyToText(seed, [.. fileEdits.OrderByDescending(e => e.StartLine)], out var error);
                if (edited is null)
                    return new Result(solution, [], error, Failure.InvalidEdit);
            }

            forked = forked.WithDocumentText(docId, edited, PreservationMode.PreserveIdentity);
            changed.Add(docId);
        }

        return new Result(forked, changed, null);
    }

    /// <summary>Captures a fork as an amendable draft, pairing each changed file's proposed text with the text it was derived from.</summary>
    /// <param name="result">A successful fork whose text has not been written to disk.</param>
    /// <param name="original">The unforked solution, source of the baseline text used to detect later drift.</param>
    /// <param name="baseVersions">The held versions the patch was built against, carried forward so an amend need not resend them.</param>
    /// <param name="cancellationToken">Cancels the document text reads this makes.</param>
    /// <returns>An unstored draft; <see cref="PatchDraftStore.Put"/> assigns its id and deadline.</returns>
    public static async Task<PatchDraft> DraftOfAsync(
        Result result, Solution original, IReadOnlyDictionary<string, string> baseVersions,
        CancellationToken cancellationToken = default)
    {
        // Keyed by Roslyn's own document FilePath, which need not agree on case with the path the
        // caller sent; ApplyAsync looks these up by the caller's spelling, so the comparer has to match.
        var proposed = new Dictionary<string, SourceText>(PathComparison.Comparer);
        var baseline = new Dictionary<string, SourceText>(PathComparison.Comparer);

        foreach (var docId in result.ChangedDocuments)
        {
            var document = result.Forked.GetDocument(docId);
            var before = original.GetDocument(docId);
            if (document?.FilePath is null || before is null)
                continue;

            proposed[document.FilePath] = await document.GetTextAsync(cancellationToken);
            baseline[document.FilePath] = await before.GetTextAsync(cancellationToken);
        }

        return new PatchDraft(baseVersions, proposed, baseline);
    }

    private static SourceText? ApplyToText(SourceText text, IReadOnlyList<PatchEdit> descendingEdits, out string? error)
    {
        error = null;
        var current = text;
        var lineEnding = DominantLineEnding(text);
        var nextStart = int.MaxValue;
        foreach (var edit in descendingEdits)
        {
            if (edit.StartLine < 1 || edit.EndLine < edit.StartLine || edit.EndLine > current.Lines.Count)
            {
                error = $"edit span out of range for {edit.File}: lines {edit.StartLine}-{edit.EndLine}";
                return null;
            }

            // Sorted by StartLine descending, so a well-formed patch has every edit ending strictly before
            // the one already applied begins. Two spans that OVERLAP cannot both be honoured -- the second
            // addresses line numbers the first has just moved -- and applying them anyway spliced a stale
            // copy of the overlap over the fresh one and reported success.
            if (edit.EndLine >= nextStart)
            {
                error = $"overlapping edits in {edit.File}: lines {edit.StartLine}-{edit.EndLine} overlap the edit "
                    + $"starting at line {nextStart}. Send one edit per span -- two changes inside one span belong "
                    + "in a single edit's newText.";
                return null;
            }
            nextStart = edit.StartLine;
            var start = current.Lines[edit.StartLine - 1].Start;
            var end = current.Lines[edit.EndLine - 1].End;
            current = current.WithChanges(
                new TextChange(TextSpan.FromBounds(start, end), Retarget(edit.NewText, lineEnding)));
        }
        return current;
    }

    /// <summary>How many leading lines to sample when deciding a file's line ending.</summary>
    private const int LineEndingSampleSize = 200;

    /// <summary>The line ending the file already uses, so an applied patch does not mix the two.</summary>
    /// <param name="text">The document text being edited.</param>
    /// <returns><c>"\r\n"</c> for a file that is predominantly CRLF, <c>"\n"</c> otherwise.</returns>
    /// <remarks>
    /// newText arrives over JSON with LF endings whatever the file on disk uses. Splicing it verbatim into
    /// a CRLF checkout - the Windows default in any repo without a .gitattributes forcing LF - leaves the
    /// file mixed, so every applied patch shows up in git as touching lines it never changed.
    /// </remarks>
    internal static string DominantLineEnding(SourceText text)
    {
        var crlf = 0;
        var lf = 0;
        var sampled = Math.Min(text.Lines.Count, LineEndingSampleSize);
        for (var i = 0; i < sampled; i++)
        {
            var line = text.Lines[i];
            switch (line.EndIncludingLineBreak - line.End)
            {
                case 2:
                    crlf++;
                    break;
                case 1:
                    lf++;
                    break;
            }
        }

        return crlf > lf ? "\r\n" : "\n";
    }

    /// <summary>Rewrites a patch hunk's line endings to match the file it is being spliced into.</summary>
    /// <param name="newText">The replacement text as the caller sent it.</param>
    /// <param name="lineEnding">The file's own line ending, from <see cref="DominantLineEnding"/>.</param>
    /// <returns>The same text with every line break rewritten to <paramref name="lineEnding"/>.</returns>
    internal static string Retarget(string newText, string lineEnding)
    {
        var normalized = newText.Replace("\r\n", "\n");
        return lineEnding == "\n" ? normalized : normalized.Replace("\n", lineEnding);
    }
}
