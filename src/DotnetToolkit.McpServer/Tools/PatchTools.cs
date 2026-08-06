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
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace DotnetToolkit.McpServer.Tools;

    /// <summary>
    /// One edit in a validate_patch request (spec §13.4): a line-range replacement (<see cref="Lines"/> +
    /// <see cref="NewText"/>) or a symbol-scoped find/replace (<see cref="SymbolId"/> + <see cref="Find"/> +
    /// <see cref="Replace"/>) — exactly one mode per edit, never both, never neither.
    /// </summary>
    /// <param name="File">Repo-relative path. Required for line-range mode; omitted for find/replace mode, whose file is resolved from <see cref="SymbolId"/>.</param>
    /// <param name="SymbolId">A symbolId from a prior response. Required for find/replace mode. Optional for line-range mode, where -- on a fresh (non-amend) patch -- it is validated: <see cref="Lines"/> must fall inside this symbol's own declaration span.</param>
    /// <param name="Lines">Line-range mode: a 1-based, inclusive range as "N-M" (or a bare "N" for one line), addressing the file as the workspace holds it -- or, with draftId, the draft's proposed text.</param>
    /// <param name="NewText">Line-range mode: the replacement text for <see cref="Lines"/>, applied verbatim.</param>
    /// <param name="Find">Find/replace mode: literal text to locate inside <see cref="SymbolId"/>'s own declaration span. Errors if it occurs zero times, or more than once without <see cref="ReplaceAll"/>.</param>
    /// <param name="Replace">Find/replace mode: the text substituted for every match of <see cref="Find"/>.</param>
    /// <param name="ReplaceAll">Find/replace mode: replace every match instead of requiring exactly one. Default false.</param>
    public sealed record PatchEditInput(
        string? File = null,
        string? SymbolId = null,
        string? Lines = null,
        string? NewText = null,
        string? Find = null,
        string? Replace = null,
        bool? ReplaceAll = null);

