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
        Assert.Contains("dotnet-write", outcome.Stderr);
    }

    [Fact]
    public void Evaluate_EditOnFileNoProjectCompiles_Allows()
    {
        // validate_patch answers file_not_in_solution for a file outside the loaded solution, so denying
        // the plain tool here would leave no write path at all. GuardCsRead gates on the same membership.
        Assert.Equal(
            HookOutcome.Allow,
            GuardCsEdit.Evaluate(new HookPayload("Edit", _repo.FileWithoutProject, null), _repo.Context()));
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
        Assert.Contains("dotnet-read", outcome.Stderr);
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

    /// <summary>
    /// A recursive search names no file at all, so the single-file check cannot see it — and it reads every
    /// compiled file in the tree rather than one. The unguarded form read strictly MORE than the guarded one.
    /// </summary>
    /// <remarks>
    /// The .cs token here sits inside an option flag, which the flag skip in FindCsArgument discards, and the
    /// only bare operand is a directory. Neither half of the old check fired.
    /// </remarks>
    [Theory]
    [InlineData("grep -rn \"Alpha\" --include=*.cs src/")]
    [InlineData("grep -r \"Alpha\" src")]
    [InlineData("rg \"Alpha\" src")]
    [InlineData("grep -n \"Alpha\" src/App/*.cs")]
    public void Evaluate_TreeOrGlobScanOverCompiledSources_Denies(string command)
    {
        var outcome = GuardCsBashRead.Evaluate(new HookPayload("Bash", null, command), _repo.Context());

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains("searching", outcome.Stderr);
    }

    /// <summary>
    /// The membership check is what keeps this from over-blocking: a tree carrying no compiled .cs is
    /// ordinary text-search territory, whether it holds no C# at all or only Compile-Removed files.
    /// </summary>
    [Theory]
    [InlineData("grep -r \"Alpha\" loose")]
    [InlineData("grep -rn \"Alpha\" --include=*.cs src/App/fixtures")]
    // A file filter naming anything but *.cs means the walk cannot open a compiled source file however
    // deep it recurses, so blocking it was a pure false positive on a command that never touched C#.
    [InlineData("grep -rn \"Alpha\" --include=*.md .")]
    [InlineData("rg -g *.md \"Alpha\" src")]
    public void Evaluate_TreeScanOverNothingCompiled_Allows(string command)
    {
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

public sealed class WriteChecklistHintTests
{
    [Fact]
    public void Evaluate_FirstCallOfSession_EmitsTheChecklist()
    {
        var session = NewSession();
        try
        {
            var outcome = WriteChecklistHint.Evaluate(Patch(session));

            Assert.Equal(0, outcome.ExitCode);
            Assert.NotNull(outcome.Stdout);
            Assert.Contains("PreToolUse", outcome.Stdout);
            Assert.Contains("validate_patch", outcome.Stdout);
        }
        finally
        {
            DeleteMarker(session);
        }
    }

    [Fact]
    public void Evaluate_SecondCallOfSameSession_IsSilent()
    {
        var session = NewSession();
        try
        {
            WriteChecklistHint.Evaluate(Patch(session));
            var second = WriteChecklistHint.Evaluate(Patch(session));

            // A checklist repeated on every patch of a long editing task is noise, and noise is ignored.
            Assert.Equal(0, second.ExitCode);
            Assert.Null(second.Stdout);
        }
        finally
        {
            DeleteMarker(session);
        }
    }

    [Fact]
    public void Evaluate_NoSessionId_IsSilent()
    {
        // Without an id there is no way to tell a first call from a fiftieth, so emitting would repeat
        // the checklist on every patch. The always-loaded rule and the skill carry it instead.
        var outcome = WriteChecklistHint.Evaluate(new HookPayload("mcp__x__validate_patch", null, null));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Null(outcome.Stdout);
    }

    [Fact]
    public void TryParse_PayloadWithSessionId_ReadsIt()
    {
        var payload = HookPayload.TryParse("""{"tool_name":"Bash","session_id":"abc123"}""");

        Assert.NotNull(payload);
        Assert.Equal("abc123", payload.SessionId);
    }

    private static string NewSession() => Guid.NewGuid().ToString("N");

    private static HookPayload Patch(string session) =>
        new("mcp__plugin_dotnet-toolkit_dotnet__validate_patch", null, null) { SessionId = session };

    private static void DeleteMarker(string session)
    {
        var marker = Path.Combine(Path.GetTempPath(), "dotnet-toolkit-hooks", $"{session}.patched");
        if (File.Exists(marker))
        {
            File.Delete(marker);
        }
    }
}
