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
        + "for the change. baseVersions is required (stale context is rejected); intent is required to apply.")]
    public static async Task<string> ValidatePatch(
        WorkspaceHost workspace,
        SolutionLocator locator,
        SymbolStore symbolStore,
        FeatureLogStore featureLog,
        SymbolIndexBuilder indexBuilder,
        TargetedTests targetedTests,
        TelemetryRecorder telemetry,
        [Description("Map of symbolId -> held contentVersion the patch was built against. Required.")] Dictionary<string, string> baseVersions,
        [Description("The edits to apply.")] PatchEditInput[] edits,
        [Description("Optional floor: raise (never lower) the required level. parse|semantic_bind|project_compile|dependent_compile|targeted_tests|solution_validate.")] string? requestedLevel = null,
        [Description("Commit to disk when sufficient && successful (default false).")] bool applyOnSuccess = false,
        [Description("Why, in user terms. REQUIRED when applyOnSuccess is true (<=200 chars).")] string? intent = null,
        [Description("Optional tags.")] string[]? tags = null,
        [Description("Optional agent conversation id (ses_...) for telemetry grouping.")] string? sessionId = null,
        [Description("Optional user task id (tsk_...) for telemetry grouping.")] string? taskId = null)
    {
        sessionId ??= Ids.AmbientSession;
        taskId ??= Ids.UnattributedTask;
        var toolCallId = Ids.ToolCall();
        var patchId = Ids.Patch();
        var validationAttemptId = Ids.ValidationAttempt();
        var stopwatch = Stopwatch.StartNew();

        if (edits is null || edits.Length == 0)
            return Error("no_edits", "At least one edit is required.");

        if (applyOnSuccess && string.IsNullOrWhiteSpace(intent))
            return Error("intent_required",
                "applyOnSuccess requires a non-empty intent describing the why.");

        if (baseVersions is null)
            return Error("missing_base_versions",
                "baseVersions is required so patches from stale context are rejected.");

        async Task<string> RunAsync()
        {
            var solution = await workspace.GetSolutionAsync();
            if (solution is null)
                return Error("workspace_loading",
                    "The semantic workspace is not ready; retry shortly.");

            var patchEdits = edits.Select(e => new PatchEdit(e.File, e.StartLine, e.EndLine, e.NewText)).ToList();
            var sandbox = await PatchSandbox.ApplyAsync(solution, locator, patchEdits);
            if (sandbox.Error is not null)
                return Error(sandbox.Stale ? "stale_workspace" : "invalid_edit", sandbox.Error);

            var detected = await ChangeClassifier.DetectAsync(solution, sandbox.Forked, sandbox.ChangedDocuments);

            var stale = detected
                .Where(c => !baseVersions.TryGetValue(c.OldSymbolId, out var held)
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
                : await DiagnosticDistiller.DistillAsync(sandbox.Forked, ladder.FailingDiagnostics,
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

            var response = BuildResponse(detected, ladder, required, isSufficient, applied, distillation);
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
        IReadOnlyList<ChangeClassifier.Change> detected, ValidationLadder.LadderResult ladder,
        ValidationLevel required, bool isSufficient, bool applied, DiagnosticDistiller.Distillation distillation)
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
                    suggestedInspection = rc.SuggestedInspection.Select(i => new { symbolId = i.SymbolId, displayString = i.DisplayString }).ToList(),
                    suppressedDiagnostics = rc.SuppressedDiagnostics,
                }),
                totalRaw = distillation.TotalRaw,
                totalSuppressed = distillation.TotalSuppressed,
            },
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
