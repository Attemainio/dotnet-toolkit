using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotnetToolkit.McpServer.Diagnostics;

/// <summary>
/// Records which sources a published <c>dist/</c> was built from, so a binary that has fallen behind the
/// source tree can be detected rather than discovered.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The failure this exists to catch shipped once.</strong> <c>dist/</c> is committed so a
/// consuming machine needs no build step, which makes a stale <c>dist/</c> worse than a stale local
/// build: it reaches every consumer who pulls. One did — the committed binary implemented the old
/// machine-newest SDK selection while the source implemented the <c>global.json</c> policy, both
/// labelled <c>1.0.1</c>, and the resulting "the server picks the wrong SDK" report could not be
/// attributed to either without disassembling the binary.
/// </para>
/// <para>
/// <strong>Comparing rebuilt binaries byte for byte cannot do this job.</strong> A rebuild produces a
/// fresh module version id, and embeds paths from whatever machine ran it, so two builds of identical
/// source differ — a gate on binary equality would fail on every correct commit. Hashing the *inputs*
/// is the comparison that means what it needs to mean: it is stable across machines, survives a git
/// checkout that rewrites no content, and answers exactly the question "was this binary produced from
/// this source".
/// </para>
/// </remarks>
internal static class SourceStamp
{
    /// <summary>The stamp file written beside the published server.</summary>
    public const string FileName = "build-manifest.json";

    /// <summary>Filename patterns whose content changes what the published binary does.</summary>
    /// <remarks>
    /// Deliberately narrow. Markdown, skills and agent definitions ship as files rather than being
    /// compiled in, so including them would report <c>dist/</c> stale after a doc edit that changed no
    /// binary — a gate that cries wolf gets republished past rather than obeyed.
    /// </remarks>
    private static readonly string[] CompiledPatterns = ["*.cs", "*.csproj", "*.props", "*.targets"];

    /// <summary>Directories under the source tree whose contents are build output, not build input.</summary>
    private static readonly string[] ExcludedDirectories = ["bin", "obj"];

    /// <summary>Hashes every source file that determines what a build produces.</summary>
    /// <param name="repoRoot">The repository root holding <c>src/</c>.</param>
    /// <returns>
    /// A hex digest over the sorted set of (relative path, content hash) pairs, or null when the
    /// repository has no <c>src/</c> directory to hash.
    /// </returns>
    /// <remarks>
    /// Paths are included in the digest and normalized to forward slashes, so a renamed file changes the
    /// stamp even when its content did not, and a Windows build and a Linux build of one tree agree.
    /// </remarks>
    public static string? Compute(string repoRoot)
    {
        var sourceRoot = Path.Combine(repoRoot, "src");
        if (!Directory.Exists(sourceRoot))
        {
            return null;
        }

        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var pattern in CompiledPatterns)
        {
            foreach (var file in Directory.EnumerateFiles(sourceRoot, pattern, SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                if (relative.Split('/').Any(segment => ExcludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
                {
                    continue;
                }

                entries[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            }
        }

        // The repo-wide build properties sit above src/ but change every assembly the build produces.
        foreach (var name in new[] { "Directory.Build.props", "Directory.Packages.props" })
        {
            var path = Path.Combine(repoRoot, name);
            if (File.Exists(path))
            {
                entries[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            }
        }

        var digest = new StringBuilder();
        foreach (var (path, hash) in entries)
        {
            digest.Append(path).Append('\n').Append(hash).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digest.ToString())));
    }

    /// <summary>Reads the stamp a published <c>dist/</c> carries.</summary>
    /// <param name="pluginRoot">The plugin's installation directory.</param>
    /// <returns>The recorded source digest, or null when the publish left no stamp or it is unreadable.</returns>
    public static string? Read(string pluginRoot)
    {
        var path = Path.Combine(pluginRoot, "dist", FileName);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("sourceHash", out var hash) ? hash.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes the stamp for the sources a just-completed publish was built from.</summary>
    /// <param name="repoRoot">The repository root that was published.</param>
    /// <returns>An exit code: zero once written, nonzero when there was nothing to hash.</returns>
    /// <remarks>
    /// Run by the publish scripts against the binary they just produced, so the stamp is written by the
    /// same build it describes. Writing it from the source tree instead would let a stamp be refreshed
    /// without a republish, which is the exact drift it exists to detect.
    /// </remarks>
    public static int Write(string repoRoot)
    {
        if (Compute(repoRoot) is not { } hash)
        {
            Console.Error.WriteLine($"stamp: no src/ directory under {repoRoot}");
            return 1;
        }

        var target = Path.Combine(repoRoot, "dist", FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(
            target,
            JsonSerializer.Serialize(
                new
                {
                    sourceHash = hash,
                    version = BuildIdentity.Version,
                    build = BuildIdentity.Build,
                },
                new JsonSerializerOptions { WriteIndented = true }) + "\n");

        Console.Out.WriteLine($"stamp: dist/{FileName} written for source {hash[..12]}");
        return 0;
    }
}
