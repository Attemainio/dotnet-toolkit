using System.Text.RegularExpressions;
using DotnetToolkit.McpServer.Workspace;

namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// Decides, from the filesystem alone, whether a <c>.cs</c> file is compiled by a project this repo's
/// own solution would load — the question both read guards ask before blocking.
/// </summary>
/// <remarks>
/// A hook is a separate, short-lived process with no access to the MCP stdio pipe, so it cannot ask the
/// running server's <c>WorkspaceHost</c> whether a file is part of the loaded solution; that question
/// only has an answer inside the running process. What is checkable statically: whether a
/// <c>.csproj</c> governs the file's directory at all, whether that project's own
/// <c>&lt;Compile Remove&gt;</c> globs exclude it anyway, and whether a nested <c>.sln</c>/<c>.slnx</c>
/// between the file and the repo root means it belongs to its own independent solution (a test
/// fixture's throwaway sample project) rather than the one this repo's server loads.
/// <para>
/// One implementation shared by <see cref="GuardCsRead"/> and <see cref="GuardCsBashRead"/>: they ask
/// the same question from two tool surfaces and must never drift on the answer.
/// </para>
/// </remarks>
internal static partial class CsFileMembership
{
    /// <summary>Finds the project that compiles a file, if this repo's own solution compiles it at all.</summary>
    /// <param name="absoluteFile">Absolute path of the <c>.cs</c> file.</param>
    /// <param name="root">Absolute path of the repo root the guard is scoped to.</param>
    /// <param name="relativeCsproj">
    /// On success, the owning project's path relative to <paramref name="root"/>, forward-slashed for
    /// quoting in a denial message.
    /// </param>
    /// <returns>
    /// True when the file is governed by a project this repo's solution would load — the block-worthy
    /// case. False for anything else, including a file outside <paramref name="root"/> entirely.
    /// </returns>
    public static bool TryResolveOwningProject(string absoluteFile, string root, out string relativeCsproj)
    {
        relativeCsproj = string.Empty;

        // The walk below climbs from the file toward the root. A file that is not under the root at all
        // (another repo, a scratch directory) must never reach it: without this check it climbs past the
        // root to whatever .sln/.csproj the filesystem happens to hold above it, and can report
        // "governed by this repo" for a read this guard has no business touching. Outside the root is
        // unconditionally external.
        if (!IsUnder(absoluteFile, root))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(absoluteFile);
        string? csproj = null;
        while (directory is not null)
        {
            csproj ??= EnumerateFiles(directory, "*.csproj").FirstOrDefault();

            // A nested solution root means an independent solution owns everything below it.
            if (!PathsEqual(directory, root)
                && (EnumerateFiles(directory, "*.slnx").Any() || EnumerateFiles(directory, "*.sln").Any()))
            {
                return false;
            }

            if (PathsEqual(directory, root))
            {
                break;
            }

            var parent = Path.GetDirectoryName(directory);
            if (parent is null || PathsEqual(parent, directory))
            {
                break;   // filesystem root, defensive
            }

            directory = parent;
        }

        if (csproj is null)
        {
            return false;
        }

        if (IsCompileRemoved(absoluteFile, csproj))
        {
            return false;
        }

        relativeCsproj = Relative(root, csproj);
        return true;
    }

    /// <summary>Whether the project's own <c>&lt;Compile Remove&gt;</c> globs exclude the file.</summary>
    /// <param name="absoluteFile">Absolute path of the <c>.cs</c> file.</param>
    /// <param name="csproj">Absolute path of the governing project file.</param>
    /// <returns>True when a glob matches, meaning the project does not compile the file after all.</returns>
    private static bool IsCompileRemoved(string absoluteFile, string csproj)
    {
        string text;
        try
        {
            text = File.ReadAllText(csproj);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        var projectDirectory = Path.GetDirectoryName(csproj);
        if (projectDirectory is null)
        {
            return false;
        }

        var relative = Relative(projectDirectory, absoluteFile);
        foreach (Match match in CompileRemovePattern().Matches(text))
        {
            foreach (var glob in match.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (GlobMatches(glob.Trim().Replace('\\', '/'), relative))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Matches an MSBuild item glob against a project-relative path.</summary>
    /// <param name="glob">The glob, forward-slashed. <c>*</c> and <c>**</c> both span separators.</param>
    /// <param name="relativePath">The project-relative path, forward-slashed.</param>
    /// <returns>True when the glob covers the path.</returns>
    /// <remarks>
    /// <c>*</c> spans <c>/</c> here, matching the shell <c>case</c> semantics the previous
    /// implementation had and MSBuild's own <c>**</c>. Erring toward matching means erring toward
    /// "not governed", which is the allow direction — the safe way for a fail-open guard to be wrong.
    /// </remarks>
    private static bool GlobMatches(string glob, string relativePath)
    {
        if (glob.Length == 0)
        {
            return false;
        }

        var pattern = new System.Text.StringBuilder("^");
        foreach (var c in glob)
        {
            _ = c switch
            {
                '*' => pattern.Append(".*"),
                '?' => pattern.Append('.'),
                _ => pattern.Append(Regex.Escape(c.ToString())),
            };
        }

        pattern.Append('$');
        pattern.Append('$');
        var options = RegexOptions.CultureInvariant;
        if (PathComparison.Comparison == StringComparison.OrdinalIgnoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return Regex.IsMatch(relativePath, pattern.ToString(), options, TimeSpan.FromSeconds(1));
    }

    private static IEnumerable<string> EnumerateFiles(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Whether a path sits inside a directory, comparing on a full path-segment boundary.</summary>
    /// <param name="path">The absolute candidate path.</param>
    /// <param name="directory">The absolute directory it must be under.</param>
    /// <returns>True when <paramref name="path"/> is strictly inside <paramref name="directory"/>.</returns>
    private static bool IsUnder(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith("../", StringComparison.Ordinal);
    }

    /// <summary>Compares two paths under the filesystem's own case rules.</summary>
    /// <param name="left">First path.</param>
    /// <param name="right">Second path.</param>
    /// <returns>True when both name the same location.</returns>
    /// <remarks>
    /// Matters here because the walk's own termination test is <c>directory == root</c>: an ordinal
    /// compare would miss the root on Windows whenever the two disagreed on a drive letter's case, and
    /// climb past it.
    /// </remarks>
    private static bool PathsEqual(string left, string right) => PathComparison.Equal(left, right);

    private static string Relative(string from, string to) =>
        Path.GetRelativePath(from, to).Replace('\\', '/');

    [GeneratedRegex("Compile\\s+Remove=\"([^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex CompileRemovePattern();
}
