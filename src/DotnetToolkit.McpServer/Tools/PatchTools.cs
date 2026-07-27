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
        [Description("Map of symbolId -> held contentVersion the patch was built against. Required, except with draftId (a draft carries its own).")] Dictionary<string, string> baseVersions,
        [Description("The edits to apply. Line spans address the file as the workspace holds it -- or, with draftId, the draft's proposed text. May be empty ONLY with draftId, which re-runs validation on the draft unchanged.")] PatchEditInput[] edits,
        [Description("Optional floor: raise (never lower) the required level. parse|semantic_bind|project_compile|dependent_compile|targeted_tests|solution_validate.")] string? requestedLevel = null,
        [Description("Commit to disk when sufficient && successful (default false).")] bool applyOnSuccess = false,
        [Description("Why, in user terms. REQUIRED when applyOnSuccess is true (<=200 chars).")] string? intent = null,
        [Description("Optional tags.")] string[]? tags = null,
        [Description("Amend a previous unapplied patch instead of resubmitting it. Pass the draftId from that response's draft field; edits then address the DRAFT's line numbers -- the same coordinates the diagnostics' locations report -- and baseVersions is inherited. Send only the lines you are correcting.")] string? draftId = null)
    {
        var sessionId = Ids.AmbientSession;
        var taskId = sessionId;
        var toolCallId = Ids.ToolCall();
        var patchId = Ids.Patch();
        var validationAttemptId = Ids.ValidationAttempt();
        var stopwatch = Stopwatch.StartNew();

        PatchDraft? draft = null;
        if (draftId is not null)
        {
            draft = drafts.Get(draftId);
            if (draft is null)
                return Error("unknown_draft",
                    $"No such draft. Drafts live {PatchDraftStore.Lifetime.TotalMinutes:0} minutes and only the "
                    + $"{PatchDraftStore.Capacity} most recent are kept, so this one expired or was evicted. "
                    + "Refetch the symbol with get_symbol and submit a full patch instead.");

            if (baseVersions is { Count: > 0 })
                return Error("draft_base_versions_conflict",
                    "An amend inherits baseVersions from the draft it corrects; omit baseVersions when passing draftId.");
        }

        // An amend is allowed to carry no edits at all: that is how a patch already reported succeeded but
        // insufficient is re-run at a higher requestedLevel without resending a line of its text.
        if (draft is null && (edits is null || edits.Length == 0))
            return Error("no_edits", "At least one edit is required.");

        if (applyOnSuccess && string.IsNullOrWhiteSpace(intent))
            return Error("intent_required",
                "applyOnSuccess requires a non-empty intent describing the why.");

        IReadOnlyDictionary<string, string>? inherited = draft?.BaseVersions ?? baseVersions;
        if (inherited is null)
            return Error("missing_base_versions",
                "baseVersions is required so patches from stale context are rejected.");

        var heldVersions = inherited;
        var staleIndexOnlyIds = heldVersions.Keys.Where(id => !id.StartsWith("sym_", StringComparison.Ordinal)).ToList();
        if (staleIndexOnlyIds.Count > 0)
            return Error("stale_index_only_id",
                $"baseVersions holds {staleIndexOnlyIds.Count} id(s) not minted by the live semantic tier (a real symbolId always starts with sym_) -- e.g. get_symbol's index_only fallback (symidx_) or SymbolKey.IdOf's own no-doc-comment-id fallback (symfb_). Neither ever matches the live tier's id for the same symbol -- call get_symbol again now that the workspace has finished loading and rebuild baseVersions from that response.");

        async Task<string> RunAsync()
        {
            var solution = await workspace.GetSolutionAsync();
            if (solution is null)
                return Error("workspace_loading",
                    "The semantic workspace is not ready; retry shortly.");

            var patchEdits = (edits ?? []).Select(e => new PatchEdit(e.File, e.StartLine, e.EndLine, e.NewText)).ToList();
            var sandbox = await PatchSandbox.ApplyAsync(solution, locator, patchEdits, draft);
            if (sandbox.Error is not null)
            {
                // A draft that no longer matches the workspace can never be amended again; drop it now so a
                // retry gets unknown_draft rather than repeating the same mismatch.
                if (sandbox.FailureKind == PatchSandbox.Failure.DraftStale && draftId is not null)
                    drafts.Remove(draftId);

                return Error(sandbox.FailureKind switch
                {
                    PatchSandbox.Failure.StaleWorkspace => "stale_workspace",
                    PatchSandbox.Failure.DraftStale => "draft_stale",
                    _ => "invalid_edit",
                }, sandbox.Error);
            }

            var detected = await ChangeClassifier.DetectAsync(solution, sandbox.Forked, sandbox.ChangedDocuments);

            var stale = detected
                .Where(c => !heldVersions.TryGetValue(c.OldSymbolId, out var held)
                            || !ContentVersion.Parse(c.OldVersion).AgreesWith(ContentVersion.Parse(held)))
                .ToList();
            if (stale.Count > 0)
                return StaleBase(stale);

            var changedIds = detected.Select(c => c.OldSymbolId).Distinct(StringComparer.Ordinal).ToList();
            var affectedTests = symbolStore.TestsReferencing(changedIds);
            var testedIds = affectedTests.Count > 0
                ? changedIds.Where(id => symbolStore.ReferenceCounts(id)?.Tests > 0).ToHashSet(StringComparer.Ordinal)
                : [];

            var computedRequired = EscalationTable.RequiredForPatch(
                detected.Select(c => ((IReadOnlyCollection<ChangeKind>)c.Kinds, testedIds.Contains(c.OldSymbolId))));
            var required = Raise(computedRequired, requestedLevel);

            var ladder = await ValidationLadder.RunAsync(
                sandbox.Forked, sandbox.ChangedDocuments, required,
                testRunner: ct => targetedTests.RunAsync(affectedTests, ct));
            var isSufficient = ladder.Succeeded && (int)ladder.Completed >= (int)required;

            var distillation = ladder.Succeeded
                ? new DiagnosticDistiller.Distillation([], 0, 0)
                : await DiagnosticDistiller.DistillAsync(sandbox.Forked, locator, ladder.FailingDiagnostics,
                    detected.Select(c => (c.SymbolId, c.DisplayString)).ToList());

            var applied = false;
            if (applyOnSuccess && isSufficient && ladder.Succeeded)
            {
                applied = await CommitAsync(sandbox.Forked, sandbox.ChangedDocuments, locator);
                if (applied)
                {
                    workspace.AdoptAppliedText(sandbox.Forked, sandbox.ChangedDocuments);
                    AppendLog(featureLog, taskId, patchId, intent!, tags, detected, ladder, required);
                    indexBuilder.Start();
                }
            }

            // Nothing reached disk, so the caller has to come back. Retain the proposed text under a handle
            // they can correct a few lines of, instead of making them resend the whole patch to fix one.
            object? draftInfo = null;
            if (!applied)
            {
                var stored = drafts.Put(await PatchSandbox.DraftOfAsync(sandbox, solution, heldVersions));
                draftInfo = new
                {
                    draftId = stored.Id,
                    expiresAt = stored.ExpiresAt,
                    files = stored.Proposed
                        .Select(kv => new { file = locator.RelPath(kv.Key), lineCount = kv.Value.Lines.Count })
                        .ToList(),
                };
            }

            var response = BuildResponse(detected, ladder, required, isSufficient, applied, distillation, draftInfo);
            var json = Formats.Render(response);

            telemetry.RecordPatch(new TelemetryRecorder.PatchEvent
            {
                ToolCallId = toolCallId,
                PatchId = patchId,
                ValidationAttemptId = validationAttemptId,
                SessionId = sessionId,
                TaskId = taskId,
                ChangedSymbolIdsJson = JsonSerializer.Serialize(detected.Select(c => c.SymbolId)),
                ChangeKindsJson = JsonSerializer.Serialize(detected.SelectMany(c => c.Kinds.Select(k => k.Wire())).Distinct()),
                BaseVersionsJson = JsonSerializer.Serialize(heldVersions),
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

    private static string StaleBase(IReadOnlyList<ChangeClassifier.Change> stale) =>
        Formats.Render(new
        {
            error = "stale_base",
            message = "Patch built against outdated content; refetch these versions and rebuild.",
            current = stale.Select(c => new { symbolId = c.OldSymbolId, currentVersion = c.OldVersion }),
        });

    private static string Error(string kind, string message) =>
        Formats.Render(new { error = kind, message });
}
