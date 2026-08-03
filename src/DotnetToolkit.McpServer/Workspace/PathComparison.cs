namespace DotnetToolkit.McpServer.Workspace;

/// <summary>
/// How this process compares file paths: by the rules of the filesystem it is running on.
/// </summary>
/// <remarks>
/// Windows and macOS default to case-insensitive filesystems; Linux does not. Comparing paths
/// ordinally everywhere means two spellings of one file — <c>src/Foo.cs</c> and <c>src/foo.cs</c>, or a
/// drive letter that arrived as <c>c:</c> rather than <c>C:</c> — are treated as two different files on
/// Windows, which is how a single edit turns into a confusing failure rather than an edit.
/// <para>
/// One definition rather than a judgment call per call site: the failure only appears on the platforms
/// this repo's own test runs do not cover, so a site that quietly kept <see cref="StringComparer.Ordinal"/>
/// would stay wrong indefinitely.
/// </para>
/// <para>
/// This is deliberately *not* path canonicalization. It does not resolve symlinks, <c>..</c> segments,
/// or 8.3 short names — callers still normalize with <see cref="Path.GetFullPath(string)"/> first. It
/// only decides whether two already-normalized paths name the same file.
/// </para>
/// </remarks>
internal static class PathComparison
{
    /// <summary>The comparison to use for path equality and prefix tests.</summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>The comparer to key a path-indexed dictionary, set, or grouping by.</summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>Whether two paths name the same file, ignoring a trailing separator.</summary>
    /// <param name="left">First path, already normalized.</param>
    /// <param name="right">Second path, already normalized.</param>
    /// <returns>True when both name the same location under the filesystem's own case rules.</returns>
    public static bool Equal(string left, string right) =>
        string.Equals(TrimTrailingSeparator(left), TrimTrailingSeparator(right), Comparison);

    private static string TrimTrailingSeparator(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
