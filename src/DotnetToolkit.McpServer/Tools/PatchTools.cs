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
using ModelContextProtocol.Server;

namespace DotnetToolkit.McpServer.Tools;

/// <summary>One line-span edit in a validate_patch request (spec §13.3).</summary>
public sealed record PatchEditInput(string File, int StartLine, int EndLine, string NewText);

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
    [Description("Validate (and optionally apply) a code change against an in-memory compilation before it "
        + "touches disk. Runs the cheapest sufficient level of the ladder (parse→semantic_bind→project_compile→"
        + "dependent_compile→targeted_tests→solution_validate) and reports honestly whether that was sufficient "
        + "for the change. baseVersions is required (stale context is rejected); intent is required to apply. "
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
        [Description("The edits to apply. Line spans address the file as the workspace holds it -- or, with draftId, the draft's proposed text. May be empty ONLY with draftId, which re-runs validation on the draft unchanged.")] PatchEditInput[]? edits = null,
        [Description("Optional floor: raise (never lower) the required level. parse|semantic_bind|project_compile|dependent_compile|targeted_tests|solution_validate.")] string? requestedLevel = null,
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
        var requestedTarget = edits is { Length: > 0 } ? edits[0].File : draftId ?? "";
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

            var patchEdits = (edits ?? []).Select(e => new PatchEdit(e.File, e.StartLine, e.EndLine, e.NewText)).ToList();
            var sandbox = await PatchSandbox.ApplyAsync(solution, locator, patchEdits, draft, cancellationToken);
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

    private static async Task<bool> CommitAsync(Solution forked, IReadOnlyList<DocumentId> changedDocs, SolutionLocator locator)
    {
        try
        {
            foreach (var docId in changedDocs)
            {
                var document = forked.GetDocument(docId)!;
                var text = await document.GetTextAsync();
                var path = document.FilePath ?? locator.AbsPath(document.Name);
                await File.WriteAllTextAsync(path, text.ToString());
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AppendLog(
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
            draft,
        };
    }

    private static (string Reason, string NextAction) Verdict(ValidationLadder.LadderResult ladder, ValidationLevel required, bool isSufficient)
    {
        if (isSufficient)
            return ($"Validated to {required.Wire()}.", "None — change is validated to the required level.");
        if (!ladder.Succeeded)
            return ($"Validation failed at {ladder.Completed.Wire()}.",
                "Fetch the suggested symbols, revise the patch, and resubmit.");
        return ($"Healthy through {ladder.Completed.Wire()} but the change requires {required.Wire()}.",
            $"Re-call validate_patch with requestedLevel={required.Wire()}.");
    }

    private static ValidationLevel Raise(ValidationLevel computed, string? requestedLevel)
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

    private static string StaleBase(IReadOnlyList<(string SymbolId, string CurrentVersion)> stale) =>
        Formats.Render(new
        {
            error = "stale_base",
            message = "Patch built against outdated content; refetch these versions and rebuild.",
            current = stale.Select(c => new { symbolId = c.SymbolId, currentVersion = c.CurrentVersion }),
        });

    private static string Error(string kind, string message) =>
        Formats.Render(new { error = kind, message });
}
