using System.Globalization;

namespace DotnetToolkit.McpServer.Hooks;

/// <summary>What the guard hooks are currently doing, and why.</summary>
/// <param name="Suspended">Whether the C# guards are letting Read/Edit/Bash calls through.</param>
/// <param name="Until">
/// When a timed suspension lapses. Null when the guards are active, and null for an environment
/// suspension, which is bounded by the process rather than by a clock.
/// </param>
/// <param name="Source">
/// <c>active</c>, <c>environment</c> (<see cref="GuardSuspension.DisableVariable"/> is set), or
/// <c>timed</c> (a state file that has not lapsed yet).
/// </param>
internal sealed record GuardState(bool Suspended, DateTimeOffset? Until, string Source);

/// <summary>
/// Whether this repo's C# guard hooks are suspended, and for how much longer.
/// </summary>
/// <remarks>
/// The guards exist so C# work goes through the MCP tools instead of Read/Edit/grep. Turning them off
/// is sometimes the point rather than a workaround — measuring what the tools actually save needs the
/// unguarded baseline, in the same repo, on the same files — but a guard left off because nobody
/// remembered to restore it is indistinguishable from one that was never installed, and it fails
/// silently: work simply stops being recorded.
/// <para>
/// So a suspension is stored as an <b>expiry, never a flag</b>. The state file holds the instant the
/// guards resume, every read that finds that instant in the past deletes the file, and
/// <see cref="MaxDuration"/> bounds what a caller can ask for. There is deliberately no way to suspend
/// indefinitely through this path, so nothing has to remember to undo it.
/// </para>
/// <para>
/// <see cref="DisableVariable"/> is the separate, non-expiring escape hatch. It is for a harness that
/// owns the whole process lifetime — CI, a benchmark runner — where "until this process exits" is
/// already a real bound, and where no interactive session is around to be surprised by it.
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

    /// <summary>How long a suspension lasts when the caller names no duration.</summary>
    public static TimeSpan DefaultDuration { get; } = TimeSpan.FromMinutes(30);

    /// <summary>The longest suspension this accepts, so a mistyped duration cannot disarm the guards for a week.</summary>
    public static TimeSpan MaxDuration { get; } = TimeSpan.FromHours(4);

    private const string FileName = "guards-suspended-until";

    /// <summary>Path of the file holding the suspension expiry for a repo.</summary>
    /// <param name="root">The repo root the guards apply to.</param>
    /// <returns>An absolute path inside the toolkit's cache directory, which is git-ignored.</returns>
    public static string StateFile(string root) =>
        Path.Combine(root, ".claude", "dotnet-toolkit", "cache", FileName);

    /// <summary>Reads the current guard state, clearing the state file if its expiry has passed.</summary>
    /// <param name="root">The repo root the guards apply to.</param>
    /// <param name="now">The instant to judge the expiry against.</param>
    /// <returns>The state, which is always <c>active</c> when anything about the file is unreadable.</returns>
    public static GuardState Current(string root, DateTimeOffset now)
    {
        if (IsSet(Environment.GetEnvironmentVariable(DisableVariable)))
        {
            return new GuardState(Suspended: true, Until: null, Source: "environment");
        }

        var file = StateFile(root);
        try
        {
            if (!File.Exists(file))
            {
                return Active;
            }

            var raw = File.ReadAllText(file).Trim();
            if (!DateTimeOffset.TryParse(raw, null, DateTimeStyles.RoundtripKind, out var until))
            {
                // Unreadable content is not a licence to stay unguarded: drop it and re-arm.
                Delete(file);
                return Active;
            }

            if (until <= now)
            {
                Delete(file);
                return Active;
            }

            return new GuardState(Suspended: true, Until: until, Source: "timed");
        }
        catch (Exception)
        {
            // An unreadable cache directory means the guards stay on, which is the safe direction.
            return Active;
        }
    }

    /// <summary>Suspends the guards for a bounded period, replacing any suspension already in force.</summary>
    /// <param name="root">The repo root the guards apply to.</param>
    /// <param name="duration">
    /// How long to suspend for. Clamped into <c>(0, <see cref="MaxDuration"/>]</c>; a non-positive value
    /// becomes <see cref="DefaultDuration"/> rather than an instant no-op, since a caller asking for zero
    /// meant to ask for something.
    /// </param>
    /// <param name="now">The instant to measure <paramref name="duration"/> from.</param>
    /// <returns>When the guards will resume.</returns>
    public static DateTimeOffset Suspend(string root, TimeSpan duration, DateTimeOffset now)
    {
        var bounded = duration <= TimeSpan.Zero ? DefaultDuration
            : duration > MaxDuration ? MaxDuration
            : duration;

        var until = now + bounded;
        var file = StateFile(root);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, until.ToString("O"));
        return until;
    }

    /// <summary>Restores the guards immediately, whether or not a suspension was in force.</summary>
    /// <param name="root">The repo root the guards apply to.</param>
    /// <returns>
    /// False when <see cref="DisableVariable"/> is set, since this cannot clear an environment variable
    /// out of a process it does not own — the caller has to say so rather than report a restore it did
    /// not perform.
    /// </returns>
    public static bool Resume(string root)
    {
        Delete(StateFile(root));
        return !IsSet(Environment.GetEnvironmentVariable(DisableVariable));
    }

    private static GuardState Active { get; } = new(Suspended: false, Until: null, Source: "active");

    private static bool IsSet(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.Equals(value, "0", StringComparison.Ordinal)
        && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

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
}
