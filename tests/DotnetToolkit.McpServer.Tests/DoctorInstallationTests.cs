using DotnetToolkit.McpServer.Diagnostics;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Covers the two installation checks that decide whether a Codex profile is reported as healthy: which
/// marketplace entries this verifier is entitled to judge, and whether the plugin's own manifest counts
/// as a registration.
/// </summary>
/// <remarks>
/// Both shipped wrong once, in opposite directions, and both failures cost the reader the same thing —
/// trust in the report. The first judged every marketplace in the profile against this repository's
/// layout, so it failed the host's own bundled plugins and advised repointing them at a dotnet-toolkit
/// clone; following that advice would have broken a working install. The second knew only the
/// <c>config.toml</c> spelling of a registration, so the portable manifest route would have been reported
/// as "no MCP tools" on a profile that had them.
/// </remarks>
public sealed class DoctorInstallationTests : IDisposable
{
    private const string Group = "codex profile";

    private readonly string _root;

    public DoctorInstallationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dotnet-toolkit-doctor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Creates a directory under the fixture root and returns its full path.</summary>
    /// <param name="relative">A forward-slashed path below the fixture root.</param>
    /// <returns>The created directory.</returns>
    private string Dir(string relative)
    {
        var path = Path.Combine(_root, Path.Combine(relative.Split('/')));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes a file under the fixture root, creating its directory.</summary>
    /// <param name="relative">A forward-slashed path below the fixture root.</param>
    /// <param name="contents">The file's text.</param>
    private void WriteFile(string relative, string contents)
    {
        var path = Path.Combine(_root, Path.Combine(relative.Split('/')));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    /// <summary>Builds a locally-sourced marketplace entry, the only kind checked against the filesystem.</summary>
    /// <param name="name">The entry's name in the profile.</param>
    /// <param name="source">The source path as configuration stores it.</param>
    /// <returns>The entry.</returns>
    private static CodexMarketplace Local(string name, string source) => new(name, "local", source);

    // ---- which marketplaces this verifier may judge --------------------------------------------

    /// <summary>
    /// Codex's own marketplaces use a third manifest layout. Reading it is what stops the verifier
    /// reporting the host's bundled plugins as broken installations.
    /// </summary>
    [Theory]
    [InlineData(".codex-plugin/plugin.json")]
    [InlineData(".claude-plugin/marketplace.json")]
    [InlineData(".agents/plugins/marketplace.json")]
    public void CheckMarketplace_AnyRecognizedManifestLayout_Passes(string manifest)
    {
        var source = Dir("other");
        WriteFile($"other/{manifest}", "{}");

        var check = Doctor.CheckMarketplace(Group, Local("openai-bundled", source), pluginRoot: Dir("ours"));

        Assert.Equal(CheckStatus.Ok, check.Status);
    }

    /// <summary>
    /// The reported regression: another plugin's marketplace in a layout we do not read was failed, with
    /// a repair telling the user to repoint it at a dotnet-toolkit clone. Skipping states the same fact
    /// without pronouncing a verdict on something this plugin does not own.
    /// </summary>
    [Fact]
    public void CheckMarketplace_ForeignSourceInAnUnknownLayout_IsSkippedNotFailed()
    {
        var check = Doctor.CheckMarketplace(
            Group,
            Local("openai-bundled", Dir("other")),
            pluginRoot: Dir("ours"));

        Assert.Equal(CheckStatus.Skip, check.Status);
        Assert.DoesNotContain("dotnet-toolkit clone", check.Detail + check.Fix);
    }

    /// <summary>Our own marketplace with no manifest is a real defect, and still fails with the repair.</summary>
    [Fact]
    public void CheckMarketplace_OurSourceWithNoManifest_Fails()
    {
        var ours = Dir("ours");

        var check = Doctor.CheckMarketplace(Group, Local("dotnet-toolkit", ours), pluginRoot: ours);

        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("dotnet-toolkit clone", check.Fix);
    }

    /// <summary>
    /// A path written for the other OS is worth surfacing wherever it appears — it is the mismatch this
    /// verb exists to find — but on someone else's entry it is information, not a failed install.
    /// </summary>
    [Fact]
    public void CheckMarketplace_ForeignSourceInTheWrongNamespace_WarnsRatherThanFails()
    {
        var foreign = OperatingSystem.IsWindows() ? "/opt/some-other-plugin" : @"C:\some-other-plugin";

        var check = Doctor.CheckMarketplace(Group, Local("openai-primary-runtime", foreign), Dir("ours"));

        Assert.Equal(CheckStatus.Warn, check.Status);
        Assert.Contains("another plugin", check.Detail);
    }

    /// <summary>The same wrong-namespace path in *our* entry is the finding, with the translation to apply.</summary>
    [Fact]
    public void CheckMarketplace_OurSourceInTheWrongNamespace_FailsWithTheTranslation()
    {
        var ours = OperatingSystem.IsWindows()
            ? "/mnt/c/Users/atte9/source/repos/dotnet-toolkit"
            : @"C:\Users\atte9\source\repos\dotnet-toolkit";

        var check = Doctor.CheckMarketplace(
            Group,
            Local("dotnet-toolkit", ours),
            pluginRoot: PathNamespace.Localize(ours)!);

        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains(PathNamespace.Localize(ours)!, check.Fix);
    }

    /// <summary>
    /// Ownership is decided after translating the stored path, because a foreign namespace is exactly the
    /// case where the answer changes what the reader is told to do.
    /// </summary>
    [Fact]
    public void CheckMarketplace_NonLocalSource_IsNotCheckedFromHere()
    {
        var check = Doctor.CheckMarketplace(Group, new CodexMarketplace("remote", "git", "https://x/y"), _root);

        Assert.Equal(CheckStatus.Skip, check.Status);
    }

    // ---- the portable registration -------------------------------------------------------------

    /// <summary>
    /// The declaration that survives moving between Windows and WSL: a plugin-relative path the host
    /// resolves against wherever the plugin sits, rather than one absolute path spelled for one OS.
    /// </summary>
    [Fact]
    public void DeclaredInManifest_ManifestPointingAtAPublishedBinary_ResolvesIt()
    {
        WriteFile(".codex-plugin/plugin.json", """{"mcpServers": "./.codex-plugin/mcp.json"}""");
        WriteFile(
            ".codex-plugin/mcp.json",
            """{"mcpServers": {"dotnet": {"command": "dotnet", "args": ["dist/DotnetToolkit.McpServer.dll"]}}}""");
        WriteFile("dist/DotnetToolkit.McpServer.dll", "binary");

        Assert.Equal(
            Path.Combine(_root, "dist", "DotnetToolkit.McpServer.dll"),
            Doctor.DeclaredInManifest(_root));
    }

    /// <summary>
    /// A manifest naming a binary nobody published fails at launch exactly like no registration at all,
    /// and the two have different repairs — so resolving the path is not enough, it has to exist.
    /// </summary>
    [Fact]
    public void DeclaredInManifest_DeclaredBinaryWasNeverPublished_IsNotARegistration()
    {
        WriteFile(".codex-plugin/plugin.json", """{"mcpServers": "./.codex-plugin/mcp.json"}""");
        WriteFile(
            ".codex-plugin/mcp.json",
            """{"mcpServers": {"dotnet": {"args": ["dist/DotnetToolkit.McpServer.dll"]}}}""");

        Assert.Null(Doctor.DeclaredInManifest(_root));
    }

    [Fact]
    public void DeclaredInManifest_ManifestDeclaresNoServers_IsNotARegistration()
    {
        WriteFile(".codex-plugin/plugin.json", """{"name": "dotnet-toolkit"}""");

        Assert.Null(Doctor.DeclaredInManifest(_root));
    }

    /// <summary>A different server in the file is not ours, and must not be counted as our registration.</summary>
    [Fact]
    public void DeclaredInManifest_FileDeclaresOnlyAnotherServer_IsNotARegistration()
    {
        WriteFile(".codex-plugin/plugin.json", """{"mcpServers": "./.codex-plugin/mcp.json"}""");
        WriteFile(".codex-plugin/mcp.json", """{"mcpServers": {"other": {"args": ["x.dll"]}}}""");

        Assert.Null(Doctor.DeclaredInManifest(_root));
    }

    /// <summary>An installation with no Codex manifest is an ordinary state, not a parse failure.</summary>
    [Fact]
    public void DeclaredInManifest_NoManifestAtAll_IsNotARegistration() =>
        Assert.Null(Doctor.DeclaredInManifest(_root));

    /// <summary>A half-written manifest must report "not declared", never take the verb down with it.</summary>
    [Fact]
    public void DeclaredInManifest_MalformedManifest_IsNotARegistration()
    {
        WriteFile(".codex-plugin/plugin.json", "{ this is not json");

        Assert.Null(Doctor.DeclaredInManifest(_root));
    }
}
