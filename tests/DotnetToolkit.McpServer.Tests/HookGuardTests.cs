using DotnetToolkit.McpServer.Hooks;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// A throwaway repo tree on disk: the guards answer solution membership from the filesystem, so there
/// is nothing to substitute for real directories and project files.
/// </summary>
public sealed class HookRepoFixture : IDisposable
{
    public HookRepoFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "dotnet-toolkit-hooks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "App.slnx"), "<Solution />");

        var project = Path.Combine(Root, "src", "App");
        Directory.CreateDirectory(project);
        File.WriteAllText(
            Path.Combine(project, "App.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><Compile Remove="fixtures/**" /></ItemGroup></Project>""");
        File.WriteAllText(Path.Combine(project, "Compiled.cs"), "class Compiled;");

        var excluded = Path.Combine(project, "fixtures");
        Directory.CreateDirectory(excluded);
        File.WriteAllText(Path.Combine(excluded, "Excluded.cs"), "class Excluded;");

        // A test fixture's own throwaway solution: its files belong to it, not to the outer repo.
        var nested = Path.Combine(Root, "tests", "SampleSolution");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "Sample.slnx"), "<Solution />");
        File.WriteAllText(
            Path.Combine(nested, "Sample.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk" />""");
        File.WriteAllText(Path.Combine(nested, "Nested.cs"), "class Nested;");

        Directory.CreateDirectory(Path.Combine(Root, "loose"));
        File.WriteAllText(Path.Combine(Root, "loose", "NoProject.cs"), "class NoProject;");
    }

    public string Root { get; }

    public string CompiledFile => Path.Combine(Root, "src", "App", "Compiled.cs");

    public string ExcludedFile => Path.Combine(Root, "src", "App", "fixtures", "Excluded.cs");

    public string NestedSolutionFile => Path.Combine(Root, "tests", "SampleSolution", "Nested.cs");

    public string FileWithoutProject => Path.Combine(Root, "loose", "NoProject.cs");

    internal HookContext Context() =>
        new(Root, Path.Combine(Root, "docs", "tools"), Root, HookContext.DefaultReadBlocklist);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}

public sealed class CsFileMembershipTests : IClassFixture<HookRepoFixture>
{
    private readonly HookRepoFixture _repo;

    public CsFileMembershipTests(HookRepoFixture repo) => _repo = repo;

    [Fact]
    public void TryResolveOwningProject_FileCompiledByProject_ReturnsRelativeProjectPath()
    {
        var governed = CsFileMembership.TryResolveOwningProject(_repo.CompiledFile, _repo.Root, out var project);

        Assert.True(governed);
        Assert.Equal("src/App/App.csproj", project);
    }

    [Fact]
    public void TryResolveOwningProject_CompileRemoveGlobExcludesFile_ReturnsFalse()
    {
        Assert.False(CsFileMembership.TryResolveOwningProject(_repo.ExcludedFile, _repo.Root, out _));
    }

    [Fact]
    public void TryResolveOwningProject_NestedSolutionBetweenFileAndRoot_ReturnsFalse()
    {
        Assert.False(CsFileMembership.TryResolveOwningProject(_repo.NestedSolutionFile, _repo.Root, out _));
    }

    [Fact]
    public void TryResolveOwningProject_NoProjectGovernsDirectory_ReturnsFalse()
    {
        Assert.False(CsFileMembership.TryResolveOwningProject(_repo.FileWithoutProject, _repo.Root, out _));
    }

    [Fact]
    public void TryResolveOwningProject_FileOutsideRoot_ReturnsFalseWithoutClimbingPastIt()
    {
        // Without the containment check the walk climbs past the root to whatever project file sits
        // above it on the real filesystem, and reports a foreign file as this repo's own.
        var outside = Path.Combine(Path.GetTempPath(), "somewhere-else", "Foreign.cs");

        Assert.False(CsFileMembership.TryResolveOwningProject(outside, _repo.Root, out _));
    }
}

public sealed class GuardCsEditTests : IClassFixture<HookRepoFixture>
{
    private readonly HookRepoFixture _repo;

    public GuardCsEditTests(HookRepoFixture repo) => _repo = repo;

    [Fact]
    public void Evaluate_EditOnExistingCsFile_Denies()
    {
        var payload = new HookPayload("Edit", _repo.CompiledFile, null);

        var outcome = GuardCsEdit.Evaluate(payload, _repo.Context());

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains("validate_patch", outcome.Stderr);
    }

    [Fact]
    public void Evaluate_WriteCreatingNewCsFile_Allows()
    {
        // A new file has no symbolId to lease a contentVersion against, so it cannot go through
        // validate_patch in the first place.
        var payload = new HookPayload("Write", Path.Combine(_repo.Root, "src", "App", "BrandNew.cs"), null);

        Assert.Equal(HookOutcome.Allow, GuardCsEdit.Evaluate(payload, _repo.Context()));
    }

    [Fact]
    public void Evaluate_WriteOverExistingCsFile_Denies()
    {
        var payload = new HookPayload("Write", _repo.CompiledFile, null);

        Assert.Equal(2, GuardCsEdit.Evaluate(payload, _repo.Context()).ExitCode);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("App.csproj")]
    [InlineData("Component.razor")]
    public void Evaluate_NonCsFile_Allows(string name)
    {
        var payload = new HookPayload("Edit", Path.Combine(_repo.Root, name), null);

        Assert.Equal(HookOutcome.Allow, GuardCsEdit.Evaluate(payload, _repo.Context()));
    }
}

public sealed class GuardCsReadTests : IClassFixture<HookRepoFixture>
{
    private readonly HookRepoFixture _repo;

    public GuardCsReadTests(HookRepoFixture repo) => _repo = repo;

    [Fact]
    public void Evaluate_ReadOfCompiledFile_DeniesNamingTheProject()
    {
        var outcome = GuardCsRead.Evaluate(new HookPayload("Read", _repo.CompiledFile, null), _repo.Context());

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains("src/App/App.csproj", outcome.Stderr);
        Assert.Contains("search_index", outcome.Stderr);
    }

    [Fact]
    public void Evaluate_ReadOfFileNoProjectCompiles_Allows()
    {
        Assert.Equal(
            HookOutcome.Allow,
            GuardCsRead.Evaluate(new HookPayload("Read", _repo.FileWithoutProject, null), _repo.Context()));
    }

    [Fact]
    public void Evaluate_ReadOfNonExistentFile_Allows()
    {
        var missing = Path.Combine(_repo.Root, "src", "App", "Gone.cs");

        Assert.Equal(
            HookOutcome.Allow,
            GuardCsRead.Evaluate(new HookPayload("Read", missing, null), _repo.Context()));
    }
}

public sealed class GuardCsBashReadTests : IClassFixture<HookRepoFixture>
{
    private readonly HookRepoFixture _repo;

    public GuardCsBashReadTests(HookRepoFixture repo) => _repo = repo;

    [Fact]
    public void Evaluate_MultiTermGrepOverCompiledFile_Denies()
    {
        // The exact shape that bypassed the shell guard: the quoted alternation contains the same
        // character the segmenter splits on.
        var command = $"""grep -n "Alpha\|Beta" "{_repo.CompiledFile}" | head""";

        var outcome = GuardCsBashRead.Evaluate(new HookPayload("Bash", null, command), _repo.Context());

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains("'grep'", outcome.Stderr);
    }

    [Fact]
    public void Evaluate_SedOverCompiledFile_Denies()
    {
        var command = $"sed -n '1,40p' {_repo.CompiledFile}";

        Assert.Equal(2, GuardCsBashRead.Evaluate(new HookPayload("Bash", null, command), _repo.Context()).ExitCode);
    }

    [Theory]
    [InlineData("git diff -- {0}")]
    [InlineData("git log {0}")]
    [InlineData("dotnet build {0}")]
    public void Evaluate_CommandNotOnTheBlocklist_Allows(string template)
    {
        var command = string.Format(template, _repo.CompiledFile);

        Assert.Equal(
            HookOutcome.Allow,
            GuardCsBashRead.Evaluate(new HookPayload("Bash", null, command), _repo.Context()));
    }

    [Fact]
    public void Evaluate_FindByNameGlob_Allows()
    {
        Assert.Equal(
            HookOutcome.Allow,
            GuardCsBashRead.Evaluate(new HookPayload("Bash", null, "find . -name '*.cs'"), _repo.Context()));
    }

    [Fact]
    public void Evaluate_ReadOfFileOutsideTheRepo_Allows()
    {
        var command = $"cat {Path.Combine(Path.GetTempPath(), "elsewhere", "Other.cs")}";

        Assert.Equal(
            HookOutcome.Allow,
            GuardCsBashRead.Evaluate(new HookPayload("Bash", null, command), _repo.Context()));
    }
}

public sealed class HookPayloadTests
{
    [Fact]
    public void TryParse_FullPayload_ReadsToolNameAndFilePath()
    {
        var payload = HookPayload.TryParse("""{"tool_name":"Read","tool_input":{"file_path":"a/B.cs"}}""");

        Assert.NotNull(payload);
        Assert.Equal("Read", payload.ToolName);
        Assert.Equal("a/B.cs", payload.FilePath);
        Assert.Null(payload.Command);
    }

    [Fact]
    public void TryParse_BashPayload_ReadsCommand()
    {
        var payload = HookPayload.TryParse("""{"tool_name":"Bash","tool_input":{"command":"cat x.cs"}}""");

        Assert.NotNull(payload);
        Assert.Equal("cat x.cs", payload.Command);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"tool_input":{"file_path":"a.cs"}}""")]
    public void TryParse_UnusablePayload_ReturnsNull(string json)
    {
        // Every one of these must mean "allow". The Windows failure this port fixed was a stub
        // interpreter producing empty output that the shell guards then read as a valid parse.
        Assert.Null(HookPayload.TryParse(json));
    }

    [Fact]
    public void TryParse_MissingToolInput_StillReadsToolName()
    {
        var payload = HookPayload.TryParse("""{"tool_name":"Bash"}""");

        Assert.NotNull(payload);
        Assert.Equal("Bash", payload.ToolName);
        Assert.Null(payload.FilePath);
    }
}
