using DotnetToolkit.McpServer.Diagnostics;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Covers the namespace translation the installation verifier uses to tell a stored path this process
/// can open from one written for the other OS.
/// </summary>
/// <remarks>
/// The mapping itself is OS-independent and tested as such; only the two methods that answer "here"
/// consult the running platform, and those are asserted against whichever platform the suite is on so
/// the expectation holds on Windows and Linux alike rather than passing on one and being skipped on the
/// other.
/// </remarks>
public sealed class PathNamespaceTests
{
    [Theory]
    [InlineData(@"C:\src\repo\dist\server.dll")]
    [InlineData("C:/src/repo/dist/server.dll")]
    [InlineData(@"\\?\C:\src\repo")]
    [InlineData(@"\\fileserver\share\repo")]
    public void IsWindows_DriveRootedAndUncPaths_AreWindows(string path)
    {
        Assert.True(PathNamespace.IsWindows(path));
        Assert.False(PathNamespace.IsUnix(path));
    }

    [Theory]
    [InlineData("/mnt/c/src/repo/dist/server.dll")]
    [InlineData("/home/atte/.codex/config.toml")]
    [InlineData("/usr/lib/dotnet")]
    public void IsUnix_AbsolutePosixPaths_AreUnix(string path)
    {
        Assert.True(PathNamespace.IsUnix(path));
        Assert.False(PathNamespace.IsWindows(path));
    }

    /// <summary>
    /// A relative path belongs to neither namespace, and must not be reported as foreign: an MCP entry's
    /// arguments routinely contain relative paths and flags, and flagging those would bury the one
    /// argument that genuinely is written for the wrong OS.
    /// </summary>
    [Theory]
    [InlineData("dist/DotnetToolkit.McpServer.dll")]
    [InlineData("server.dll")]
    [InlineData("--json")]
    public void RelativePaths_BelongToNeitherNamespace(string path)
    {
        Assert.False(PathNamespace.IsWindows(path));
        Assert.False(PathNamespace.IsUnix(path));
        Assert.True(PathNamespace.IsUsableHere(path));
    }

    [Theory]
    [InlineData(@"C:\Users\atte9\source\repos\dotnet-toolkit", "/mnt/c/Users/atte9/source/repos/dotnet-toolkit")]
    [InlineData(@"\\?\C:\Users\atte9\source\repos\dotnet-toolkit", "/mnt/c/Users/atte9/source/repos/dotnet-toolkit")]
    [InlineData(@"D:\work", "/mnt/d/work")]
    public void ToUnix_RewritesDriveRootedPathsToTheWslMount(string windows, string expected) =>
        Assert.Equal(expected, PathNamespace.ToUnix(windows));

    [Theory]
    [InlineData("/mnt/c/Users/atte9/source/repos/dotnet-toolkit", @"C:\Users\atte9\source\repos\dotnet-toolkit")]
    [InlineData("/mnt/d/work", @"D:\work")]
    public void ToWindows_RewritesMountedDrivePathsBackToDriveLetters(string unix, string expected) =>
        Assert.Equal(expected, PathNamespace.ToWindows(unix));

    /// <summary>
    /// A Linux-native path has no Windows spelling, and a UNC share has no default WSL mount. Returning
    /// null rather than an invented path is what stops the verifier printing a repair that would fail
    /// exactly as the original did.
    /// </summary>
    [Theory]
    [InlineData("/home/atte/.codex")]
    [InlineData("/usr/lib/dotnet")]
    public void ToWindows_LinuxNativePaths_HaveNoWindowsSpelling(string unix) =>
        Assert.Null(PathNamespace.ToWindows(unix));

    [Fact]
    public void ToUnix_UncPath_HasNoDefaultMount() =>
        Assert.Null(PathNamespace.ToUnix(@"\\fileserver\share"));

    /// <summary>
    /// The round trip has to be lossless, because the verifier's repair text is produced by one
    /// direction and the user's next run reads it back through the other.
    /// </summary>
    [Fact]
    public void ToUnix_AndBack_RoundTripsThroughBothNamespaces()
    {
        const string Original = @"C:\Users\atte9\source\repos\dotnet-toolkit\dist\DotnetToolkit.McpServer.dll";

        var unix = PathNamespace.ToUnix(Original);

        Assert.NotNull(unix);
        Assert.Equal(Original, PathNamespace.ToWindows(unix));
    }

    /// <summary>
    /// The finding the whole verifier was written for: a Windows path stored in a profile that a Linux
    /// process has to launch from.
    /// </summary>
    [Fact]
    public void IsUsableHere_ForeignPath_IsRejectedAndTranslated()
    {
        var foreign = OperatingSystem.IsWindows()
            ? "/mnt/c/Users/atte9/source/repos/dotnet-toolkit"
            : @"C:\Users\atte9\source\repos\dotnet-toolkit";

        Assert.False(PathNamespace.IsUsableHere(foreign));

        var localized = PathNamespace.Localize(foreign);

        Assert.NotNull(localized);
        Assert.True(PathNamespace.IsUsableHere(localized));
    }

    [Fact]
    public void Localize_PathAlreadyInThisNamespace_IsUnchanged()
    {
        var native = OperatingSystem.IsWindows()
            ? @"C:\Users\atte9\source"
            : "/home/atte/source";

        Assert.Equal(native, PathNamespace.Localize(native));
    }
}
