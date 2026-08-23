using System.Globalization;

namespace DotnetToolkit.McpServer.Hooks;

/// <summary>Which of the three guard states is currently in force.</summary>
internal enum GuardMode
{
    /// <summary>Normal enforcement: write guards deny, read guards advise.</summary>
    Active,

    /// <summary>Nothing blocks and nothing advises - every guard hook allows the call outright.</summary>
    Suspended,

    /// <summary>Read guards deny too, on top of the write guards that always did.</summary>
    Blocked,
}

/// <summary>What the guard hooks are currently doing, and why.</summary>
/// <param name="Mode">Which of the three states (<see cref="GuardMode"/>) is in force.</param>
/// <param name="Until">
/// When a timed suspension or block lapses. Null when the guards are active, and null for an environment
/// suspension, which is bounded by the process rather than by a clock.
/// </param>
/// <param name="Source">
/// <c>active</c>, <c>environment</c> (<see cref="GuardSuspension.DisableVariable"/> is set), or
/// <c>timed</c> (a state file that has not lapsed yet).
/// </param>
internal sealed record GuardState(GuardMode Mode, DateTimeOffset? Until, string Source)
{
    /// <summary>Whether nothing blocks and nothing advises - the unequipped-session simulation.</summary>
    public bool Suspended => Mode == GuardMode.Suspended;

    /// <summary>Whether the read guards are hard-denying too, on top of the write guards' usual hard deny.</summary>
    public bool Blocked => Mode == GuardMode.Blocked;
}

/// <summary>
/// Whether this repo's C# guard hooks are suspended or forced to block, and for how much longer.
/// </summary>
/// <remarks>
/// The guards exist so C# work goes through the MCP tools instead of Read/Edit/grep. Turning them off
/// (<see cref="GuardMode.Suspended"/>) is sometimes the point rather than a workaround — measuring what
/// the tools actually save needs the unguarded baseline, in the same repo, on the same files. Forcing
/// them harder (<see cref="GuardMode.Blocked"/>) is the opposite need: reproducing this repo's own
/// pre-2026-08-20 read enforcement as a forced-enforcement comparison point, not a default anyone should
/// ship. Either one left in force because nobody remembered to restore it is a real failure mode, and it
/// fails silently in different ways: a stuck suspension stops work from being recorded; a stuck block
/// makes an ordinary `Read` start denying with no explanation.
/// <para>
/// So a suspend or a block is stored as an <b>expiry, never a flag</b>. The state file holds which mode
/// is in force and the instant the guards resume, every read that finds that instant in the past deletes
/// the file, and <see cref="MaxDuration"/> bounds what a caller can ask for. There is deliberately no way
/// to suspend or block indefinitely through this path, so nothing has to remember to undo it.
/// </para>
/// <para>
/// <see cref="DisableVariable"/> is the separate, non-expiring escape hatch. It is for a harness that
/// owns the whole process lifetime — CI, a benchmark runner — where "until this process exits" is
/// already a real bound, and where no interactive session is around to be surprised by it.
/// </para>
/// <para>
/// A suspension scopes to the calling <see cref="SessionVariable"/> Claude Code session when one is
/// present, so a second, unrelated session pointed at this same repo root does not inherit or clobber
/// it. Subagents share their parent's session id rather than getting one of their own — verified by
/// direct observation, not a documented guarantee — so this scopes to the top-level session, not to an
/// individual agent within it. A caller with no session id in its environment falls back to the
/// unscoped, repo-wide file this always used, and every session-scoped check still falls back to that
/// same file, so an older unscoped suspension stays honoured rather than going silently invisible.
/// </para>
/// <para>
/// <b>Which id is authoritative matters more than it looks.</b> This process inherits
/// <see cref="SessionVariable"/> once, at launch, and keeps it for life; a hook process is spawned per
/// tool call and always sees the current one. Resuming a session therefore leaves the server writing
/// suspensions under an id no hook will ever look up — the tool reports success, workspace_status agrees,
/// and the guards stay armed regardless. <see cref="ObserveSessionId"/> closes that: hooks report their
/// own id on every metered call, and the last one observed outranks this process's environment.
/// </para>
/// <para>
/// Every uncertain path reports the guards ACTIVE: an unreadable or unparseable state file leaves them
/// on. That is the opposite of <see cref="HookCli"/>'s fail-open rule, and deliberately so — there,
/// failing open keeps a broken guard from wedging the user's editing; here, failing open would silently
/// disarm a guard that is working.
/// </para>
/// </remarks>
internal static class GuardSuspension
{
    /// <summary>Environment variable that suspends the guards outright, ignoring the state file.</summary>
    public const string DisableVariable = "DOTNET_TOOLKIT_DISABLE_HOOKS";