/// <summary>
/// The v2 write path (spec §13). Applies edits to a forked in-memory solution, runs the validation
/// ladder to the level the change requires, distils diagnostics to root causes, and — only when the
/// result is sufficient and successful — commits to disk and appends one development-log record.
/// Never reports a green light it did not earn (C3); requires intent to apply (C8).
/// </summary>
[McpServerToolType]
public static class PatchTools
{
    [McpServerTool(Name = "validate_patch")]
    [Description("Validate (and optionally apply) a code change — the safe way to edit or modify a C# file — against an in-memory compilation before it "
        + "touches disk. Runs the cheapest sufficient level of the ladder (parse→semantic_bind→project_compile→"
        + "dependent_compile→targeted_tests→solution_validate) and reports honestly whether that was sufficient "
        + "for the change. baseVersions is required (stale context is rejected -- and a patch that rewrites a "
        + "BODY must hold a version carrying the body layer, which only a get_symbol that served the source, "
        + "bodyOutline or mechanicalFacts hands out); intent is required to apply. "
        + "Any result that was NOT applied returns a draft: pass its draftId back with only the lines you are "
        + "correcting, instead of resubmitting the whole patch.")]
    public static async Task<string> ValidatePatch(
        WorkspaceHost workspace,
        SolutionLocator locator,
        SymbolStore symbolStore,
        FeatureLogStore featureLog,
        SymbolIndexBuilder indexBuilder,
        TargetedTests targetedTests,
        TelemetryRecorder telemetry,
        PatchDraftStore drafts,
        [Description("Map of symbolId -> held contentVersion the patch was built against. Required, except with draftId, whose draft carries its own -- entries sent alongside a draftId are MERGED into it, which is how unheld_symbol is resolved.")] Dictionary<string, string>? baseVersions = null,
        [Description("The edits to apply, each EITHER a line-range edit {file, lines, newText[, symbolId]} OR a symbol-scoped find/replace {symbolId, find, replace[, replaceAll]} -- never both shapes in one edit. Line spans (\"N-M\", 1-based inclusive) address the file as the workspace holds it -- or, with draftId, the draft's proposed text. find/replace resolves against the LIVE workspace only; it is rejected alongside a draftId. May be empty ONLY with draftId, which re-runs validation on the draft unchanged.")] PatchEditInput[]? edits = null,
        [Description("Optional floor: raise (never lower) the required level. parse|semantic_bind|project_compile|dependent_compile|targeted_tests|solution_validate.")] string? requestedLevel = null,
        [Description("Whether to run Roslyn analyzers (CA/IDE rules) once the compile rungs are clean (default true). Set false when only the compile/semantic result matters -- checks.analyzers then reports ran:false with a skipReason instead of a verdict.")] bool runAnalyzers = true,
        [Description("Commit to disk when sufficient && successful (default false).")] bool applyOnSuccess = false,
        [Description("Why, in user terms. REQUIRED when applyOnSuccess is true (<=200 chars).")] string? intent = null,
        [Description("Optional tags.")] string[]? tags = null,
        [Description(ToolTelemetry.TaskIdParam)] string? taskId = null,
        [Description("Amend a previous unapplied patch instead of resubmitting it. Pass the draftId from that response's draft field; edits then address the DRAFT's line numbers -- the same coordinates the diagnostics' locations report -- and baseVersions is inherited, with anything you send merged in. Send only the lines you are correcting.")] string? draftId = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = Ids.AmbientSession;
        var attributedTask = Ids.TaskId(taskId);
        var toolCallId = Ids.ToolCall();
        var patchId = Ids.Patch();
        var validationAttemptId = Ids.ValidationAttempt();
        var stopwatch = Stopwatch.StartNew();

        // A rejected call still cost a round trip and the tokens its error payload carries. Record it as
        // a retrieval event, the way every other tool records its error payloads: the patch_events row
        // further down is written only once validation actually ran, so without this every reject --
        // stale_base most of all -- is invisible to get_retrieval_metrics, which is the instrument every
        // measurement of this server depends on.
        var requestedTarget = edits is { Length: > 0 } ? edits[0].File ?? edits[0].SymbolId ?? "" : draftId ?? "";
        string Reject(string errorKind, string json) =>
            ToolTelemetry.Record(telemetry, toolCallId, sessionId, attributedTask, "validate_patch",
                requestedTarget, json, errorKind: errorKind);
        string Fail(string errorKind, string message) => Reject(errorKind, Error(errorKind, message));

        PatchDraft? draft = null;
        if (draftId is not null)
        {
            draft = drafts.Get(draftId);
            if (draft is null)
                return Fail("unknown_draft",
                    $"No such draft. Drafts live {PatchDraftStore.Lifetime.TotalMinutes:0} minutes and only the "
                    + $"{PatchDraftStore.Capacity} most recent are kept, so this one expired or was evicted. "
                    + "Refetch the symbol with get_symbol and submit a full patch instead.");
        }

        // An amend is allowed to carry no edits at all: that is how a patch already reported succeeded but
        // insufficient is re-run at a higher requestedLevel without resending a line of its text.
        if (draft is null && (edits is null || edits.Length == 0))
            return Fail("no_edits", "At least one edit is required.");

        if (applyOnSuccess && string.IsNullOrWhiteSpace(intent))
            return Fail("intent_required",
                "applyOnSuccess requires a non-empty intent describing the why.");

        IReadOnlyDictionary<string, string>? inherited = baseVersions;
        if (draft is not null)
        {
            // An amend inherits the draft's map and may ADD to it. That is the cheap way out of
            // unheld_symbol below: resend the draftId with only the missing versions and no edits, rather
            // than retransmitting text that was never what the server objected to.
            var merged = new Dictionary<string, string>(draft.BaseVersions, StringComparer.Ordinal);
            if (baseVersions is not null)
            {
                foreach (var (id, version) in baseVersions)
                    merged[id] = version;
            }

            inherited = merged;
        }

        if (inherited is null)
            return Fail("missing_base_versions",
                "baseVersions is required so patches from stale context are rejected.");

        var heldVersions = inherited;

        // A provisional id never equals the live semantic tier's id for the same symbol -- they are
        // hashed from incompatible inputs by construction. Rejecting them outright here keeps a caller
        // who fetched during startup from getting a stale_base cascade across every symbol in the file,
        // which says nothing about the actual cause.
        var provisionalIds = heldVersions.Keys
            .Where(id => !id.StartsWith("sym_", StringComparison.Ordinal))
            .ToList();
        if (provisionalIds.Count > 0)
            return Fail("stale_index_only_id",
                $"baseVersions holds {provisionalIds.Count} id(s) not minted by the live semantic tier (a "
                + "real symbolId always starts with sym_) -- e.g. get_symbol's index_only fallback (symidx_) "
                + "or SymbolKey.IdOf's own no-doc-comment-id fallback (symfb_). Neither ever matches the live "
                + "tier's id for the same symbol -- call get_symbol again once the workspace has finished "
                + "loading (check workspace_status) and rebuild baseVersions from that response.");

        async Task<string> RunAsync()
        {
            var solution = await workspace.GetSolutionAsync();
            if (solution is null)
                return Fail("workspace_loading",
                    "The semantic workspace is not ready; retry shortly.");

            var (patchEdits, editsErrorKind, editsErrorJson) = await ResolveEditsAsync(
                solution, locator, symbolStore, edits ?? [], draft is not null, cancellationToken);
            if (editsErrorJson is not null)
                return Reject(editsErrorKind!, editsErrorJson);

            var sandbox = await PatchSandbox.ApplyAsync(solution, locator, patchEdits!, draft, cancellationToken);
            if (sandbox.Error is not null)
            {
                // A draft that no longer matches the workspace can never be amended again; drop it now so a
                // retry gets unknown_draft rather than repeating the same mismatch.
                if (sandbox.FailureKind == PatchSandbox.Failure.DraftStale && draftId is not null)
                    drafts.Remove(draftId);

                var sandboxError = sandbox.FailureKind switch
                {
                    PatchSandbox.Failure.StaleWorkspace => "stale_workspace",
                    PatchSandbox.Failure.DraftStale => "draft_stale",
                    _ => "invalid_edit",
                };
                return Fail(sandboxError, sandbox.Error);
            }

            var (detected, unchangedVersions) = await ChangeClassifier.DetectAsync(solution, sandbox.Forked, sandbox.ChangedDocuments, cancellationToken);

            // Two different failures used to share one error, and both were denied a draft. A version that
            // DISAGREES means the content moved under the patch: its text is built on assumptions that no
            // longer hold, so it must be rebuilt, and offering a cheap retry would only tempt the caller to
            // re-apply it. A version that is merely ABSENT means the classifier attributed a change to a
            // symbol the caller did not anticipate -- an added member anchors to its containing type, the
            // usual cause -- and the proposed text is perfectly good. Keeping that text as a draft makes
            // the fix cost one map entry instead of the whole patch.
            var disagreeing = detected
                .Where(c => heldVersions.TryGetValue(c.OldSymbolId, out var held)
                            && !ContentVersion.Parse(c.OldVersion).AgreesWith(ContentVersion.Parse(held)))
                .Select(c => (SymbolId: c.OldSymbolId, CurrentVersion: c.OldVersion));
            var staleUnchanged = heldVersions
                .Where(kv => unchangedVersions.TryGetValue(kv.Key, out var current)
                             && !ContentVersion.Parse(current).AgreesWith(ContentVersion.Parse(kv.Value)))
                .Select(kv => (SymbolId: kv.Key, CurrentVersion: unchangedVersions[kv.Key]));
            var stale = disagreeing.Concat(staleUnchanged).ToList();
            if (stale.Count > 0)
                return Reject("stale_base", StaleBase(stale));

            var unheld = detected
                .Where(c => !heldVersions.ContainsKey(c.OldSymbolId))
                .Select(c => (SymbolId: c.OldSymbolId, CurrentVersion: c.OldVersion))
                .ToList();
            if (unheld.Count > 0)
                return Reject("unheld_symbol", UnheldSymbol(unheld,
                    await DraftInfoAsync(drafts, sandbox, solution, heldVersions, locator, cancellationToken)));

            // A held token only proves the layers it actually carries: get_symbol narrows its token to the
            // components it served, so the default fetch leases decl (+refs) and no body, and AgreesWith
            // compares shared layers only. A patch rewriting a body against such a token was never checked
            // against the body it overwrites -- exactly the concurrent-edit case baseVersions exists to
            // reject -- and it succeeded silently. Demand the layer rather than skipping the check.
            var unleasedBody = detected
                .Where(c => c.Kinds.Contains(ChangeKind.Body)
                            && ContentVersion.Parse(c.OldVersion).Get("body") is not null
                            && ContentVersion.Parse(heldVersions[c.OldSymbolId]).Get("body") is null)
                .Select(c => (SymbolId: c.OldSymbolId, CurrentVersion: c.OldVersion))
                .ToList();

            // The classifier reports SEMANTIC changes, so a comment- or doc-comment-only body rewrite
            // produced no Change at all and never reached the check above -- while the guard's own
            // rationale ("a concurrent edit to the body would have been overwritten silently") applies to
            // it identically, and a second agent rewriting comments is precisely the likely case. Keyed on
            // whether the patch touches body TEXT, which is answerable from the edit spans alone.
            var textTouched = await BodyTextTouchedIdsAsync(solution, locator, patchEdits!, cancellationToken);
            var alreadyUnleased = unleasedBody.Select(u => u.SymbolId).ToHashSet(StringComparer.Ordinal);
            unleasedBody.AddRange(textTouched
                .Where(id => !alreadyUnleased.Contains(id)
                             && heldVersions.TryGetValue(id, out var held)
                             && ContentVersion.Parse(held).Get("body") is null
                             && unchangedVersions.TryGetValue(id, out var current)
                             && ContentVersion.Parse(current).Get("body") is not null)
                .Select(id => (SymbolId: id, CurrentVersion: unchangedVersions[id])));
            if (unleasedBody.Count > 0)
                return Reject("unleased_body", UnleasedBody(unleasedBody,
                    await DraftInfoAsync(drafts, sandbox, solution, heldVersions, locator, cancellationToken)));

            var changedIds = detected.Select(c => c.OldSymbolId).Distinct(StringComparer.Ordinal).ToList();
            var affectedTests = symbolStore.TestsReferencing(changedIds);
            var testedIds = affectedTests.Count > 0
                ? changedIds.Where(id => symbolStore.ReferenceCounts(id)?.Tests > 0).ToHashSet(StringComparer.Ordinal)
                : [];

            var computedRequired = EscalationTable.RequiredForPatch(
                detected.Select(c => ((IReadOnlyCollection<ChangeKind>)c.Kinds, testedIds.Contains(c.OldSymbolId))));

            // The escalation table maxes over the symbols the classifier attributed a change to, so an
            // empty set floors it at parse. Text can change without any symbol changing: a using
            // directive, a file-scoped namespace, an assembly attribute -- none of which sit inside a
            // declaration -- and trivia-blind fingerprints mean a comment-only edit lands here too.
            // Parse alone cannot tell those apart: `using Nope.Missing;` is syntactically perfect and
            // fails to bind, so applying on a parse pass writes a file whose project no longer compiles
            // and reports succeeded:true doing it. Changed text is enough to demand a real compile.
            if (detected.Count == 0 && sandbox.ChangedDocuments.Count > 0
                && (int)computedRequired < (int)ValidationLevel.ProjectCompile)
                computedRequired = ValidationLevel.ProjectCompile;

            var required = Raise(computedRequired, requestedLevel);

            var ladder = await ValidationLadder.RunAsync(
                sandbox.Forked, sandbox.ChangedDocuments, required,
                testRunner: ct => targetedTests.RunAsync(affectedTests, ct),
                runAnalyzers: runAnalyzers,
                cancellationToken: cancellationToken);
            var isSufficient = ladder.Succeeded && (int)ladder.Completed >= (int)required;

            var distillation = ladder.Succeeded
                ? new DiagnosticDistiller.Distillation([], 0, 0)
                : await DiagnosticDistiller.DistillAsync(sandbox.Forked, locator, ladder.FailingDiagnostics,
                    detected.Select(c => (c.SymbolId, c.DisplayString)).ToList(), cancellationToken);

            var applied = false;
            if (applyOnSuccess && isSufficient && ladder.Succeeded)
            {
                applied = await CommitAsync(sandbox.Forked, sandbox.ChangedDocuments, locator);
                if (applied)
                {
                    workspace.AdoptAppliedText(sandbox.Forked, sandbox.ChangedDocuments);
                    AppendLog(featureLog, attributedTask, patchId, intent!, tags, detected, ladder, required);
                    indexBuilder.Start();
                }
            }

            // Nothing reached disk, so the caller has to come back. Retain the proposed text under a handle
            // they can correct a few lines of, instead of making them resend the whole patch to fix one.
            var draftInfo = applied
                ? null
                : await DraftInfoAsync(drafts, sandbox, solution, heldVersions, locator, cancellationToken);

            var response = BuildResponse(locator, detected, ladder, required, isSufficient, applied, distillation, draftInfo);
            var json = Formats.Render(response);

            telemetry.RecordPatch(new TelemetryRecorder.PatchEvent
            {
                ToolCallId = toolCallId,
                PatchId = patchId,
                ValidationAttemptId = validationAttemptId,
                SessionId = sessionId,
                TaskId = attributedTask,
                ChangedSymbolIdsJson = JsonSerializer.Serialize(detected.Select(c => c.SymbolId)),
                ChangeKindsJson = JsonSerializer.Serialize(detected.SelectMany(c => c.Kinds.Select(k => k.Wire())).Distinct()),
                BaseVersionsJson = JsonSerializer.Serialize(baseVersions),
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

            return json;
        }

        return applyOnSuccess
            ? await workspace.RunExclusiveApplyAsync(RunAsync)
            : await RunAsync();
    }

    // internal, not private: rename_symbol produces a validated fork the same way a patch does, and must
    // reach disk through this one writer so both tools share the identical commit semantics.
    internal static async Task<bool> CommitAsync(Solution forked, IReadOnlyList<DocumentId> changedDocs, SolutionLocator locator)
    {
        try
        {
            foreach (var docId in changedDocs)
            {
                var document = forked.GetDocument(docId)!;
                var text = await document.GetTextAsync();
                var path = document.FilePath ?? locator.AbsPath(document.Name);

                // Write back in the encoding the file was read in. The default here is UTF-8 without a
                // BOM, which silently strips the BOM Visual Studio puts on files it creates - a
                // whole-file diff on Windows for a one-line patch.
                var encoding = text.Encoding ?? new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                await File.WriteAllTextAsync(path, text.ToString(), encoding);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // internal: the development log has exactly one writer, and rename_symbol's applies must land in it
    // under the same schema as a patch's rather than through a second, drifting copy.
    internal static void AppendLog(
        FeatureLogStore featureLog, string taskId, string patchId, string intent, string[]? tags,
        IReadOnlyList<ChangeClassifier.Change> detected, ValidationLadder.LadderResult ladder, ValidationLevel required)
    {
        var validationJson = JsonSerializer.Serialize(new
        {
            completedLevel = ladder.Completed.Wire(),
            requiredLevel = required.Wire(),
            succeeded = ladder.Succeeded,
        });
        featureLog.Append(new FeatureLogStore.LogEntry(
            taskId, patchId, null, intent, tags ?? [], validationJson,
            detected.Select(c => new FeatureLogStore.LogSymbol(
                c.SymbolId, c.OldSymbolId == c.SymbolId ? null : c.OldSymbolId,
                c.Kinds.Select(k => k.Wire()).ToList(), c.Detail,
                c.OldVersion, c.NewVersion, c.ApiImpact)).ToList()));
    }

    private static object BuildResponse(
        SolutionLocator locator,
        IReadOnlyList<ChangeClassifier.Change> detected, ValidationLadder.LadderResult ladder,
        ValidationLevel required, bool isSufficient, bool applied, DiagnosticDistiller.Distillation distillation,
        object? draft)
    {
        var (reason, nextAction) = Verdict(ladder, required, isSufficient);
        return new
        {
            detectedChanges = detected.Select(c => new
            {
                symbolId = c.SymbolId,
                changeKinds = c.Kinds.Select(k => k.Wire()).ToList(),
                oldVersion = c.OldVersion,
                newVersion = applied ? c.NewVersion : null,
                apiImpact = c.ApiImpact,
                // Where the declaration sits in the text this call produced: the file itself once applied,
                // otherwise the draft. Same shape and same bounds as get_symbol's declarationSites, so a
                // follow-up edit to this symbol needs no refetch just to recover its shifted line span.
                declarationSites = c.NewSymbol is null ? null : ContextTools.DeclarationSites(c.NewSymbol, locator),
            }),
            ladder = new
            {
                completedLevel = ladder.Completed.Wire(),
                requiredLevel = required.Wire(),
                isSufficient,
                reason = isSufficient ? null : reason,
                nextAction = isSufficient ? null : nextAction,
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
            // Emitted on every run, pass or fail: which rungs ran and over what, what the analyzer pass
            // found at each severity, and what went unchecked. A clean run has to say so explicitly --
            // an absent diagnostics block alone cannot distinguish "found nothing" from "looked at nothing".
            checks = CheckReport.Build(ladder, locator),
            draft,
        };
    }

    private static (string Reason, string NextAction) Verdict(ValidationLadder.LadderResult ladder, ValidationLevel required, bool isSufficient)
    {
        var analyzers = ladder.Analyzers;
        if (isSufficient)
        {
            var advisory = analyzers switch
            {
                { Ran: true } a when a.Warnings.Count + a.Suggestions.Count > 0 =>
                    $" {a.Warnings.Count} analyzer warning(s) and {a.Suggestions.Count} suggestion(s) in checks.analyzers; neither blocks.",
                { IsClean: true } => " Analyzers clean over the changed documents.",
                _ => "",
            };
            return ($"Validated to {required.Wire()}.{advisory}", "None — change is validated to the required level.");
        }
        if (!ladder.Succeeded)
        {
            // An analyzer error is a different fix from a compile error: the rule's severity is the repo's
            // own .editorconfig/warnaserror choice, so changing that configuration is a legitimate response.
            return analyzers is { Ran: true, Errors.Count: > 0 }
                ? ("Analyzer diagnostics at effective severity error blocked the change.",
                    "Fix the reported analyzer errors, or lower their severity in .editorconfig if the rule is wrong for this repo.")
                : ($"Validation failed at {ladder.Completed.Wire()}.",
                    "Fetch the suggested symbols, revise the patch, and resubmit.");
        }
        return ($"Healthy through {ladder.Completed.Wire()} but the change requires {required.Wire()}.",
            $"Re-call validate_patch with requestedLevel={required.Wire()}.");
    }

    // internal: shared with rename_symbol so requestedLevel means the same thing on both tools.
    internal static ValidationLevel Raise(ValidationLevel computed, string? requestedLevel)
    {
        if (string.IsNullOrWhiteSpace(requestedLevel))
            return computed;
        var requested = requestedLevel.Trim().ToLowerInvariant() switch
        {
            "parse" => ValidationLevel.Parse,
            "semantic_bind" => ValidationLevel.SemanticBind,
            "project_compile" => ValidationLevel.ProjectCompile,
            "dependent_compile" => ValidationLevel.DependentCompile,
            "targeted_tests" => ValidationLevel.TargetedTests,
            "solution_validate" => ValidationLevel.SolutionValidate,
            _ => computed,
        };
        return (ValidationLevel)Math.Max((int)computed, (int)requested);
    }

    /// <summary>Stores a fork as an amendable draft and renders the handle the caller sends back.</summary>
    /// <param name="drafts">The store that assigns the id and deadline.</param>
    /// <param name="sandbox">The successful fork whose proposed text is being retained.</param>
    /// <param name="solution">The unforked solution, source of the baseline used to detect later drift.</param>
    /// <param name="heldVersions">The versions this patch was built against, inherited by every amend of the draft.</param>
    /// <param name="locator">Renders each retained file's path repo-relative for the response.</param>
    /// <param name="cancellationToken">Cancels the document text reads this makes.</param>
    /// <returns>An anonymous object carrying the draftId, its expiry, and the files it covers.</returns>
    private static async Task<object> DraftInfoAsync(
        PatchDraftStore drafts, PatchSandbox.Result sandbox, Solution solution,
        IReadOnlyDictionary<string, string> heldVersions, SolutionLocator locator,
        CancellationToken cancellationToken)
    {
        var stored = drafts.Put(await PatchSandbox.DraftOfAsync(sandbox, solution, heldVersions, cancellationToken));
        return new
        {
            draftId = stored.Id,
            expiresAt = stored.ExpiresAt,
            files = stored.Proposed
                .Select(kv => new { file = locator.RelPath(kv.Key), lineCount = kv.Value.Lines.Count })
                .ToList(),
        };
    }

    private static string UnheldSymbol(IReadOnlyList<(string SymbolId, string CurrentVersion)> unheld, object draft) =>
        Formats.Render(new
        {
            error = "unheld_symbol",
            message = "This patch changes symbols no baseVersions entry covers -- an added member anchors to "
                + "its containing type, which is the usual cause. Nothing is wrong with the proposed text, so "
                + "it is kept as a draft: resend its draftId with these versions in baseVersions and an empty "
                + "edits array, rather than retransmitting the patch.",
            current = unheld.Select(c => new { symbolId = c.SymbolId, currentVersion = c.CurrentVersion }),
            draft,
        });

    /// <summary>
    /// The rejection for a body-changing patch whose held version never carried a body layer, so the
    /// staleness check could not cover the text the patch overwrites.
    /// </summary>
    /// <param name="unleased">The changed symbols whose body layer was not held, with their current versions.</param>
    /// <param name="draft">The retained proposed text, which is not what was objected to.</param>
    /// <returns>The rendered error payload.</returns>
    private static string UnleasedBody(IReadOnlyList<(string SymbolId, string CurrentVersion)> unleased, object draft) =>
        Formats.Render(new
        {
            error = "unleased_body",
            message = "This patch rewrites a body against a contentVersion that carries no body layer, so "
                + "staleness was verified for the declaration only and a concurrent edit to the body would "
                + "have been overwritten silently. get_symbol narrows its token to what it served: refetch "
                + "with an include that serves the body (source, bodyOutline or mechanicalFacts), then "
                + "resend this draftId with that version in baseVersions and an empty edits array. The "
                + "versions below already carry the layer, so they can be sent as-is.",
            current = unleased.Select(c => new { symbolId = c.SymbolId, currentVersion = c.CurrentVersion }),
            draft,
        });

    /// <summary>
    /// Symbols whose BODY TEXT the patch's edit lines fall inside, whether or not the edit changed
    /// anything the classifier would call a change.
    /// </summary>
    /// <param name="solution">The base solution the edit line numbers address.</param>
    /// <param name="locator">Resolves an edit's repo-relative path to a document.</param>
    /// <param name="edits">The patch's edits.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The touched symbols' ids; empty when no edit lands in a body.</returns>
    /// <remarks>
    /// Deliberately a TEXT question, not a semantic one: the body lease exists to prove the caller held
    /// the text it is overwriting, and a comment-only rewrite overwrites text just as a semantic one does.
    /// An edit landing on a signature or between members touches no body and is unaffected.
    /// </remarks>
    private static async Task<IReadOnlyCollection<string>> BodyTextTouchedIdsAsync(
        Solution solution,
        SolutionLocator locator,
        IReadOnlyList<PatchEdit> edits,
        CancellationToken cancellationToken)
    {
        var touched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in edits.GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase))
        {
            var documentId = solution.GetDocumentIdsWithFilePath(locator.AbsPath(group.Key)).FirstOrDefault();
            if (documentId is null || solution.GetDocument(documentId) is not { } document)
                continue;

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var model = await document.GetSemanticModelAsync(cancellationToken);
            var text = await document.GetTextAsync(cancellationToken);
            if (root is null || model is null)
                continue;

            var members = root.DescendantNodes().OfType<MemberDeclarationSyntax>().ToList();
            foreach (var edit in group)
            {
                if (edit.StartLine < 1 || edit.EndLine < edit.StartLine || edit.EndLine > text.Lines.Count)
                    continue;

                var span = TextSpan.FromBounds(
                    text.Lines[edit.StartLine - 1].Start,
                    text.Lines[edit.EndLine - 1].End);

                foreach (var member in members)
                {
                    if (BodySpanOf(member) is not { } body || !body.IntersectsWith(span))
                        continue;
                    if (model.GetDeclaredSymbol(member, cancellationToken) is { } symbol)
                        touched.Add(SymbolKey.IdOf(symbol));
                }
            }
        }

        return touched;
    }

    /// <summary>The span of a member's executable body, or null when it has none to overwrite.</summary>
    /// <param name="member">The member declaration to measure.</param>
    /// <returns>The block, accessor list or expression-body span.</returns>
    private static TextSpan? BodySpanOf(MemberDeclarationSyntax member) => member switch
    {
        BaseMethodDeclarationSyntax { Body: { } block } => block.Span,
        BaseMethodDeclarationSyntax { ExpressionBody: { } arrow } => arrow.Span,
        PropertyDeclarationSyntax { AccessorList: { } accessors } => accessors.Span,
        PropertyDeclarationSyntax { ExpressionBody: { } arrow } => arrow.Span,
        _ => null,
    };

    private static string StaleBase(IReadOnlyList<(string SymbolId, string CurrentVersion)> stale) =>
        Formats.Render(new
        {
            error = "stale_base",
            message = "Patch built against outdated content; refetch these versions and rebuild.",
            current = stale.Select(c => new { symbolId = c.SymbolId, currentVersion = c.CurrentVersion }),
        });

    private static string Error(string kind, string message) =>
        Formats.Render(new { error = kind, message });

    private static bool TryParseLines(string lines, out int startLine, out int endLine)
    {
        startLine = endLine = 0;
        var dash = lines.IndexOf('-');
        if (dash < 0)
        {
            if (!int.TryParse(lines, out startLine) || startLine < 1)
                return false;
            endLine = startLine;
            return true;
        }
        if (!int.TryParse(lines[..dash], out startLine) || !int.TryParse(lines[(dash + 1)..], out endLine))
            return false;
        return startLine >= 1 && endLine >= startLine;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static async Task<(ISymbol? Symbol, string? ErrorJson)> ResolveEditSymbolAsync(
        Solution solution, SymbolStore symbolStore, string symbolId, CancellationToken cancellationToken)
    {
        var fqName = symbolStore.FqNameFor(symbolId);
        if (fqName is null)
            return (null, Error("symbol_not_found",
                $"No symbol known by id {symbolId}. Refetch it with get_symbol or search_index."));

        var resolution = await SymbolResolver.ResolveAsync(solution, fqName, cancellationToken);
        if (resolution.Symbol is null)
            return (null, Error("symbol_not_found",
                $"{symbolId} (\"{fqName}\") no longer resolves uniquely in the live workspace -- it may have moved or been removed. Refetch it with get_symbol."));

        return (resolution.Symbol, null);
    }

    /// <summary>
    /// Resolves every input edit to a concrete line-range <see cref="PatchEdit"/>, dispatching on which
    /// mode each carries (spec §13.4). A line-range edit's optional symbolId is cross-checked against
    /// that symbol's own live declaration span; a find/replace edit is resolved against the symbol's
    /// current text into an equivalent line-range edit over its whole declaration span.
    /// </summary>
    /// <remarks>
    /// Both modes reduce to the same <see cref="PatchEdit"/> shape before reaching
    /// <see cref="PatchSandbox.ApplyAsync"/>, so nothing downstream needs to know which one produced it.
    /// find/replace and the symbolId cross-check both address the LIVE workspace, never a draft's
    /// proposed text -- an amend keeps them out of scope rather than checking against stale coordinates.
    /// </remarks>
    private static async Task<(List<PatchEdit>? Edits, string? ErrorKind, string? ErrorJson)> ResolveEditsAsync(
        Solution solution, SolutionLocator locator, SymbolStore symbolStore,
        IReadOnlyList<PatchEditInput> edits, bool amending, CancellationToken cancellationToken)
    {
        var resolved = new List<PatchEdit>(edits.Count);
        foreach (var e in edits)
        {
            var isFindReplace = e.Find is not null || e.Replace is not null;
            var isLineRange = e.Lines is not null || e.NewText is not null || e.File is not null;
            if (isFindReplace == isLineRange)
                return (null, "invalid_edit", Error("invalid_edit",
                    "Each edit is either line-range (file, lines, newText) or find/replace (symbolId, find, replace) -- not both, and not neither."));

            if (isFindReplace)
            {
                if (e.SymbolId is null || string.IsNullOrEmpty(e.Find) || e.Replace is null)
                    return (null, "invalid_edit", Error("invalid_edit",
                        "A find/replace edit requires symbolId, a non-empty find, and replace."));
                if (amending)
                    return (null, "find_replace_requires_fresh_patch", Error("find_replace_requires_fresh_patch",
                        "find/replace edits resolve against the live workspace's text, not a draft's proposed text -- resend a fresh patch instead of amending."));

                var (symbol, symbolError) = await ResolveEditSymbolAsync(solution, symbolStore, e.SymbolId, cancellationToken);
                if (symbol is null)
                    return (null, "symbol_not_found", symbolError);

                var sites = ContextTools.DeclarationSpans(symbol, locator);
                if (sites.Count != 1)
                    return (null, "ambiguous_declaration_sites", Error("ambiguous_declaration_sites",
                        $"find/replace targets exactly one declaration site; {e.SymbolId} has {sites.Count} (a partial type split across files). Use a line-range edit against the specific file instead."));

                var site = sites[0];
                var documentId = solution.GetDocumentIdsWithFilePath(locator.AbsPath(site.File)).FirstOrDefault();
                if (documentId is null || solution.GetDocument(documentId) is not { } document)
                    return (null, "symbol_not_found", Error("symbol_not_found",
                        $"{e.SymbolId}'s file is not part of the loaded solution."));

                var text = await document.GetTextAsync(cancellationToken);
                if (site.StartLine < 1 || site.EndLine > text.Lines.Count)
                    return (null, "symbol_not_found", Error("symbol_not_found",
                        $"{e.SymbolId}'s declaration span no longer fits its file; refetch it with get_symbol."));

                var span = TextSpan.FromBounds(text.Lines[site.StartLine - 1].Start, text.Lines[site.EndLine - 1].End);
                var body = text.ToString(span);
                var occurrences = CountOccurrences(body, e.Find);
                if (occurrences == 0)
                    return (null, "find_not_found", Error("find_not_found",
                        $"\"{e.Find}\" does not occur inside {e.SymbolId}'s own declaration span ({site.File}:{site.StartLine}-{site.EndLine})."));
                if (occurrences > 1 && e.ReplaceAll != true)
                    return (null, "ambiguous_find_match", Error("ambiguous_find_match",
                        $"\"{e.Find}\" occurs {occurrences} times inside {e.SymbolId}'s span -- narrow the text or pass replaceAll: true to replace every occurrence."));

                resolved.Add(new PatchEdit(site.File, site.StartLine, site.EndLine,
                    body.Replace(e.Find, e.Replace, StringComparison.Ordinal)));
            }
            else
            {
                if (e.File is null || e.Lines is null || e.NewText is null)
                    return (null, "invalid_edit", Error("invalid_edit",
                        "A line-range edit requires file, lines and newText."));
                if (!TryParseLines(e.Lines, out var startLine, out var endLine))
                    return (null, "invalid_edit", Error("invalid_edit",
                        $"lines must be \"N\" or \"N-M\" (1-based, inclusive): \"{e.Lines}\""));

                if (e.SymbolId is not null && !amending)
                {
                    var (symbol, symbolError) = await ResolveEditSymbolAsync(solution, symbolStore, e.SymbolId, cancellationToken);
                    if (symbol is null)
                        return (null, "symbol_not_found", symbolError);

                    var sites = ContextTools.DeclarationSpans(symbol, locator);
                    var withinAny = sites.Any(s => PathComparison.Comparer.Equals(locator.AbsPath(s.File), locator.AbsPath(e.File))
                        && startLine >= s.StartLine && endLine <= s.EndLine);
                    if (!withinAny)
                        return (null, "edit_outside_symbol", Error("edit_outside_symbol",
                            $"lines {startLine}-{endLine} of {e.File} do not fall inside {e.SymbolId}'s own declaration span "
                            + $"({string.Join("; ", sites.Select(s => $"{s.File}:{s.StartLine}-{s.EndLine}"))}). "
                            + "Refetch the symbol and rebuild the edit from its reported span."));
                }

                resolved.Add(new PatchEdit(e.File, startLine, endLine, e.NewText));
            }
        }

        return (resolved, null, null);
    }
}
