using System.ComponentModel;
using DotnetToolkit.McpServer.Identity;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Telemetry;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Rename;
using ModelContextProtocol.Server;

namespace DotnetToolkit.McpServer.Tools;

/// <summary>
/// Solution-wide rename, driven by Roslyn's own <see cref="Renamer"/> rather than by caller-authored text.
/// </summary>
/// <remarks>
/// The one edit this toolkit still makes. A string-anchored <c>Edit</c> cannot rename a symbol: it
/// cannot see interface, virtual or delegate dispatch, and it rewrites unrelated text that shares the
/// name. Roslyn can, so the rename is derived from the semantic model and written straight to disk.
/// Nothing here compiles the result — <c>dotnet build</c> is the gate for every other edit and it is
/// the gate for this one too; what this tool adds is the rewrite itself and the blast radius up front.
/// </remarks>
[McpServerToolType]
public static class RenameTools
{
    [McpServerTool(Name = "rename_symbol")]
    [Description("Rename a C# symbol and every reference to it across the whole solution — rename a class, interface, "
        + "method, property, field or parameter everywhere it is used, from the compiler's own model. USE THIS "
        + "INSTEAD OF a search-and-replace or a pile of Edit calls, which miss interface/virtual/delegate dispatch "
        + "and silently rewrite unrelated text that happens to share the name. A dry run (apply:false, the "
        + "default) reports every file that would change and how many occurrences, without writing; apply:true "
        + "writes the files and refreshes the index so the new name is searchable at once. The result is NOT "
        + "compiled: run dotnet build afterwards, as after any edit. nameAlreadyExists warns when the new name "
        + "is already declared in the same container (a same-signature clash will not compile; a different-"
        + "signature one silently becomes an overload set). Does not rename the containing FILE — when that "
        + "is wanted the response says so under fileRenameHint, and the move is a git mv.")]
    public static async Task<string> RenameSymbol(
        WorkspaceHost workspace,
        SolutionLocator locator,
        SymbolStore symbolStore,
        SymbolIndexBuilder indexBuilder,
        TelemetryRecorder telemetry,
        [Description("The symbol to rename: fully-qualified name, a unique suffix, or a sym_... id from a previous response. Append a parameter list to pick one overload.")] string symbol,
        [Description("The new identifier. Must be a valid C# identifier and not a keyword; the '@' prefix is accepted for a verbatim identifier.")] string newName,
        [Description("Write the rename to disk (default false). false is a full dry run: every file that would change and every occurrence, nothing written.")] bool apply = false,
        [Description("Also rename the other overloads of this method (default false). Ignored for a non-method symbol.")] bool renameOverloads = false,
        [Description("Also update occurrences of the old name inside comments and doc comments (default false). Textual and best-effort, unlike the reference rewrite itself.")] bool renameInComments = false,
        [Description("Also update occurrences of the old name inside string literals (default false). Textual and best-effort; leave off unless the name is genuinely reflected over.")] bool renameInStrings = false,
        [Description(ToolTelemetry.TaskIdParam)] string? taskId = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = Ids.AmbientSession;
        var attributedTask = Ids.TaskId(taskId);
        var toolCallId = Ids.ToolCall();

        string Reject(string errorKind, string json) =>
            ToolTelemetry.Record(telemetry, toolCallId, sessionId, attributedTask, "rename_symbol",
                symbol ?? "", json, errorKind: errorKind);
        string Fail(string errorKind, string message) => Reject(errorKind, Error(errorKind, message));

        if (string.IsNullOrWhiteSpace(symbol))
            return Fail("missing_symbol", "symbol is required.");

        if (string.IsNullOrWhiteSpace(newName))
            return Fail("missing_new_name", "newName is required.");

        newName = newName.Trim();
        var bare = newName.StartsWith('@') ? newName[1..] : newName;
        if (!SyntaxFacts.IsValidIdentifier(bare))
            return Fail("invalid_name", $"'{newName}' is not a valid C# identifier.");
        // A keyword is a valid *identifier* per IsValidIdentifier but will not parse where the old name
        // stood, so the rename would fail at the build with a wall of syntax errors instead of here.
        if (!newName.StartsWith('@') && SyntaxFacts.GetKeywordKind(bare) != SyntaxKind.None)
            return Fail("invalid_name",
                $"'{newName}' is a C# keyword; prefix it with '@' if a verbatim identifier is really intended.");

        async Task<string> RunAsync()
        {
            var solution = await workspace.GetSolutionAsync();
            if (solution is null)
                return Fail("workspace_loading", "The semantic workspace is not ready; retry shortly.");

            // A sym_... id is the handle every other tool hands out, so it has to work here too —
            // SymbolResolver alone matches on NAME and would read the id as one, missing every time.
            var spec = ContextTools.ResolveHandle(symbol, symbolStore);
            var resolution = await SymbolResolver.ResolveAsync(solution, spec, cancellationToken);
            if (resolution.Symbol is null)
            {
                return resolution.Candidates.Count == 0
                    ? Reject("symbol_not_found", Formats.Render(new
                    {
                        error = "symbol_not_found",
                        message = $"No source symbol matched '{symbol}'.",
                        didYouMean = ContextTools.NearMisses(symbolStore, spec),
                    }))
                    : Reject("ambiguous_symbol", ContextTools.AmbiguousSymbol(resolution.Candidates,
                        "Several symbols match; re-call with one of these exact names (append a "
                        + "parameter list to pick an overload)."));
            }

            var target = SymbolKey.Canonicalize(resolution.Symbol);
            if (target.DeclaringSyntaxReferences.IsEmpty)
                return Fail("external_symbol",
                    $"'{target.ToDisplayString()}' is not declared in this solution's source, so it cannot be "
                    + "renamed here.");

            var oldName = target.Name;
            if (string.Equals(oldName, bare, StringComparison.Ordinal))
                return Fail("unchanged_name", $"'{oldName}' is already the requested name.");

            var oldSymbolId = SymbolKey.IdOf(target);

            var options = new SymbolRenameOptions(
                RenameOverloads: renameOverloads,
                RenameInStrings: renameInStrings,
                RenameInComments: renameInComments,
                // File renaming is deliberately never delegated to Roslyn here: it would leave this server's
                // workspace holding a document whose FilePath no longer exists, and the change detector is an
                // mtime poll that cannot reconcile that. fileRenameHint reports the case instead.
                RenameFile: false);

            Solution forked;
            try
            {
                forked = await Renamer.RenameSymbolAsync(solution, target, options, bare, cancellationToken);
            }
            catch (ArgumentException ex)
            {
                return Fail("rename_rejected", $"Roslyn refused the rename: {ex.Message}");
            }

            // One DocumentId per file: a linked or multi-targeted file appears once per project, and the
            // write (whole documents to their FilePath) would otherwise double-count it.
            var changedDocs = forked.GetChanges(solution)
                .GetProjectChanges()
                .SelectMany(p => p.GetChangedDocuments())
                .DistinctBy(id => forked.GetDocument(id)?.FilePath ?? id.ToString(), StringComparer.Ordinal)
                .ToList();

            if (changedDocs.Count == 0)
                return Fail("no_changes",
                    $"Renaming '{oldName}' to '{bare}' produced no text changes — nothing references it under "
                    + "a name this rename would rewrite.");

            // An apply writes the WHOLE document back, so a rename computed over a workspace copy that lags
            // disk silently reverts everything else in that file. Refresh the lagging files once and retry;
            // if they are still behind, the workspace is mid-reload and the caller should wait.
            var changedPaths = changedDocs
                .Select(id => solution.GetDocument(id)?.FilePath)
                .OfType<string>()
                .ToList();
            if (apply && await workspace.IsBehindDiskAsync(changedPaths))
            {
                return Fail("stale_workspace",
                    "the workspace's copy of a file this rename touches is behind disk; call workspace_status, "
                    + "wait for it to report loaded, and retry the rename");
            }

            var applied = false;
            if (apply)
            {
                applied = await DocumentWriter.WriteAsync(forked, changedDocs, locator);
                if (applied)
                {
                    workspace.AdoptAppliedText(forked, changedDocs);
                    // Refreshed here rather than left to the hook: the new symbol id is reported below, and
                    // a caller that fetches it next should not have to wait for a sweep.
                    await indexBuilder.RefreshDocumentsAsync(changedPaths);
                }
            }

            var files = await FileSummariesAsync(solution, forked, changedDocs, oldName, locator, cancellationToken);
            var renamed = await RenamedSymbolAsync(forked, target, bare, cancellationToken);

            var response = new
            {
                rename = new
                {
                    oldName,
                    newName = bare,
                    oldSymbolId,
                    newSymbolId = renamed is null ? null : SymbolKey.IdOf(renamed),
                    kind = SymbolKey.KindOf(target),
                    filesChanged = files.Count,
                    occurrencesRewritten = files.Sum(f => f.Occurrences),
                },
                files = files.Select(f => new { file = f.File, occurrences = f.Occurrences }),
                applied,
                // A rename derived from a degraded workspace's own reference graph can miss call sites
                // entirely, which is a wrong answer, not a thin one.
                limitedBy = workspace.IsDegraded ? "degraded" : null,
                nameAlreadyExists = NameAlreadyExists(target, bare),
                fileRenameHint = FileRenameHint(target, oldName, bare, locator),
                notChecked = apply
                    ? "The rewritten solution was not compiled. Run dotnet build before relying on it."
                    : "Dry run: nothing was written. Pass apply:true to write these files.",
            };

            return ToolTelemetry.Record(telemetry, toolCallId, sessionId, attributedTask, "rename_symbol",
                symbol, Formats.Render(response), symbolId: oldSymbolId);
        }

        return apply
            ? await workspace.RunExclusiveApplyAsync(RunAsync)
            : await RunAsync();
    }

