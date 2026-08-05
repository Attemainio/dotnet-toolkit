namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// <c>PreToolUse</c> guard on <c>Edit</c>/<c>Write</c>/<c>NotebookEdit</c>: keeps C# edits on the
/// <c>validate_patch</c> write path.
/// </summary>
/// <remarks>
/// CLAUDE.md has carried this rule since the plugin existed and it is still the one broken most often,
/// because CLAUDE.md is context, not enforcement, and adherence decays over a long session. The compile
/// check is the cheap half of what <c>validate_patch</c> does; the development-log entry is the half
/// that cannot be recovered later, so an <c>Edit</c> that slips through is a permanent hole in the log
/// rather than a slower route to the same place.
/// </remarks>
internal static class GuardCsEdit
{
    /// <summary>Decides whether an edit tool call may proceed.</summary>
    /// <param name="payload">The parsed hook payload.</param>
    /// <param name="context">The repo and docs paths to cite in a denial.</param>
    /// <returns>A denial for a C# file that already exists; otherwise <see cref="HookOutcome.Allow"/>.</returns>
    public static HookOutcome Evaluate(HookPayload payload, HookContext context)
    {
        var file = payload.FilePath;
        if (string.IsNullOrEmpty(file) || !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            // Everything else is explicitly the plain-tool path: csproj, json, md, sh. .csx/.cshtml/.razor
            // are not what validate_patch operates on either.
            return HookOutcome.Allow;
        }

        // Creating a new file is not a validate_patch case: it needs baseVersions keyed by a symbolId,
        // and a symbol that does not exist yet has no version to lease against. Write it, then make
        // every subsequent change through the tool.
        if (payload.ToolName == "Write" && !File.Exists(file))
        {
            return HookOutcome.Allow;
        }

        // A .cs file no project compiles is one validate_patch cannot write either: it resolves edits
        // through the loaded solution and answers file_not_in_solution for anything outside it. Denying
        // the plain tool there leaves NO write path at all -- the case that found this is this repo's own
        // tests/.../fixtures/SampleSolution, deliberately excluded from the build so tests can load it
        // as a workspace of its own. GuardCsRead has always gated on membership; this guard did not, so
        // the same file was readable and uneditable. Membership, not the extension, is the real test.
        var absolute = Path.GetFullPath(Path.Combine(context.WorkingDirectory, file));
        if (File.Exists(absolute)
            && !CsFileMembership.TryResolveOwningProject(absolute, context.Root, out _))
        {
            return HookOutcome.Allow;
        }

        // $$ raw string: {{expr}} interpolates, a single brace is literal. The message quotes
        // validate_patch's own argument shapes, which are full of braces.
        return HookOutcome.Deny($$"""
            Blocked {{payload.ToolName}} on {{file}}: C# edits go through validate_patch, not {{payload.ToolName}}.

            validate_patch is the write path for .cs files, not a faster dotnet build. It is also the ONLY thing
            that appends to the development log — an edit made with {{payload.ToolName}} is a change whose reasoning is gone
            the moment this conversation ends, and search_log can never recover it.

            Do this instead:
              1. get_symbol on the target symbol; keep its contentVersion and the declarationSites line span.
                 Use include: "all" when the edit rewrites a body — the default fetch's contentVersion carries no
                 body layer, and a body edit built on it is rejected with unleased_body.
              2. validate_patch with baseVersions {symbolId: contentVersion}, line-span edits, applyOnSuccess
                 true, and an intent in user terms. Nothing is written unless the result is sufficient, so
                 there is no reason to dry-run with applyOnSuccess false first.
              3. If it fails, the response carries diagnostics.rootCauses[].locations (where the error landed
                 in the text you proposed) and a draft {draftId}. Send that draftId back with ONLY the lines
                 you are correcting — baseVersions is inherited and the spans address the draft. Do not
                 resubmit the whole patch.

            A change that feels too large or too interleaved to decompose is still not a reason to fall back to
            {{payload.ToolName}} — split it into more validate_patch calls, one per touched symbol, sharing one intent.

            If the change is a pure RENAME, do not hand-author the call-site edits at all: rename_symbol takes the
            symbol, the new name and its contentVersion, derives every reference edit from the compiler's own graph
            (including interface, virtual and delegate dispatch, which a hand-written patch set misses), and runs
            the same ladder and the same log entry. See {{context.Doc("rename_symbol")}}

            Full arguments, the sufficiency triple, and every failure mode (unheld_symbol, stale_workspace,
            stale_index_only_id): {{context.Doc("validate_patch")}}

            If this genuinely is not a validate_patch case (the workspace failed to load, or you are reverting a
            partial write), say so and ask the user to allow it explicitly rather than retrying the same call.
            """);
    }
}