    /// <summary>
    /// Environment variable Claude Code sets to the calling session's id. Subagents share their parent
    /// session's value rather than getting a distinct one, so this scopes a suspension to a whole
    /// top-level session, never to one agent within it — see <see cref="CurrentSessionId"/>.
    /// </summary>
    public const string SessionVariable = "CLAUDE_CODE_SESSION_ID";

    /// <summary>How long a suspension lasts when the caller names no duration.</summary>
    public static TimeSpan DefaultDuration { get; } = TimeSpan.FromMinutes(30);

    /// <summary>The longest suspension this accepts, so a mistyped duration cannot disarm the guards for a week.</summary>
    public static TimeSpan MaxDuration { get; } = TimeSpan.FromHours(4);

    private const string FileName = "guards-suspended-until";

    /// <summary>The live session id a hook reported, which outranks this process's inherited one.</summary>
    private static string? _observedSessionId;

    /// <summary>Records the Claude Code session id a hook process reported over the control channel.</summary>
    /// <param name="sessionId">The live id as the hook process sees it; null or blank values are ignored.</param>
    /// <remarks>
    /// This process inherits <see cref="SessionVariable"/> from the session that launched it and holds that
    /// value for its whole life, so a resumed or continued session leaves it stale while every hook process
    /// receives the current one. A suspension written under a stale id is invisible to the guards it was meant
    /// to lift, and fails closed without saying so. Hooks report their own id on every metered call, so the
    /// last one observed outranks this process's environment.
    /// </remarks>
    public static void ObserveSessionId(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            Interlocked.Exchange(ref _observedSessionId, sessionId.Trim());
        }
    }

    /// <summary>Whether a hook has reported its id, making <see cref="CurrentSessionId"/> authoritative.</summary>
    /// <value>True once any hook has reported one; false while only this process's own environment is known.</value>
    public static bool SessionIdIsConfirmed => Volatile.Read(ref _observedSessionId) is not null;

    /// <summary>Forgets any hook-reported id, restoring this process's own environment as the source.</summary>
    /// <remarks>
    /// Nothing in production calls this — an observed id only ever becomes more current, never less. It
    /// exists so a test that sets one can put this process-wide state back, the same way the tests around
    /// <see cref="SessionVariable"/> restore the environment variable they set.
    /// </remarks>
    public static void ForgetObservedSessionId() => Interlocked.Exchange(ref _observedSessionId, null);

    /// <summary>The calling Claude Code session's id, or null when unset, blank, or outside Claude Code.</summary>
    /// <returns>The last id a hook reported, else the trimmed <see cref="SessionVariable"/> value, or null.</returns>
    public static string? CurrentSessionId() =>
        Volatile.Read(ref _observedSessionId)
        ?? (Environment.GetEnvironmentVariable(SessionVariable) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null);

    /// <summary>Path of the file holding the suspension expiry for a repo, optionally scoped to a session.</summary>
    /// <param name="root">The repo root the guards apply to.</param>
    /// <param name="sessionId">
    /// A session id from <see cref="CurrentSessionId"/> to scope the file to, or null for the unscoped,
    /// repo-wide file every caller used before session scoping existed.
    /// </param>
    /// <returns>An absolute path inside the toolkit's cache directory, which is git-ignored.</returns>
    public static string StateFile(string root, string? sessionId = null) =>
        Path.Combine(root, ".claude", "dotnet-toolkit", "cache",
            sessionId is null ? FileName : $"{FileName}.{Sanitize(sessionId)}");

    /// <summary>
    /// Reads the current guard state, clearing an expired state file, and checking a session-scoped
    /// suspension before falling back to the unscoped, repo-wide one.
    /// </summary>
    /// <param name="root">The repo root the guards apply to.</param>
    /// <param name="now">The instant to judge the expiry against.</param>
    /// <param name="sessionId">
    /// The caller's session id from <see cref="CurrentSessionId"/>, or null to check only the unscoped
    /// file the way every caller did before session scoping existed.
    /// </param>
    /// <returns>The state, which is always <c>active</c> when anything about the file is unreadable.</returns>
    public static GuardState Current(string root, DateTimeOffset now, string? sessionId = null)
    {
        if (IsSet(Environment.GetEnvironmentVariable(DisableVariable)))
        {
            return new GuardState(GuardMode.Suspended, Until: null, Source: "environment");
        }

        try
        {
            if (sessionId is not null && TryRead(StateFile(root, sessionId), now, out var scoped))
            {
                return scoped;
            }

            return TryRead(StateFile(root), now, out var global) ? global : Active;
        }
        catch (Exception)
        {
            // An unreadable cache directory means the guards stay on, which is the safe direction.
            return Active;
        }
    }

    /// <summary>
    /// Sets a timed guard mode (<see cref="GuardMode.Suspended"/> or <see cref="GuardMode.Blocked"/>) for a
    /// bounded period, replacing any mode already in force for the same scope.
    /// </summary>
    /// <param name="root">The repo root the guards apply to.</param>
    /// <param name="duration">
    /// How long the mode lasts. Clamped into <c>(0, <see cref="MaxDuration"/>]</c>; a non-positive value
    /// becomes <see cref="DefaultDuration"/> rather than an instant no-op, since a caller asking for zero
    /// meant to ask for something.
    /// </param>
    /// <param name="now">The instant to measure <paramref name="duration"/> from.</param>
    /// <param name="sessionId">
    /// The caller's session id from <see cref="CurrentSessionId"/>, or null to write the unscoped,
    /// repo-wide file the way every caller did before session scoping existed.
    /// </param>
    /// <param name="mode">
    /// <see cref="GuardMode.Suspended"/> (the default) or <see cref="GuardMode.Blocked"/>.
    /// <see cref="GuardMode.Active"/> is never a valid argument here - it is the state <see cref="Resume"/>
    /// returns to, not one this writes a file for.
    /// </param>
    /// <returns>When the mode lapses.</returns>
    public static DateTimeOffset SetMode(
        string root, TimeSpan duration, DateTimeOffset now, string? sessionId = null, GuardMode mode = GuardMode.Suspended)
    {
        var bounded = duration <= TimeSpan.Zero ? DefaultDuration
            : duration > MaxDuration ? MaxDuration
            : duration;

        var until = now + bounded;
        var file = StateFile(root, sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, $"{mode}\n{until:O}");
        return until;
    }

    /// <summary>Restores the guards immediately, whether or not a suspension was in force.</summary>
    /// <param name="root">The repo root the guards apply to.</param>
    /// <param name="sessionId">
    /// The caller's session id from <see cref="CurrentSessionId"/>, or null. Always clears the
    /// unscoped, repo-wide file regardless — that file is the fallback every <see cref="Current"/>
    /// check still honours, so a stray suspension left there (a caller with no session id, or one
    /// written before session scoping existed) must not survive a restore just because this restore
    /// happens to carry a session id. Also clears the matching scoped file when one is given.
    /// </param>
    /// <returns>
    /// False when <see cref="DisableVariable"/> is set, since this cannot clear an environment variable
    /// out of a process it does not own — the caller has to say so rather than report a restore it did
    /// not perform.
    /// </returns>
    public static bool Resume(string root, string? sessionId = null)
    {
        if (sessionId is not null)
        {
            Delete(StateFile(root, sessionId));
        }

        Delete(StateFile(root));
        return !IsSet(Environment.GetEnvironmentVariable(DisableVariable));
    }

    private static GuardState Active { get; } = new(GuardMode.Active, Until: null, Source: "active");

    private static bool IsSet(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.Equals(value, "0", StringComparison.Ordinal)
        && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads one state file's expiry, clearing it if unparseable or already past.</summary>
    /// <param name="file">The state file to read.</param>
    /// <param name="now">The instant to judge the expiry against.</param>
    /// <param name="state">The live suspension state, valid only when this returns true.</param>
    /// <returns>Whether <paramref name="file"/> holds a suspension still in force.</returns>
    private static bool TryRead(string file, DateTimeOffset now, out GuardState state)
    {
        state = Active;
        if (!File.Exists(file))
        {
            return false;
        }

        var raw = File.ReadAllText(file).Trim();

        // The file holds "<mode>\n<until>" since GuardMode.Blocked was added; a bare timestamp is the
        // pre-Blocked format still on disk from a suspension written by an older build of this server -
        // treat that as Suspended rather than fail the read.
        var mode = GuardMode.Suspended;
        var timestamp = raw;
        var split = raw.IndexOf('\n');
        if (split > 0 && Enum.TryParse<GuardMode>(raw[..split].Trim(), out var parsed) && parsed != GuardMode.Active)
        {
            mode = parsed;
            timestamp = raw[(split + 1)..].Trim();
        }

        if (!DateTimeOffset.TryParse(timestamp, null, DateTimeStyles.RoundtripKind, out var until) || until <= now)
        {
            // Unreadable or expired content is not a licence to stay unguarded: drop it and re-arm.
            Delete(file);
            return false;
        }

        state = new GuardState(mode, Until: until, Source: "timed");
        return true;
    }

    private static void Delete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception)
        {
            // A stale file we cannot delete still reports its own expiry, so it re-arms on the next read.
        }
    }

    /// <summary>Strips a session id down to characters safe inside a file name.</summary>
    /// <param name="sessionId">The raw value from <see cref="CurrentSessionId"/>.</param>
    /// <returns>The id with anything but letters, digits, '-' and '_' removed, or "unknown" if that empties it.</returns>
    private static string Sanitize(string sessionId)
    {
        Span<char> buffer = stackalloc char[sessionId.Length];
        var count = 0;
        foreach (var c in sessionId)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                buffer[count++] = c;
            }
        }

        return count > 0 ? new string(buffer[..count]) : "unknown";
    }
}
