using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using DotnetToolkit.McpServer.Contracts;
using DotnetToolkit.McpServer.Identity;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Telemetry;
using DotnetToolkit.McpServer.Validation;
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
/// This is deliberately NOT a thin wrapper that emits edits for validate_patch to apply. A rename's edits
/// are derived mechanically from the semantic model — the caller never writes them — so the whole
/// baseVersions/draft/amend apparatus that exists to guard *authored* text has nothing to guard here
/// beyond the one symbol being renamed. Everything downstream of the fork (the ladder, the distiller, the
/// commit, the development-log entry) is shared with validate_patch so both tools stay honest in the same
/// way and write the same history.
/// </remarks>
[McpServerToolType]
public static class RenameTools
{
    [McpServerTool(Name = "rename_symbol")]
    [Description("Rename a C# symbol and every reference to it across the whole solution — rename a class, interface, "
        + "method, property, field or parameter everywhere it is used, from the compiler's own model. USE THIS "
        + "INSTEAD OF a search-and-replace or a pile of validate_patch calls, which miss "
        + "interface/virtual/delegate dispatch and silently rewrite unrelated text that happens to share the "
        + "name. Resolves the symbol, applies Roslyn's rename to an in-memory fork, runs the same validation "
        + "ladder validate_patch runs (so a rename onto a name whose SIGNATURE also collides is reported as a "
        + "compile failure and NOTHING reaches disk; one whose signature differs is legal C# and succeeds, "
        + "producing an overload set rather than a rename -- nameAlreadyExists says so when it happens), and on "
        + "apply writes the same development-log entry a patch "
        + "would. baseVersion is a SINGLE version string here, not validate_patch's symbolId->version map, "
        + "because only one symbol is named and the rest is derived; get it from a get_symbol on that symbol. "
        + "It is required, so a rename built on stale context is rejected; intent is required to apply. Unlike "
        + "validate_patch, a dry run (applyOnSuccess:false) is worth it on a widely-referenced symbol, because "
        + "the blast radius is what you cannot predict. Does not rename the containing FILE — when that is "
        + "wanted the response says so under fileRenameHint, and the move is a git mv plus reload_workspace.")]
    public static async Task<string> RenameSymbol(
        WorkspaceHost workspace,
        SolutionLocator locator,
        SymbolStore symbolStore,
        FeatureLogStore featureLog,
        SymbolIndexBuilder indexBuilder,
        TargetedTests targetedTests,
        TelemetryRecorder telemetry,
        [Description("The symbol to rename: fully-qualified name, a unique suffix, or a sym_... id from a previous response. Append a parameter list to pick one overload.")] string symbol,
        [Description("The new identifier. Must be a valid C# identifier and not a keyword; the '@' prefix is accepted for a verbatim identifier.")] string newName,
        [Description("The contentVersion get_symbol handed out for this symbol. Required, so a rename built against content that has since moved is rejected rather than applied.")] string? baseVersion = null,
        [Description("Commit to disk when sufficient && successful (default false). A false run is a full dry run: it reports every file that would change, and every conflict, without writing.")] bool applyOnSuccess = false,
        [Description("Why, in user terms. REQUIRED when applyOnSuccess is true (<=200 chars).")] string? intent = null,
        [Description("Also rename the other overloads of this method (default false). Ignored for a non-method symbol.")] bool renameOverloads = false,
        [Description("Also update occurrences of the old name inside comments and doc comments (default false). Textual and best-effort, unlike the reference rewrite itself.")] bool renameInComments = false,
        [Description("Also update occurrences of the old name inside string literals (default false). Textual and best-effort; leave off unless the name is genuinely reflected over.")] bool renameInStrings = false,
        [Description("Optional floor: raise (never lower) the required level. parse|semantic_bind|project_compile|dependent_compile|targeted_tests|solution_validate. An unrecognized value is not honored -- ladder.requestedLevelHint in the response says so and names what it probably was.")] string? requestedLevel = null,
        [Description("Optional tags for the development-log entry.")] string[]? tags = null,
        [Description(ToolTelemetry.TaskIdParam)] string? taskId = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = Ids.AmbientSession;
        var attributedTask = Ids.TaskId(taskId);
        var toolCallId = Ids.ToolCall();
        var patchId = Ids.Patch();
        var stopwatch = Stopwatch.StartNew();

        // Mirrors validate_patch: a rejected call still cost a round trip and the tokens of its error
        // payload, so it is recorded as a retrieval event rather than vanishing from get_retrieval_metrics.
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
        // stood, so the rename would fail at the ladder with a wall of syntax errors instead of here.
        if (!newName.StartsWith('@') && SyntaxFacts.GetKeywordKind(bare) != SyntaxKind.None)
            return Fail("invalid_name",
                $"'{newName}' is a C# keyword; prefix it with '@' if a verbatim identifier is really intended.");

        if (string.IsNullOrWhiteSpace(baseVersion))
            return Fail("missing_base_version",
                "baseVersion is required so a rename built from stale context is rejected. Call get_symbol "
                + "for this symbol and pass back its contentVersion.");

        if (applyOnSuccess && string.IsNullOrWhiteSpace(intent))
            return Fail("intent_required", "applyOnSuccess requires a non-empty intent describing the why.");

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
            var currentVersion = ContextTools.VersionOf(target);
            if (!currentVersion.AgreesWith(ContentVersion.Parse(baseVersion)))
                return Reject("stale_base", Formats.Render(new
                {
                    error = "stale_base",
                    message = "This rename was built against outdated content; refetch the symbol and retry.",
                    current = new[] { new { symbolId = oldSymbolId, currentVersion = currentVersion.ToString() } },
                }));

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

            // One DocumentId per file: a linked or multi-targeted file appears once per project, and both
            // CommitAsync (which writes whole documents to their FilePath) and the log entry would otherwise
            // double-count it.
            var changedDocs = forked.GetChanges(solution)
                .GetProjectChanges()
                .SelectMany(p => p.GetChangedDocuments())
                .DistinctBy(id => forked.GetDocument(id)?.FilePath ?? id.ToString(), StringComparer.Ordinal)
                .ToList();

            if (changedDocs.Count == 0)
                return Fail("no_changes",
                    $"Renaming '{oldName}' to '{bare}' produced no text changes — nothing references it under "
                    + "a name this rename would rewrite.");

            // Same reason PatchSandbox refuses a drifted fork: an apply writes the WHOLE document back, so a
            // rename computed over a workspace copy that lags disk silently reverts everything else in that
            // file. baseVersion covers the renamed symbol only, not the rest of every file it touches.
            foreach (var docId in changedDocs)
            {
                var before = solution.GetDocument(docId);
                if (before?.FilePath is null)
                    continue;
                if (await DiskDrift.DriftedAsync(before.FilePath, await before.GetTextAsync(cancellationToken)))
                    return Fail("stale_workspace", $"the workspace's copy of {locator.RelPath(before.FilePath)} "
                        + "is behind disk; reload_workspace and retry the rename");
            }

            var (detected, _) = await ChangeClassifier.DetectAsync(solution, forked, changedDocs, cancellationToken);

            // Reported separately from `detected`: escalation, tests and diagnostics below must still see
            // every change, but the caller-facing list should not restate the mechanical half of it.
            var reported = WithoutMechanicalRekeys(detected, out var membersRekeyed);

            var changedIds = detected.Select(c => c.OldSymbolId).Distinct(StringComparer.Ordinal).ToList();
            var affectedTests = symbolStore.TestsReferencing(changedIds);
            var testedIds = affectedTests.Count > 0
                ? changedIds.Where(id => symbolStore.ReferenceCounts(id)?.Tests > 0).ToHashSet(StringComparer.Ordinal)
                : [];

            var computedRequired = EscalationTable.RequiredForPatch(
                detected.Select(c => ((IReadOnlyCollection<ChangeKind>)c.Kinds, testedIds.Contains(c.OldSymbolId))));

            // A rename alters an existing contract by definition, and its call sites can sit in any project
            // that depends on this one — so dependent_compile is the floor regardless of what the classifier
            // attributed. Without it a rename whose only detected change reads as a body edit (a file that
            // merely *references* the symbol) would be signed off by a single project's compile.
            if ((int)computedRequired < (int)ValidationLevel.DependentCompile)
                computedRequired = ValidationLevel.DependentCompile;

            var (required, requestedLevelRecognized) = PatchTools.Raise(computedRequired, requestedLevel);
                var requestedLevelHint = PatchTools.RequestedLevelHint(requestedLevel, requestedLevelRecognized, required);

            var ladder = await ValidationLadder.RunAsync(
                forked, changedDocs, required,
                original: solution,
                testRunner: ct => targetedTests.RunAsync(affectedTests, ct),
                cancellationToken: cancellationToken);
            var isSufficient = ladder.Succeeded && (int)ladder.Completed >= (int)required;

            var distillation = ladder.Succeeded
                ? new DiagnosticDistiller.Distillation([], 0, 0)
                : await DiagnosticDistiller.DistillAsync(forked, locator, ladder.FailingDiagnostics,
                    detected.Select(c => (c.SymbolId, c.DisplayString)).ToList(), cancellationToken);

            var applied = false;
            if (applyOnSuccess && isSufficient && ladder.Succeeded)
            {
                applied = await PatchTools.CommitAsync(forked, changedDocs, locator);
                if (applied)
                {
                    workspace.AdoptAppliedText(forked, changedDocs);
                    PatchTools.AppendLog(featureLog, attributedTask, patchId, intent!, tags, detected, ladder, required);
                    indexBuilder.Start();
                }
            }

            var files = await FileSummariesAsync(solution, forked, changedDocs, oldName, locator, cancellationToken);
            // A collision (renaming to a name already in scope) is not rejected up front - Roslyn's Renamer
            // just renames the syntax and lets the resulting duplicate surface as a compile error (CS0102/
            // CS0229) below. Every symbol id is a hash of its fully-qualified name, so in that case the
            // rewritten symbol's id is IDENTICAL to the pre-existing symbol's id it collided with - there is
            // no way to compute a distinct id for it. Only resolve/expose it once the ladder actually
            // succeeded; on failure the id would silently identify the wrong symbol (self-eval, 2026-08-10).
            var newSymbol = ladder.Succeeded ? RenamedSymbolId(detected, target, oldSymbolId, bare) : null;


            var response = new
            {
                rename = new
                {
                    oldName,
                    newName = bare,
                    oldSymbolId,
                    newSymbolId = newSymbol,
                    kind = SymbolKey.KindOf(target),
                    filesChanged = files.Count,
                    occurrencesRewritten = files.Sum(f => f.Occurrences),
                },
                files = files.Select(f => new { file = f.File, occurrences = f.Occurrences }),
                membersRekeyed = membersRekeyed == 0 ? (int?)null : membersRekeyed,
                detectedChanges = reported.Select(c => new
                {
                    // Same reason newSymbol above is gated on ladder.Succeeded: on a failed rename this id
                    // can alias a different, pre-existing symbol rather than identify the one just renamed.
                    symbolId = ladder.Succeeded ? c.SymbolId : null,
                    // Dropped only when it would duplicate symbolId. On a FAILED rename symbolId is withheld
                    // for the reason above, so dropping this one too left entries carrying neither -- a bare
                    // changeKinds:[removed] with no declarationSites and nothing to fetch. The pre-rename id is
                    // resolvable either way, since a failed rename wrote nothing to disk.
                    previousSymbolId = ladder.Succeeded && c.OldSymbolId == c.SymbolId ? null : c.OldSymbolId,
                    changeKinds = c.Kinds.Select(k => k.Wire()).ToList(),
                    apiImpact = c.ApiImpact,
                    declarationSites = c.NewSymbol is null ? null : ContextTools.DeclarationSites(c.NewSymbol, locator),
                }),

                ladder = new
                {
                    completedLevel = ladder.Completed.Wire(),
                    requiredLevel = required.Wire(),
                    isSufficient,
                    reason = isSufficient ? null : Reason(ladder, required, workspace.IsDegraded),
                    nextAction = isSufficient ? null : NextAction(ladder, required, workspace.IsDegraded),
                        requestedLevelHint,
                },
                succeeded = ladder.Succeeded,
                applied,
                testFailures = ladder.TestFailureOutput,
                diagnostics = distillation.RootCauses.Count == 0 ? null : new
                {
                    rootCauses = distillation.RootCauses.Select(rc => new
                    {
                        diagnostic = rc.Diagnostic,
                        summary = rc.Summary,
                        affectedSymbolId = rc.AffectedSymbolId,
                        fixHint = rc.FixHint,
                        locations = rc.Sites.Select(s => new { file = s.File, line = s.Line, column = s.Column, excerpt = s.Excerpt }).ToList(),
                        suggestedInspection = rc.SuggestedInspection.Select(i => new { symbolId = i.SymbolId, displayString = i.DisplayString }).ToList(),
                        suppressedDiagnostics = rc.SuppressedDiagnostics,
                    }),
                    totalRaw = distillation.TotalRaw,
                    totalSuppressed = distillation.TotalSuppressed,
                },
                // Same block validate_patch emits, for the same reason: a rename that reports success has
                // to say what that success covered, and which checks never ran.
                checks = CheckReport.Build(ladder, locator),
                fileRenameHint = FileRenameHint(target, oldName, bare, locator),
                nameAlreadyExists = NameAlreadyExists(target, bare, ladder.Succeeded),
                // Same reason validate_patch emits it: a rename derived from a degraded workspace's own
                // reference graph can miss call sites entirely, which is a wrong answer, not a thin one.
                limitedBy = workspace.IsDegraded ? "degraded" : null,
            };

            var json = Formats.Render(response);

            telemetry.RecordPatch(new TelemetryRecorder.PatchEvent
            {
                ToolCallId = toolCallId,
                PatchId = patchId,
                ValidationAttemptId = Ids.ValidationAttempt(),
                SessionId = sessionId,
                TaskId = attributedTask,
                ChangedSymbolIdsJson = JsonSerializer.Serialize(detected.Select(c => c.SymbolId)),
                ChangeKindsJson = JsonSerializer.Serialize(detected.SelectMany(c => c.Kinds.Select(k => k.Wire())).Distinct()),
                BaseVersionsJson = JsonSerializer.Serialize(new Dictionary<string, string> { [oldSymbolId] = baseVersion }),
                CompletedLevel = ladder.Completed.Wire(),
                RequiredLevel = required.Wire(),
                IsSufficient = isSufficient,
                Succeeded = ladder.Succeeded,
                Applied = applied,
                Intent = intent,
                RawDiagnostics = distillation.TotalRaw,
                DistilledDiagnostics = distillation.RootCauses.Count,
                ReturnedTokens = TelemetryRecorder.EstimateTokens(json),
                DurationMs = stopwatch.ElapsedMilliseconds,
            });

            return ToolTelemetry.Record(telemetry, toolCallId, sessionId, attributedTask, "rename_symbol",
                symbol, json, symbolId: oldSymbolId, contentVersion: baseVersion);
        }

