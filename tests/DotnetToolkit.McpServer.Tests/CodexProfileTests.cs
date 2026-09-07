using DotnetToolkit.McpServer.Diagnostics;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Covers the TOML subset the installation verifier reads a Codex profile with, and the profile shape it
/// builds from it.
/// </summary>
/// <remarks>
/// The fixtures are written the way a real profile is, including the literal-string spelling a Windows
/// path needs and the two ways an <c>env</c> block can be written, because those spellings are exactly
/// what a hand-rolled reader gets wrong — and a reader that mangles the stored path reports a mismatch
/// the user never configured.
/// </remarks>
public sealed class CodexProfileTests : IDisposable
{
    private readonly string _home;

    public CodexProfileTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "dotnet-toolkit-codex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);

    /// <summary>Writes a <c>config.toml</c> into the fixture profile and loads it.</summary>
    /// <param name="contents">The file's text.</param>
    /// <returns>The loaded profile.</returns>
    private CodexProfile Load(string contents)
    {
        File.WriteAllText(Path.Combine(_home, "config.toml"), contents);
        return CodexProfile.Load(_home);
    }

    /// <summary>
    /// A profile that does not exist yet is an ordinary state — a fresh machine — not a failure, and the
    /// verifier reports it as "nothing registered here" rather than as a broken installation.
    /// </summary>
    [Fact]
    public void Load_NoConfigFile_ReportsAbsenceWithoutError()
    {
        var profile = CodexProfile.Load(_home);

        Assert.False(profile.Exists);
        Assert.Null(profile.ReadError);
        Assert.Empty(profile.Servers);
    }

    /// <summary>
    /// The exact registration shape the audit found broken: a Windows path stored as a TOML literal
    /// string, which must survive the read byte for byte or the reported repair names the wrong file.
    /// </summary>
    [Fact]
    public void Load_WindowsPathInLiteralString_KeepsBackslashesVerbatim()
    {
        var profile = Load(
            """
            [mcp_servers.dotnet]
            command = "dotnet"
            args = ['C:\Users\atte9\source\repos\dotnet-toolkit\dist\DotnetToolkit.McpServer.dll']
            """);

        var server = profile.Server("dotnet");

        Assert.NotNull(server);
        Assert.Equal("dotnet", server.Command);
        Assert.Equal(
            @"C:\Users\atte9\source\repos\dotnet-toolkit\dist\DotnetToolkit.McpServer.dll",
            Assert.Single(server.Args));
    }

    [Fact]
    public void Load_UnixPath_IsReadAsWritten()
    {
        var profile = Load(
            """
            [mcp_servers.dotnet]
            command = "dotnet"
            args = ["/mnt/c/Users/atte9/source/repos/dotnet-toolkit/dist/DotnetToolkit.McpServer.dll"]
            """);

        Assert.Equal(
            "/mnt/c/Users/atte9/source/repos/dotnet-toolkit/dist/DotnetToolkit.McpServer.dll",
            Assert.Single(profile.Server("dotnet")!.Args));
    }

    /// <summary>
    /// Both <c>env</c> spellings appear in real profiles, and a reader that understands only one tells a
    /// user their override is missing while it is sitting in the file.
    /// </summary>
    [Fact]
    public void Load_EnvWrittenAsAnInlineTable_IsRead()
    {
        var profile = Load(
            """
            [mcp_servers.dotnet]
            command = "dotnet"
            args = ["/opt/dotnet-toolkit/dist/DotnetToolkit.McpServer.dll"]
            env = { DOTNET_TOOLKIT_DOTNET_ROOT = "/usr/lib/dotnet" }
            """);

        Assert.Equal("/usr/lib/dotnet", profile.Server("dotnet")!.Env["DOTNET_TOOLKIT_DOTNET_ROOT"]);
    }

    [Fact]
    public void Load_EnvWrittenAsItsOwnSection_IsRead()
    {
        var profile = Load(
            """
            [mcp_servers.dotnet]
            command = "dotnet"
            args = ["/opt/dotnet-toolkit/dist/DotnetToolkit.McpServer.dll"]

            [mcp_servers.dotnet.env]
            DOTNET_TOOLKIT_DOTNET_ROOT = "/usr/lib/dotnet"
            """);

        Assert.Equal("/usr/lib/dotnet", profile.Server("dotnet")!.Env["DOTNET_TOOLKIT_DOTNET_ROOT"]);
    }

    /// <summary>
    /// Codex treats an absent <c>enabled</c> key as enabled. A verifier defaulting the other way would
    /// report every ordinary registration as switched off.
    /// </summary>
    [Fact]
    public void Load_EnabledKeyAbsent_MeansEnabled()
    {
        var profile = Load(
            """
            [mcp_servers.dotnet]
            command = "dotnet"
            """);

        Assert.True(profile.Server("dotnet")!.Enabled);
    }

    [Fact]
    public void Load_EnabledFalse_IsHonored()
    {
        var profile = Load(
            """
            [mcp_servers.dotnet]
            command = "dotnet"
            enabled = false
            """);

        Assert.False(profile.Server("dotnet")!.Enabled);
    }

    /// <summary>
    /// The <c>env</c> subtable must not be mistaken for a second registered server, or the verifier
    /// reports a server named <c>env</c> that nothing configured.
    /// </summary>
    [Fact]
    public void Load_ServerSubtables_AreNotReportedAsSeparateServers()
    {
        var profile = Load(
            """
            [mcp_servers.dotnet]
            command = "dotnet"

            [mcp_servers.dotnet.env]
            X = "y"

            [mcp_servers.other]
            command = "node"
            """);

        Assert.Equal(["dotnet", "other"], profile.Servers.Select(server => server.Name));
    }

    [Fact]
    public void Load_Marketplaces_AreReadWithSourceAndType()
    {
        var profile = Load(
            """
            [marketplaces.dotnet-toolkit]
            source_type = "local"
            source = "/mnt/c/Users/atte9/source/repos/dotnet-toolkit"
            """);

        var marketplace = Assert.Single(profile.Marketplaces);

        Assert.Equal("dotnet-toolkit", marketplace.Name);
        Assert.Equal("local", marketplace.SourceType);
        Assert.Equal("/mnt/c/Users/atte9/source/repos/dotnet-toolkit", marketplace.Source);
    }

    /// <summary>
    /// Comments and blank lines surround real configuration, and a value's trailing comment must not
    /// become part of the path the verifier then reports as missing.
    /// </summary>
    [Fact]
    public void Load_CommentsAndBlankLines_AreIgnored()
    {
        var profile = Load(
            """
            # dotnet-toolkit
            [mcp_servers.dotnet]

            command = "dotnet"   # the muxer on PATH
            args = ["/opt/toolkit/dist/DotnetToolkit.McpServer.dll"] # published server
            """);

        var server = profile.Server("dotnet");

        Assert.Equal("dotnet", server!.Command);
        Assert.Equal("/opt/toolkit/dist/DotnetToolkit.McpServer.dll", Assert.Single(server.Args));
    }

    /// <summary>
    /// A profile registering other servers must not be mistaken for one registering ours: that is the
    /// difference between "install the MCP server" and "your path is wrong", which have different fixes.
    /// </summary>
    [Fact]
    public void Server_NotRegistered_IsNull()
    {
        var profile = Load(
            """
            [mcp_servers.something-else]
            command = "node"
            """);

        Assert.Null(profile.Server("dotnet"));
    }
}
