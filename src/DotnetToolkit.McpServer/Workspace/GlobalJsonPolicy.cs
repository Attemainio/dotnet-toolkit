using System.Text.Json;

namespace DotnetToolkit.McpServer.Workspace;

/// <summary>How far a <c>global.json</c> lets SDK selection move from the version it names.</summary>
/// <remarks>
/// The names and semantics are the .NET SDK host's, not ours. Half of them pick the <em>lowest</em>
/// qualifying SDK and half the highest, which is the distinction
/// <see cref="GlobalJsonPolicy.Select"/> exists to keep.
/// </remarks>
internal enum RollForward
{
    /// <summary>The named version exactly, or nothing.</summary>
    Disable,

    /// <summary>The lowest patch at or above the named one, in the same feature band.</summary>
    Patch,

    /// <summary>The lowest at or above the named version, in the same major.minor.</summary>
    Feature,

    /// <summary>The lowest at or above the named version, in the same major.</summary>
    Minor,

    /// <summary>The lowest at or above the named version.</summary>
    Major,

    /// <summary>The highest patch in the named feature band. The default when nothing is stated.</summary>
    LatestPatch,

    /// <summary>The highest at or above the named version, in the same major.minor.</summary>
    LatestFeature,

    /// <summary>The highest at or above the named version, in the same major.</summary>
    LatestMinor,

    /// <summary>The highest installed at or above the named version.</summary>
    LatestMajor,
}

/// <summary>The SDK a repository's <c>global.json</c> asks to be built with.</summary>
/// <remarks>
/// <para>
/// <strong>This is the mechanism the .NET SDK already provides for exactly this conflict, and the
/// server used to bypass it.</strong> Registration picked the highest-versioned SDK on the machine,
/// which is a machine-global answer to a per-repository question: a repository pinned to the 10.0.1xx
/// band had its workspace loaded by a 10.0.3xx MSBuild, and — worse — a developer's own
/// <c>dotnet build</c> resolved a different SDK than the server's restore, so whichever ran last owned
/// the NuGet assets cache and the other reported the workspace degraded.
/// </para>
/// <para>
/// <strong>An absent <c>global.json</c> is not a constraint and must not become one.</strong> Most
/// repositories have none, and for those the previous behaviour — take the newest MSBuild available —
/// is right, because a newer MSBuild opens an older project fine and the reverse is not true.
/// </para>
/// </remarks>
/// <param name="Requested">The version <c>global.json</c> names.</param>
/// <param name="Mode">How far selection may move from it.</param>
/// <param name="Path">The file this came from, for the startup line.</param>
internal sealed record GlobalJsonPolicy(Version Requested, RollForward Mode, string Path)
{
    /// <summary>Reads the <c>global.json</c> governing a directory, if there is one.</summary>
    /// <param name="root">The directory to start from — a repository root.</param>
    /// <returns>
    /// The policy, or <see langword="null"/> when no <c>global.json</c> governs the directory, when it
    /// names no SDK version, or when it cannot be read. Every one of those means "no constraint",
    /// which is the same answer the SDK host gives.
    /// </returns>
    /// <remarks>
    /// Walks upward, because that is how the SDK host finds it: a <c>global.json</c> beside the
    /// solution and one two directories above it are both in force, nearest winning.
    /// </remarks>
    public static GlobalJsonPolicy? Read(string root)
    {
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
        {
            var path = System.IO.Path.Combine(directory.FullName, "global.json");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(
                    File.ReadAllText(path),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

                if (!document.RootElement.TryGetProperty("sdk", out var sdk)
                    || !sdk.TryGetProperty("version", out var version)
                    || !Version.TryParse(version.GetString(), out var requested))
                {
                    return null;
                }

                var mode = sdk.TryGetProperty("rollForward", out var rollForward)
                    ? Parse(rollForward.GetString())
                    : RollForward.LatestPatch;

                return new GlobalJsonPolicy(requested, mode, path);
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // A malformed or unreadable global.json is not this server's problem to report: the
                // repository's own build will say so far more clearly. Treat it as no constraint.
                return null;
            }
        }

        return null;
    }

    /// <summary>Picks the SDK this policy would have the repository built with.</summary>
    /// <param name="installed">Every SDK version discovered on the machine.</param>
    /// <returns>The chosen version, or <see langword="null"/> when none satisfies the policy.</returns>
    /// <remarks>
    /// <strong>Half of the modes want the lowest qualifying SDK and half the highest</strong>, and
    /// collapsing them to "highest allowed" is wrong in a way nothing would notice for months:
    /// <c>rollForward: feature</c> on a machine with three bands installed would silently build
    /// against the newest rather than the nearest.
    /// </remarks>
    public Version? Select(IEnumerable<Version> installed)
    {
        ArgumentNullException.ThrowIfNull(installed);

        var allowed = installed.Where(Allows).ToList();

        if (allowed.Count == 0)
        {
            return null;
        }

        return Mode is RollForward.LatestPatch or RollForward.LatestFeature
            or RollForward.LatestMinor or RollForward.LatestMajor
            ? allowed.Max()
            : allowed.Min();
    }

    /// <summary>Tells whether an installed SDK satisfies this policy at all.</summary>
    /// <param name="candidate">An installed SDK version.</param>
    /// <returns><see langword="true"/> when the SDK host would accept it.</returns>
    public bool Allows(Version candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (Mode == RollForward.Disable)
        {
            return candidate == Requested;
        }

        // Rolling forward never goes backward, whatever the scope: `latestMajor` on a 10.0.100 pin
        // does not license an 8.0 SDK.
        if (candidate < Requested)
        {
            return false;
        }

        return Mode switch
        {
            RollForward.Patch or RollForward.LatestPatch => FeatureBand(candidate) == FeatureBand(Requested),
            RollForward.Feature or RollForward.LatestFeature =>
                candidate.Major == Requested.Major && candidate.Minor == Requested.Minor,
            RollForward.Minor or RollForward.LatestMinor => candidate.Major == Requested.Major,
            _ => true,
        };
    }

    /// <summary>The feature band a patch level falls in.</summary>
    /// <param name="version">An SDK version.</param>
    /// <returns>The version with its patch truncated to the hundred, so 10.0.111 becomes 10.0.100.</returns>
    /// <remarks>
    /// The band, not the patch, is what makes two SDKs meaningfully different: 10.0.111 and 10.0.100
    /// are the same servicing line, and 10.0.301 is a different one that ships different MSBuild and
    /// NuGet.
    /// </remarks>
    private static Version FeatureBand(Version version) =>
        new(version.Major, version.Minor, version.Build / 100 * 100);

    private static RollForward Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "disable" => RollForward.Disable,
        "patch" => RollForward.Patch,
        "feature" => RollForward.Feature,
        "minor" => RollForward.Minor,
        "major" => RollForward.Major,
        "latestfeature" => RollForward.LatestFeature,
        "latestminor" => RollForward.LatestMinor,
        "latestmajor" => RollForward.LatestMajor,

        // Both `latestPatch` and anything unrecognised. An unknown value is the SDK host's error to
        // report, and guessing a wider scope here would pick an SDK the repository's own build will
        // not.
        _ => RollForward.LatestPatch,
    };
}
