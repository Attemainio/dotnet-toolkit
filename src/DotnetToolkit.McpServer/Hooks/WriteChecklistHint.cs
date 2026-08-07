using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// Delivers the write-time checklist on the first <c>validate_patch</c> of a session.
/// </summary>
/// <remarks>
/// The other guards are retrospective: they fire only once <c>Edit</c>/<c>Write</c>/<c>Bash</c> has
/// already been reached for. A caller that goes straight to <c>validate_patch</c> without invoking
/// the <c>dotnet-write</c> skill trips none of them, so the checklist that skill carries never
/// arrives. Firing here puts it in front of the caller at the moment it applies, which is also why
/// the skill does not need to be pre-loaded to get it.
/// <para>
/// Once per session, not once per call: a checklist repeated on every patch of a long editing task is
/// noise, and noise is ignored. Dedupe state is a marker file under the OS temp directory keyed by
/// session id — deliberately not inside the repo, which should not accumulate per-session files.
/// </para>
/// </remarks>
internal static class WriteChecklistHint
{
    private const string MarkerDirectory = "dotnet-toolkit-hooks";

    private const string Checklist =
        "First validate_patch of this session. Hold these - they are the items most expensive to catch "
        + "late, and no analyzer checks them:\n"
        + "  - No credential-shaped literal in source; configuration comes from IConfiguration, the "
        + "environment, or a secret store.\n"
        + "  - No string-concatenated or interpolated SQL in a raw-SQL call - parameterize.\n"
        + "  - Every controller/endpoint carries an explicit [Authorize] or [AllowAnonymous], never an "
        + "unmarked one relying on the global default.\n"
        + "  - New tests exercise real dependencies, not an in-memory database substitute, wherever they "
        + "assert constraint, transaction, or query-translation behavior.\n"
        + "  - A body edit needs a contentVersion from a get_symbol that actually served the body "
        + "(include: \"all\"); the default include leases the declaration only.\n"
        + "  - intent is required to apply, and is the only record of WHY - the diff already says what.\n"
        + "Full procedure, the standards step, and every failure mode: invoke the dotnet-write skill.";

    /// <summary>Emits the checklist once per session, and allows silently every other time.</summary>
    /// <param name="payload">The parsed hook stdin payload.</param>
    /// <returns>
    /// The checklist as non-blocking <c>additionalContext</c> on the session's first patch, otherwise
    /// <see cref="HookOutcome.Allow"/>. Never denies: this is a reminder, not a guard.
    /// </returns>
    public static HookOutcome Evaluate(HookPayload payload)
    {
        // No session id means no way to tell a first call from a fiftieth. Emitting anyway would
        // repeat the checklist on every patch of the session, so stay silent and let the always-loaded
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
        return $"{safe}.patched";
    }
}