        return applyOnSuccess
            ? await workspace.RunExclusiveApplyAsync(RunAsync)
            : await RunAsync();
    }

    /// <summary>
    /// The detected changes worth reporting, with the added/removed pairs a type rename produces for its
    /// own members collapsed into a count.
    /// </summary>
    /// <param name="detected">Every change the classifier attributed to the rename.</param>
    /// <param name="membersRekeyed">How many members were dropped as mechanical re-keys.</param>
    /// <returns>The changes to render, in their original order.</returns>
    /// <remarks>
    /// A symbolId encodes its containing type, so renaming a type re-keys every member it declares and the
    /// classifier sees each one as a removal plus an addition — for members whose own names never changed.
    /// On an 8-member type that was 16 of 24 entries and about 65% of the response, and it tagged each pair
    /// breaking-public, overstating one breaking change as nine.
    /// <para>
    /// Paired by bare member name, so a member genuinely added in the same operation as an unrelated one
    /// removed under the same name would also collapse. That is the deliberate trade: a rename is not a
    /// shape-changing edit, and the count still says how many ids moved.
    /// </para>
    /// </remarks>
    private static List<ChangeClassifier.Change> WithoutMechanicalRekeys(
        List<ChangeClassifier.Change> detected,
        out int membersRekeyed)
    {
        static bool IsSolely(ChangeClassifier.Change change, ChangeKind kind) =>
            change.Kinds.Count == 1 && change.Kinds.Contains(kind);

        static string BareName(string display)
        {
            var withoutParameters = display.Split('(')[0].TrimEnd();
            var lastDot = withoutParameters.LastIndexOf('.');
            return lastDot >= 0 ? withoutParameters[(lastDot + 1)..] : withoutParameters;
        }

        var removed = detected.Where(c => IsSolely(c, ChangeKind.Removed)).Select(c => BareName(c.DisplayString));
        var added = detected.Where(c => IsSolely(c, ChangeKind.Added)).Select(c => BareName(c.DisplayString)).ToHashSet(StringComparer.Ordinal);
        var rekeyed = removed.Where(added.Contains).ToHashSet(StringComparer.Ordinal);

        membersRekeyed = rekeyed.Count;
        if (rekeyed.Count == 0)
            return detected;

        return [.. detected.Where(c =>
            !((IsSolely(c, ChangeKind.Added) || IsSolely(c, ChangeKind.Removed))
              && rekeyed.Contains(BareName(c.DisplayString))))];
    }

    /// <summary>The renamed symbol's new id, or null when the classifier's view cannot identify it.</summary>
    /// <remarks>
    /// Not simply "the change whose OldSymbolId is the target": <c>ChangeClassifier</c>'s rename pairing
    /// only computes a name-stripped signature for a method, property or event, so a renamed TYPE arrives
    /// as an unpaired removed-plus-added pair. The removed half carries SymbolId == OldSymbolId == the old
    /// id, so the obvious lookup matched it and reported the OLD id as the new one — observed renaming
    /// DiskDrift, where the response claimed the id had not changed at all.
    ///
    /// Identify the new symbol by what it actually is instead: the changed symbol now carrying the new
    /// name, of the same kind as the target. That is correct for both the paired and the unpaired shape.
    /// The paired lookup is still tried first, since it is exact when the classifier managed the pairing.
    /// </remarks>
    private static string? RenamedSymbolId(
        IReadOnlyList<ChangeClassifier.Change> detected, ISymbol target, string oldSymbolId, string newName)
    {
        var paired = detected.FirstOrDefault(c => c.OldSymbolId == oldSymbolId && c.SymbolId != oldSymbolId);
        if (paired is not null)
            return paired.SymbolId;

        var targetKind = SymbolKey.KindOf(target);
        return detected.FirstOrDefault(c =>
            c.NewSymbol is { } s
            && string.Equals(s.Name, newName, StringComparison.Ordinal)
            && SymbolKey.KindOf(s) == targetKind)?.SymbolId;
    }

    private sealed record FileSummary(string File, int Occurrences);

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
    /// It fires on both outcomes, and says something different about each. A SAME-signature collision does not
    /// compile, and this names the clash the distilled CS0102/CS0229 is reporting. A DIFFERENT-signature one
    /// compiles perfectly and reports succeeded:true, because C# has simply gained an overload -- so the caller
    /// asked for a rename, got an overload set, and nothing in the response said the name was taken. That case
    /// is a note rather than a failure because the resulting code is legal and may well be what was wanted.
    /// </remarks>
    /// <param name="target">The symbol being renamed, resolved against the pre-rename solution.</param>
    /// <param name="newName">The bare identifier it is being renamed to.</param>
    /// <param name="succeeded">Whether the ladder passed, which decides what the clash MEANS.</param>
    /// <returns>A one-sentence note, or null when nothing else declares the name.</returns>
    private static string? NameAlreadyExists(ISymbol target, string newName, bool succeeded)
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
        return succeeded
            ? clash + "The rename still succeeded, because the signatures differ -- so what you have now is an "
                + "overload set rather than a rename."
            : clash + "That is almost certainly the collision the diagnostics report: pick a different name, or "
                + "resolve the clash with validate_patch first, then retry.";
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

    /// <summary>Why a rename that did not fully validate stopped where it did.</summary>
    /// <remarks>Internal for the same reason as <see cref="NextAction"/>.</remarks>
    internal static string Reason(ValidationLadder.LadderResult ladder, ValidationLevel required, bool degraded) =>
        ladder switch
        {
            { Analyzers: { Ran: true, Errors.Count: > 0 } } =>
                "Analyzer diagnostics at effective severity error blocked the rename.",
            { Succeeded: false } when degraded =>
                $"Rename failed validation at {ladder.Completed.Wire()}, against a DEGRADED workspace.",
            { Succeeded: false } => $"Rename failed validation at {ladder.Completed.Wire()}.",
            _ => $"Healthy through {ladder.Completed.Wire()} but the rename requires {required.Wire()}.",
        };

    /// <summary>What to do about a rename that did not fully validate.</summary>
    /// <remarks>Internal rather than private so the degraded wording can be asserted without a fixture
    /// project that fails MSBuild on purpose.</remarks>
    internal static string NextAction(ValidationLadder.LadderResult ladder, ValidationLevel required, bool degraded) =>
        ladder switch
        {
            // A rename cannot introduce an analyzer error by itself; the new name tripping a naming or
            // documentation rule is the realistic cause, so pointing at the rule's severity is the fix path.
            { Analyzers: { Ran: true, Errors.Count: > 0 } } =>
                "Fix the reported analyzer errors, or lower their severity in .editorconfig if the rule is "
                  + "wrong for this repo, then retry.",
            // A degraded workspace reports errors the rename did not cause, and "the new name collides" is
            // then a confident misdiagnosis that sends the caller looking for a collision that isn't there.
            { Succeeded: false } when degraded =>
                "Call workspace_status first: projects that failed to load report errors this rename did not "
                  + "introduce. Fix the load failure and reload_workspace before picking a different name.",
            { Succeeded: false } =>
                "The new name collides or breaks a call site. Inspect the suggested symbols, pick a different "
                  + "name or fix the collision with validate_patch first, then retry.",
            _ => $"Re-call rename_symbol with requestedLevel={required.Wire()}.",
        };

    private static string Error(string kind, string message) =>
        Formats.Render(new { error = kind, message });
}
