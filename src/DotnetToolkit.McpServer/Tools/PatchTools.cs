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
        var format = Formats.Parse(locator.Config.DefaultFormat);

        if (edits is null || edits.Length == 0)
            return Error("no_edits", "At least one edit is required.", format);

        // C8: applying requires an intent, rejected before any validation runs.
        if (applyOnSuccess && string.IsNullOrWhiteSpace(intent))
            return Error("intent_required",
                "applyOnSuccess requires a non-empty intent describing the why.", format);

        if (baseVersions is null)
            return Error("missing_base_versions",
                "baseVersions is required so patches from stale context are rejected.", format);

        // The fetch-validate-commit-adopt sequence below is wrapped in a local function so an apply can
        // run it under workspace.RunExclusiveApplyAsync -- otherwise two concurrent applies could each
        // fetch this same pre-commit solution, both pass their staleness checks against it, and both
        // write, the second silently clobbering the first. Read-only calls (applyOnSuccess: false) skip
        // the gate: they never touch disk, so there is nothing to race.
        async Task<string> RunAsync()
        {
            var solution = await workspace.GetSolutionAsync();
            if (solution is null)
                return Error("workspace_loading",
                    "The semantic workspace is not ready; retry shortly.", format);

            var patchEdits = edits.Select(e => new PatchEdit(e.File, e.StartLine, e.EndLine, e.NewText)).ToList();
            var sandbox = await PatchSandbox.ApplyAsync(solution, locator, patchEdits);
            if (sandbox.Error is not null)
                return Error(sandbox.Stale ? "stale_workspace" : "invalid_edit", sandbox.Error, format);

            var detected = await ChangeClassifier.DetectAsync(solution, sandbox.Forked, sandbox.ChangedDocuments);

            // stale_base: every changed symbol's current (old) version must match what the caller held.
            // Keyed on the old symbol id — an arity/rename change gives the result a new id the agent
            // never held, so matching the new id would spuriously reject a validly-based patch.
            // Compared per layer, not by string equality: the caller may hold a four-layer token while the
            // classifier computes only the syntax layers, and a layer one side never computed is not
            // evidence that the base moved.
            var stale = detected
                .Where(c => !baseVersions.TryGetValue(c.OldSymbolId, out var held)
                            || !ContentVersion.Parse(c.OldVersion).AgreesWith(ContentVersion.Parse(held)))
                .ToList();
            if (stale.Count > 0)
                return StaleBase(stale, format);

            // Which changed symbols are covered by tests decides whether the ladder must reach level 5.
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

            // C3: never apply unless the result is both sufficient and successful.
            var applied = false;
            if (applyOnSuccess && isSufficient && ladder.Succeeded)
            {
                applied = await CommitAsync(sandbox.Forked, sandbox.ChangedDocuments, locator);
                if (applied)
                {
                    // Both tiers have to move with the disk write, or the next patch to this file reads as
                    // drifted against its own predecessor.
                    workspace.AdoptAppliedText(sandbox.Forked, sandbox.ChangedDocuments);
                    AppendLog(featureLog, taskId, patchId, intent!, tags, detected, ladder, required);
                    indexBuilder.Start(); // refresh the symbol index against the new on-disk content
                }
            }

            var response = BuildResponse(detected, ladder, required, isSufficient, applied, distillation);
            var json = Formats.Render(response, format);

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
            // A CompactTable like every other multi-item response (search_index, get_references,
            // get_symbol's batch mode, search_log) -- column names paid for once instead of repeated per
            // changed symbol. oldVersion is the current on-disk identity (what a retry must send as
            // baseVersions); newVersion only describes reality once the patch is actually on disk.
            detectedChanges = CompactTable.Of(
                ["symbolId", "changeKinds", "oldVersion", "newVersion", "apiImpact"],
                detected,
                c => new object?[]
                {
                    c.SymbolId,
                    c.Kinds.Select(k => k.Wire()).ToList(),
                    c.OldVersion,
                    applied ? c.NewVersion : null,
                    c.ApiImpact,
                }),
            // The honest triple plus the imperative next step. Per-level timings live in telemetry, not
            // here — completedLevel already identifies where the ladder stopped.
            // reason/nextAction are emitted only when they add something: on a sufficient, successful
            // run they merely restate the triple above and say "do nothing".
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
            // Present only when level 5 actually ran and failed; compiler diagnostics live below.
            testFailures = ladder.TestFailureOutput,
            diagnostics = distillation.RootCauses.Count == 0 ? null : new
            {
                rootCauses = CompactTable.Of(
                    ["diagnostic", "summary", "affectedSymbolId", "fixHint", "suggestedInspection", "suppressedDiagnostics"],
                    distillation.RootCauses,
                    rc => new object?[]
                    {
                        rc.Diagnostic,
                        rc.Summary,
                        rc.AffectedSymbolId,
                        rc.FixHint,
                        rc.SuggestedInspection.Select(i => new { symbolId = i.SymbolId, displayString = i.DisplayString }).ToList(),
                        rc.SuppressedDiagnostics,
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

    private static string StaleBase(IReadOnlyList<ChangeClassifier.Change> stale, OutputFormat format) =>
        Formats.Render(new
        {
            error = "stale_base",
            message = "Patch built against outdated content; refetch these versions and rebuild.",
            current = stale.Select(c => new { symbolId = c.OldSymbolId, currentVersion = c.OldVersion }),
        }, format);

    private static string Error(string kind, string message, OutputFormat format) =>
        Formats.Render(new { error = kind, message }, format);
}
