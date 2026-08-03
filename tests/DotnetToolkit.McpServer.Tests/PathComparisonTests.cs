using DotnetToolkit.McpServer.Workspace;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Locks the contract of <see cref="PathComparison"/>, which decides what "the same file" means
/// everywhere a path is compared or used as a dictionary key.
/// </summary>
/// <remarks>
/// The case-insensitive branch cannot be *executed* on Linux, where this repo's tests run — the whole
/// point is that it follows the host filesystem. So these assert the two properties that hold on every
/// platform: the three members never disagree with each other, and the choice tracks the OS rather than
/// a hardcoded comparer. That is what catches the real regression, which is one call site quietly
/// reverting to <see cref="StringComparer.Ordinal"/> while the others move on.
/// </remarks>
public sealed class PathComparisonTests
{
    private const string Upper = "/repo/src/Foo.cs";
    private const string Lower = "/repo/src/foo.cs";

    [Fact]
    public void Comparison_TracksTheHostFilesystem() =>
        Assert.Equal(
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase,
            PathComparison.Comparison);

    [Fact]
    public void Comparer_AgreesWithComparison_OnCaseVariants() =>
        Assert.Equal(string.Equals(Upper, Lower, PathComparison.Comparison), PathComparison.Comparer.Equals(Upper, Lower));

    [Fact]
    public void Equal_AgreesWithComparer_OnCaseVariants() =>
        Assert.Equal(PathComparison.Comparer.Equals(Upper, Lower), PathComparison.Equal(Upper, Lower));

    [Fact]
    public void Comparer_HashesEqualPathsAlike()
    {
        // A dictionary keyed by this comparer is only correct if equality and hashing agree; a comparer
        // assembled from mismatched halves passes an equality test and still misses lookups.
        if (PathComparison.Comparer.Equals(Upper, Lower))
        {
            Assert.Equal(PathComparison.Comparer.GetHashCode(Upper), PathComparison.Comparer.GetHashCode(Lower));
        }

        Assert.Equal(PathComparison.Comparer.GetHashCode(Upper), PathComparison.Comparer.GetHashCode(Upper));
    }

    [Fact]
    public void Equal_IdenticalPaths_AlwaysMatch() =>
        Assert.True(PathComparison.Equal(Upper, Upper));

    [Fact]
    public void Equal_DifferentPaths_NeverMatch() =>
        Assert.False(PathComparison.Equal(Upper, "/repo/src/Bar.cs"));

    [Fact]
    public void Equal_TrailingSeparator_IsIgnored()
    {
        var directory = Path.Combine("repo", "src");

        Assert.True(PathComparison.Equal(directory, directory + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Comparer_UsedAsDictionaryKey_CollapsesExactlyTheVariantsEqualAccepts()
    {
        // The PatchSandbox bug in one line: two spellings of one file becoming two entries.
        var byPath = new Dictionary<string, int>(PathComparison.Comparer) { [Upper] = 1 };
        byPath[Lower] = 2;

        Assert.Equal(PathComparison.Equal(Upper, Lower) ? 1 : 2, byPath.Count);
    }
}