    /// <summary>The renamed symbol as it exists in the forked solution, or null if it cannot be found there.</summary>
    /// <remarks>
    /// Found by declaration rather than by name lookup: the target's first declaring reference maps to the
    /// same tree position in the fork, and the symbol declared there is the renamed one. A name lookup on
    /// the container would be ambiguous when the new name already exists (see <see cref="NameAlreadyExists"/>).
    /// </remarks>
    private static async Task<ISymbol?> RenamedSymbolAsync(Solution forked, ISymbol target, string newName, CancellationToken cancellationToken)
    {
        var reference = target.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null)
            return null;
        var document = forked.GetDocument(reference.SyntaxTree);
        if (document is null)
        {
            // A linked file has one tree per project; find the fork's document by path instead.
            var ids = forked.GetDocumentIdsWithFilePath(reference.SyntaxTree.FilePath);
            document = ids.IsEmpty ? null : forked.GetDocument(ids[0]);
            if (document is null)
                return null;
        }
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null)
            return null;

        // The declaration's start does not move: the rename only changes the identifier token inside it.
        var node = root.FindToken(reference.Span.Start).Parent;
        while (node is not null)
        {
            if (model.GetDeclaredSymbol(node, cancellationToken) is { } declared
                && string.Equals(declared.Name, newName, StringComparison.Ordinal)
                && declared.Kind == target.Kind)
                return declared;
            node = node.Parent;
        }
        return null;
    }

    /// <summary>
    /// Per-file counts of the old name actually rewritten, so a dry run reports the rename's blast radius
    /// without returning any source text.
    /// </summary>
    /// <remarks>
    /// Counted as whole-identifier occurrences that DISAPPEARED, not as diff regions:
    /// <see cref="SourceText.GetTextChanges"/> coalesces a rename's scattered edits into one span covering
    /// the whole file, which reported 1 for a file where four references moved. Counting the name itself
    /// also makes renameInComments/renameInStrings visible in the number, which a reference count would not.
    /// </remarks>
    private static async Task<IReadOnlyList<FileSummary>> FileSummariesAsync(
        Solution before, Solution after, IReadOnlyList<DocumentId> changedDocs, string oldName,
        SolutionLocator locator, CancellationToken cancellationToken)
    {
        var summaries = new List<FileSummary>();
        foreach (var docId in changedDocs)
        {
            var oldDoc = before.GetDocument(docId);
            var newDoc = after.GetDocument(docId);
            if (oldDoc is null || newDoc is null)
                continue;

            var oldText = (await oldDoc.GetTextAsync(cancellationToken)).ToString();
            var newText = (await newDoc.GetTextAsync(cancellationToken)).ToString();
            summaries.Add(new FileSummary(
                locator.RelPath(newDoc.FilePath ?? newDoc.Name),
                Math.Max(0, OccurrencesOf(oldText, oldName) - OccurrencesOf(newText, oldName))));
        }
        return [.. summaries.OrderByDescending(s => s.Occurrences).ThenBy(s => s.File, StringComparer.Ordinal)];
    }

    /// <summary>Whole-identifier occurrences of <paramref name="name"/> in <paramref name="text"/>.</summary>
    /// <remarks>
    /// The word-boundary test is what keeps a rename of <c>Foo</c> to <c>FooBar</c> from counting each
    /// rewritten site as still present afterwards.
    /// </remarks>
    private static int OccurrencesOf(string text, string name)
    {
        static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

        var count = 0;
        var i = 0;
        while (i <= text.Length - name.Length && (i = text.IndexOf(name, i, StringComparison.Ordinal)) >= 0)
        {
            var boundedLeft = i == 0 || !IsIdentifierPart(text[i - 1]);
            var end = i + name.Length;
            var boundedRight = end >= text.Length || !IsIdentifierPart(text[end]);
            if (boundedLeft && boundedRight)
                count++;
            i = end;
        }
        return count;
    }

    /// <summary>
    /// A note naming what already carried <paramref name="newName"/> in the renamed symbol's own container, or
    /// null when the name was free.
    /// </summary>
    /// <remarks>
    /// Nothing here compiles the result, so this note is how the caller learns of a collision before
    /// <c>dotnet build</c> does. A SAME-signature collision will not compile (CS0102/CS0229); a
    /// DIFFERENT-signature one compiles perfectly, because C# has simply gained an overload -- the
    /// caller asked for a rename and got an overload set, which may or may not be what was wanted.
    /// </remarks>
    /// <param name="target">The symbol being renamed, resolved against the pre-rename solution.</param>
    /// <param name="newName">The bare identifier it is being renamed to.</param>
    /// <returns>A one-sentence note, or null when nothing else declares the name.</returns>
    private static string? NameAlreadyExists(ISymbol target, string newName)
    {
        // Locals, parameters and type parameters are excluded: shadowing an outer name is ordinary C# and
        // reporting it would be noise, not a finding.
        if (target.Kind is not (SymbolKind.NamedType or SymbolKind.Method or SymbolKind.Property
            or SymbolKind.Field or SymbolKind.Event))
            return null;

        INamespaceOrTypeSymbol? container = target is INamedTypeSymbol { ContainingType: null } topLevel
            ? topLevel.ContainingNamespace
            : target.ContainingType;
        var existing = container?.GetMembers(newName)
            .Where(m => !SymbolEqualityComparer.Default.Equals(m, target))
            .ToList();
        if (existing is null || existing.Count == 0)
            return null;

        var kinds = string.Join(", ", existing.Take(3).Select(m => SymbolKey.KindOf(m).ToLowerInvariant()).Distinct());
        var clash = $"{container!.ToDisplayString()} already declares {existing.Count} member(s) named {newName} "
            + $"({kinds}). ";
        return clash + "If the signatures collide the result will not compile; if they differ, what you have "
            + "is an overload set rather than a rename. dotnet build tells you which.";
    }

    /// <summary>
    /// The one thing this tool deliberately does not do. A type whose file is named after it leaves that
    /// file misnamed after the rename, and moving it is a git operation plus a reload -- not a text edit.
    /// </summary>
    private static string? FileRenameHint(ISymbol target, string oldName, string newName, SolutionLocator locator)
    {
        if (target is not INamedTypeSymbol)
            return null;

        var paths = target.DeclaringSyntaxReferences
            .Select(r => r.SyntaxTree.FilePath)
            .Where(p => !string.IsNullOrEmpty(p)
                        && string.Equals(Path.GetFileNameWithoutExtension(p), oldName, StringComparison.Ordinal))
            .Select(locator.RelPath)
            .ToList();

        if (paths.Count == 0)
            return null;

        return $"This rename did NOT rename the file(s) named after the type: {string.Join(", ", paths)}. "
            + $"Run `git mv <path> <dir>/{newName}.cs` for each, then reload_workspace.";
    }

    private sealed record FileSummary(string File, int Occurrences);

    private static string Error(string kind, string message) =>
        Formats.Render(new { error = kind, message });
}
