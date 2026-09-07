using System.Text.RegularExpressions;

namespace DotnetToolkit.McpServer.Diagnostics;

/// <summary>
/// Translates a filesystem path between the Windows and Unix namespaces, and reports which namespace a
/// path is written in.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists because the agent host's OS and the OS its tool processes run in are not the
/// same question.</strong> A Windows desktop agent can execute local processes inside WSL2 while
/// keeping a Windows-owned profile, so the profile stores <c>C:\clone\dist\server.dll</c> while the
/// process that must open it is Linux — where that string is not a path at all, but a relative
/// filename containing backslashes. Every inventory command still reports the server as configured and
/// enabled, because configuration is all they read; only the launch fails, with a message naming a file
/// "that does not exist". Comparing a stored path against the running process's own namespace is the
/// only check that catches it, and it can only be made somewhere that knows both.
/// </para>
/// <para>
/// The Unix half of the mapping is WSL's default <c>DrvFs</c> convention (<c>C:\x</c> ↔ <c>/mnt/c/x</c>)
/// rather than a general rule. A path on a Linux-native filesystem has no Windows spelling at all, and
/// a WSL install with a custom <c>automount.root</c> spells drives differently; both cases return null
/// here instead of an invented answer, since a confidently wrong path is worse to act on than an
/// admitted unknown.
/// </para>
/// </remarks>
internal static partial class PathNamespace
{
    /// <summary>The mount point WSL exposes Windows drives under by default.</summary>
    private const string DriveMountRoot = "/mnt/";

    /// <summary>Matches a path rooted at a drive letter, with or without the <c>\\?\</c> long-path prefix.</summary>
    [GeneratedRegex(@"^(?:\\\\\?\\)?(?<drive>[A-Za-z]):[\\/]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DriveRooted();

    /// <summary>Matches a Unix path sitting under WSL's default drive mount root.</summary>
    [GeneratedRegex(@"^/mnt/(?<drive>[A-Za-z])(?:/|$)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DriveMounted();

    /// <summary>Is this path written in the Windows namespace?</summary>
    /// <param name="path">The path to classify.</param>
    /// <returns>
    /// True for a drive-rooted path (<c>C:\x</c>, <c>C:/x</c>, <c>\\?\C:\x</c>) or a UNC path
    /// (<c>\\server\share</c>). A relative path is false: it is namespace-neutral, and a diagnostic that
    /// called it Windows-only would flag every ordinary relative argument.
    /// </returns>
    public static bool IsWindows(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && (DriveRooted().IsMatch(path) || path.StartsWith(@"\\", StringComparison.Ordinal));

    /// <summary>Is this path written in the Unix namespace?</summary>
    /// <param name="path">The path to classify.</param>
    /// <returns>True for an absolute Unix path; false for a relative one, and for anything Windows-rooted.</returns>
    public static bool IsUnix(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && path[0] == '/'
        && !path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>Rewrites a Windows path as the WSL path naming the same file.</summary>
    /// <param name="path">A drive-rooted Windows path.</param>
    /// <returns>
    /// The <c>/mnt/&lt;drive&gt;/...</c> spelling, or null when <paramref name="path"/> is not
    /// drive-rooted — a UNC share has no default mount point, so there is nothing to return.
    /// </returns>
    public static string? ToUnix(string path)
    {
        var match = DriveRooted().Match(path);
        if (!match.Success)
        {
            return null;
        }

        var drive = char.ToLowerInvariant(match.Groups["drive"].Value[0]);
        var remainder = path[match.Length..].Replace('\\', '/');
        return $"{DriveMountRoot}{drive}/{remainder}";
    }

    /// <summary>Rewrites a WSL path as the Windows path naming the same file.</summary>
    /// <param name="path">A Unix path under WSL's drive mount root.</param>
    /// <returns>
    /// The <c>&lt;DRIVE&gt;:\...</c> spelling, or null when the path is not under a mounted drive — a
    /// Linux-native path such as <c>/home/user/src</c> genuinely has no Windows equivalent.
    /// </returns>
    public static string? ToWindows(string path)
    {
        var match = DriveMounted().Match(path);
        if (!match.Success)
        {
            return null;
        }

        var drive = char.ToUpperInvariant(match.Groups["drive"].Value[0]);
        var remainder = path[match.Length..].Replace('/', '\\');
        return $@"{drive}:\{remainder}";
    }

    /// <summary>Rewrites a path into the namespace this process can actually open files in.</summary>
    /// <param name="path">The path as some configuration file stores it.</param>
    /// <returns>
    /// The path spelled for the running OS, <paramref name="path"/> unchanged when it is already in that
    /// namespace, or null when no translation exists.
    /// </returns>
    /// <remarks>
    /// The repair a diagnostic can offer, rather than only the complaint. A user told that a stored path
    /// is unopenable still has to work out what to store instead, and the drive-letter arithmetic is
    /// exactly the step people get wrong by hand.
    /// </remarks>
    public static string? Localize(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return IsWindows(path) ? path : ToWindows(path);
        }

        return IsUnix(path) ? path : ToUnix(path);
    }

    /// <summary>Could this process open a file at this path, judged on spelling alone?</summary>
    /// <param name="path">The path to judge.</param>
    /// <returns>
    /// False only when the path is definitely written for the other OS. A relative path returns true,
    /// because it is legal everywhere and this method judges namespace, not existence.
    /// </returns>
    public static bool IsUsableHere(string path) =>
        OperatingSystem.IsWindows() ? !IsUnix(path) : !IsWindows(path);
}
