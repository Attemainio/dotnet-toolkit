using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// Delivers the write-time checklist on the first edit of a compiled <c>.cs</c> file in a session.
/// </summary>
/// <remarks>
/// A caller that goes straight to <c>Edit</c> without invoking the <c>dotnet-write</c> skill never
/// sees the checklist that skill carries. Firing here — <c>PreToolUse</c> on the native edit tools,
/// gated on the file being one a project compiles — puts it in front of the caller at the moment it
/// applies, which is also why the skill does not need to be pre-loaded to get it. It never denies:
/// <c>Edit</c> and <c>Write</c> are the write path for C#.
/// <para>
/// Once per session, not once per edit: a checklist repeated on every edit of a long refactor is
/// noise, and noise is ignored. Dedupe state is a marker file under the OS temp directory keyed by
/// session id — deliberately not inside the repo, which should not accumulate per-session files.
/// </para>
/// </remarks>
internal static class CsWriteChecklistHint
{
    private const string MarkerDirectory = "dotnet-toolkit-hooks";

    private const string Checklist =
        "First C# edit of this session. Hold these - they are the items most expensive to catch late, "
        + "and no analyzer checks them:\n"
        + "  - No credential-shaped literal in source; configuration comes from IConfiguration, the "
        + "environment, or a secret store.\n"
        + "  - No string-concatenated or interpolated SQL in a raw-SQL call - parameterize.\n"
        + "  - Every controller/endpoint carries an explicit [Authorize] or [AllowAnonymous], never an "
        + "unmarked one relying on the global default.\n"
        + "  - New tests exercise real dependencies, not an in-memory database substitute, wherever they "
        + "assert constraint, transaction, or query-translation behavior.\n"
        + "  - Before changing a signature, get_references/get_call_hierarchy for the blast radius; "
        + "after a burst of edits, dotnet build is the gate - nothing here compiles for you.\n"
        + "Full procedure, the standards step, and every failure mode: invoke the dotnet-write skill.";

    /// <summary>Emits the checklist once per session, and allows silently every other time.</summary>
    /// <param name="payload">The parsed hook stdin payload.</param>
    /// <param name="context">The repo the membership check is scoped to.</param>
    /// <returns>
    /// The checklist as non-blocking <c>additionalContext</c> on the session's first edit of a compiled
    /// <c>.cs</c> file, otherwise <see cref="HookOutcome.Allow"/>. Never denies: this is a reminder.
    /// </returns>
    public static HookOutcome Evaluate(HookPayload payload, HookContext context)
    {
        if (!TouchesCompiledCs(payload, context))
        {
            return HookOutcome.Allow;
        }

        // No session id means no way to tell a first call from a fiftieth. Emitting anyway would
        // repeat the checklist on every edit of the session, so stay silent and let the always-loaded
        // rule and the skill carry it instead.
        if (payload.SessionId is not { Length: > 0 } sessionId || !TryClaimFirstCall(sessionId))
        {
            return HookOutcome.Allow;
        }

        var output = new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["additionalContext"] = Checklist,
            },
        };

        return HookOutcome.AllowWith(output.ToJsonString(JsonSerializerOptions.Default));
    }

    /// <summary>Whether the call writes a <c>.cs</c> file some project of this repo compiles.</summary>
    /// <remarks>
    /// A brand-new file has no owning project yet in the static check, and a test fixture's throwaway
    /// solution is deliberately outside it; neither is the C# the checklist is about.
    /// </remarks>
    private static bool TouchesCompiledCs(HookPayload payload, HookContext context)
    {
        IEnumerable<string> files = payload.ToolName == "apply_patch"
            ? ApplyPatchCommand.Parse(payload.Command).Select(change => change.Path)
            : payload.FilePath is { Length: > 0 } path ? [path] : [];

        foreach (var file in files)
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var absolute = Path.GetFullPath(Path.Combine(context.WorkingDirectory, file));
            if (File.Exists(absolute) && CsFileMembership.TryResolveOwningProject(absolute, context.Root, out _))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Creates this session's marker, reporting whether this call is the one that created it.
    /// </summary>
    /// <param name="sessionId">The session id, used as the marker's name.</param>
    /// <returns>True on the first call of the session; false afterwards, or on any IO failure.</returns>
    private static bool TryClaimFirstCall(string sessionId)
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), MarkerDirectory);
            Directory.CreateDirectory(directory);

            // CreateNew is the claim: it throws for the second caller rather than racing, so two
            // concurrent agents on one session still produce exactly one checklist between them.
            using var marker = new FileStream(
                Path.Combine(directory, SanitizeFileName(sessionId)),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            return true;
        }
        catch (Exception)
        {
            // An existing marker, an unwritable temp directory, or a session id that survived
            // sanitizing as an unusable name all mean the same thing here: say nothing.
            return false;
        }
    }

    /// <summary>Reduces a session id to characters that are safe in a file name on every platform.</summary>
    /// <param name="sessionId">The raw session id from the payload.</param>
    /// <returns>The sanitized name, capped so an absurd id cannot overrun the path limit.</returns>
    private static string SanitizeFileName(string sessionId)
    {
        const int MaxLength = 64;
        var safe = new string([.. sessionId
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            .Take(MaxLength)]);
        return $"{safe}.cs-checklist";
    }
}
