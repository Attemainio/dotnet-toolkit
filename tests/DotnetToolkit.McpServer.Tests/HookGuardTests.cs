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
        File.WriteAllText(
            Path.Combine(Root, "App.sln"),
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"AppWithOwnSln\", \"src\\AppWithOwnSln\\AppWithOwnSln.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\nEndProject\n");

        var project = Path.Combine(Root, "src", "App");
        Directory.CreateDirectory(project);
        File.WriteAllText(
            Path.Combine(project, "App.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><Compile Remove="fixtures/**" /></ItemGroup></Project>""");
        File.WriteAllText(Path.Combine(project, "Compiled.cs"), "class Compiled;");

        var excluded = Path.Combine(project, "fixtures");
        Directory.CreateDirectory(excluded);
        File.WriteAllText(Path.Combine(excluded, "Excluded.cs"), "class Excluded;");

        // A project that also ships its own per-project .sln for standalone-open convenience, while
        // still being included in the root solution -- the PandaAI.Core.sln/PandaAI.sln shape. The
        // nested .sln here must not exempt this project from guard enforcement.
        var ownSln = Path.Combine(Root, "src", "AppWithOwnSln");
        Directory.CreateDirectory(ownSln);
        File.WriteAllText(Path.Combine(ownSln, "AppWithOwnSln.sln"), "");
        File.WriteAllText(
            Path.Combine(ownSln, "AppWithOwnSln.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk" />""");
        File.WriteAllText(Path.Combine(ownSln, "Included.cs"), "class Included;");

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

        // A separate checkout the guard must not speak for. Same shape as Root -- solution, project, a
        // compiled .cs -- so a command that reaches it is only allowed because it is somewhere else, not
        // because there was nothing there to find.
        Sibling = Path.Combine(Path.GetTempPath(), "dotnet-toolkit-sibling-" + Guid.NewGuid().ToString("N"));
        var siblingProject = Path.Combine(Sibling, "src", "Other");
        Directory.CreateDirectory(siblingProject);
        File.WriteAllText(Path.Combine(Sibling, "Other.slnx"), "<Solution />");
        File.WriteAllText(
            Path.Combine(siblingProject, "Other.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk" />""");
        File.WriteAllText(Path.Combine(siblingProject, "Elsewhere.cs"), "class Elsewhere;");
    }

    public string Root { get; }

    /// <summary>A second repo tree, deliberately NOT under <see cref="Root"/>.</summary>
    public string Sibling { get; }

    public string CompiledFile => Path.Combine(Root, "src", "App", "Compiled.cs");

    public string ExcludedFile => Path.Combine(Root, "src", "App", "fixtures", "Excluded.cs");

    public string NestedSolutionFile => Path.Combine(Root, "tests", "SampleSolution", "Nested.cs");

    public string IncludedNestedSolutionFile => Path.Combine(Root, "src", "AppWithOwnSln", "Included.cs");

    public string FileWithoutProject => Path.Combine(Root, "loose", "NoProject.cs");

    internal HookContext Context() =>
        new(Root, Path.Combine(Root, "docs", "tools"), Root, HookContext.DefaultReadBlocklist);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
            Directory.Delete(Sibling, recursive: true);
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
    public void TryResolveOwningProject_NestedSolutionButProjectIsInRootSolution_ReturnsTrue()
    {
        var governed = CsFileMembership.TryResolveOwningProject(
            _repo.IncludedNestedSolutionFile, _repo.Root, out var project);

        Assert.True(governed);
        Assert.Equal("src/AppWithOwnSln/AppWithOwnSln.csproj", project);
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

public sealed class GuardCsReadTests : IClassFixture<HookRepoFixture>
{
    private readonly HookRepoFixture _repo;

    public GuardCsReadTests(HookRepoFixture repo) => _repo = repo;

    [Fact]
    public void Evaluate_ReadOfCompiledFile_AllowsWithAdvisoryNote()
    {
        var outcome = GuardCsRead.Evaluate(new HookPayload("Read", _repo.CompiledFile, null), _repo.Context());

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains("src/App/App.csproj", outcome.Stdout);
        Assert.Contains("dotnet-read", outcome.Stdout);
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

    [Fact]
    public void Evaluate_BlockedModeOnCompiledFile_Denies()
    {
        var outcome = GuardCsRead.Evaluate(new HookPayload("Read", _repo.CompiledFile, null), _repo.Context(), blocked: true);

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains("dotnet-read", outcome.Stderr);
    }

    [Fact]
    public void Evaluate_BlockedModeOnFileNoProjectCompiles_Allows()
    {
        Assert.Equal(
            HookOutcome.Allow,
            GuardCsRead.Evaluate(new HookPayload("Read", _repo.FileWithoutProject, null), _repo.Context(), blocked: true));
    }

    /// <summary>
    /// A Read with offset/limit is how a located symbol's text is meant to be fetched, so it draws no
    /// note - and no denial even in forced-block mode, or the MCP probe could not read code at all.
    /// </summary>
    [Theory]
    [InlineData("""{"file_path":"x","offset":12,"limit":30}""")]
    [InlineData("""{"file_path":"x","offset":12}""")]
    [InlineData("""{"file_path":"x","limit":40}""")]
    public void Evaluate_RangedRead_IsSilentInEveryMode(string toolInput)
    {
        var payload = new HookPayload("Read", _repo.CompiledFile, null) { ToolInputRaw = toolInput };

        Assert.Equal(HookOutcome.Allow, GuardCsRead.Evaluate(payload, _repo.Context()));
        Assert.Equal(HookOutcome.Allow, GuardCsRead.Evaluate(payload, _repo.Context(), blocked: true));
    }

    [Fact]
    public void Evaluate_WholeFileReadWithExplicitInput_StillAdvises()
    {
        var payload = new HookPayload("Read", _repo.CompiledFile, null) { ToolInputRaw = """{"file_path":"x"}""" };

        Assert.Contains("ranged Read", GuardCsRead.Evaluate(payload, _repo.Context()).Stdout);
    }
}

public sealed class GuardCsBashReadTests : IClassFixture<HookRepoFixture>
{
    private readonly HookRepoFixture _repo;

    public GuardCsBashReadTests(HookRepoFixture repo) => _repo = repo;

    [Theory]
    [InlineData("sed -n '12,40p' {0}")]
    [InlineData("sed -n 12p {0}")]
    [InlineData("head -n 40 {0}")]
    [InlineData("head -40 {0}")]
    [InlineData("tail -n +12 {0}")]
    public void Evaluate_RangedReadOfCompiledFile_IsSilentInEveryMode(string template)
    {
        var command = string.Format(template, _repo.CompiledFile);

        Assert.Equal(HookOutcome.Allow, GuardCsBashRead.Evaluate(new HookPayload("Bash", null, command), _repo.Context()));
        Assert.Equal(HookOutcome.Allow, GuardCsBashRead.Evaluate(new HookPayload("Bash", null, command), _repo.Context(), blocked: true));
    }

    [Theory]
    [InlineData("sed 's/a/b/' {0}")]
    [InlineData("head {0}")]
    [InlineData("cat {0}")]
    public void Evaluate_WholeFileReadOfCompiledFile_StillAdvises(string template)
    {
        var command = string.Format(template, _repo.CompiledFile);
        var outcome = GuardCsBashRead.Evaluate(new HookPayload("Bash", null, command), _repo.Context());

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains("ranged read", outcome.Stdout);
    }

    [Fact]
    public void Evaluate_MultiTermGrepOverCompiledFile_AllowsWithAdvisoryNote()
    {
        // The exact shape that bypassed the shell guard: the quoted alternation contains the same
        // character the segmenter splits on.
        var command = $"""grep -n "Alpha\|Beta" "{_repo.CompiledFile}" | head""";

        var outcome = GuardCsBashRead.Evaluate(new HookPayload("Bash", null, command), _repo.Context());

        Assert.Equal(0, outcome.ExitCode);
        // The raw JSON text escapes the backtick to ` (System.Text.Json's default encoder), so
            // this decodes the payload rather than substring-matching the raw bytes.
            var reason = System.Text.Json.Nodes.JsonNode.Parse(outcome.Stdout!)!["hookSpecificOutput"]!["permissionDecisionReason"]!.GetValue<string>();
            Assert.Contains("`grep`", reason);
    }

    [Fact]
    public void Evaluate_SedOverCompiledFile_AllowsWithAdvisoryNote()
    {
        var command = $"sed -n '1,40p' {_repo.CompiledFile}";

        Assert.Equal(0, GuardCsBashRead.Evaluate(new HookPayload("Bash", null, command), _repo.Context()).ExitCode);
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
    /// A cd earlier in the same command decides what a later relative path means. Resolved against the
    /// hook's own directory instead, a search of an unrelated checkout was denied as a read of THIS repo,
    /// in a message naming a project the command had already left.
    /// </summary>
    [Fact]
    public void Evaluate_TreeScanAfterCdIntoAnotherRepo_Allows()
    {
        var command = $"cd {_repo.Sibling} && grep -rn \"Alpha\" .";

        Assert.Equal(
            HookOutcome.Allow,
            GuardCsBashRead.Evaluate(new HookPayload("Bash", null, command), _repo.Context()));
    }

    /// <summary>The cd is honoured, not treated as a way out: one INTO this repo still guards what it reaches.</summary>
    [Fact]
    public void Evaluate_TreeScanAfterCdWithinTheRepo_StillAdvises()
    {
        var outcome = GuardCsBashRead.Evaluate(
            new HookPayload("Bash", null, "cd src && grep -rn \"Alpha\" ."), _repo.Context());

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains("searching", outcome.Stdout);
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
    public void Evaluate_TreeOrGlobScanOverCompiledSources_Advises(string command)
    {
        var outcome = GuardCsBashRead.Evaluate(new HookPayload("Bash", null, command), _repo.Context());

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains("searching", outcome.Stdout);
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

    [Fact]
    public void Evaluate_BlockedModeOnCompiledFile_Denies()
    {
        var command = $"cat {_repo.CompiledFile}";

        var outcome = GuardCsBashRead.Evaluate(new HookPayload("Bash", null, command), _repo.Context(), blocked: true);

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains("dotnet-read", outcome.Stderr);
    }

    [Fact]
    public void Evaluate_BlockedModeTreeScan_Denies()
    {
        var outcome = GuardCsBashRead.Evaluate(
            new HookPayload("Bash", null, "grep -r \"Alpha\" src"), _repo.Context(), blocked: true);

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains("dotnet-read", outcome.Stderr);
    }

    [Fact]
    public void Evaluate_BlockedModeCommandNotOnBlocklist_Allows()
    {
        var command = $"git log {_repo.CompiledFile}";

        Assert.Equal(
            HookOutcome.Allow,
            GuardCsBashRead.Evaluate(new HookPayload("Bash", null, command), _repo.Context(), blocked: true));
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

    // The meter reads five fields no guard ever needed, so a parser change that silently dropped them
    // would leave every guard test passing and the measurement blind.
    [Fact]
    public void TryParse_PostToolUsePayload_ReadsBothDirectionsAndTheAgentItRanIn()
    {
        var payload = HookPayload.TryParse(
            """
            {"tool_name":"Grep","tool_use_id":"toolu_01","agent_id":"agt_7",
             "agent_type":"dotnet-perf-raw-probe","session_id":"ses_abc",
             "tool_input":{"pattern":"class Solver"},
             "tool_response":{"mode":"content","numLines":3}}
            """);

        Assert.NotNull(payload);
        Assert.Equal("toolu_01", payload.ToolUseId);
        Assert.Equal("agt_7", payload.AgentId);
        Assert.Equal("dotnet-perf-raw-probe", payload.AgentType);
        Assert.Equal("ses_abc", payload.SessionId);
        Assert.NotNull(payload.ToolInputRaw);
        Assert.Contains("class Solver", payload.ToolInputRaw, StringComparison.Ordinal);
        Assert.NotNull(payload.ToolResponseRaw);
        Assert.Contains("numLines", payload.ToolResponseRaw, StringComparison.Ordinal);
    }

    // A string tool_response is as ordinary as an object one - Bash returns text - so the raw capture
    // must handle both rather than throwing on the shape it did not expect.
    [Fact]
    public void TryParse_StringToolResponse_IsMeasurableToo()
    {
        var payload = HookPayload.TryParse(
            """{"tool_name":"Bash","tool_use_id":"toolu_02","tool_response":"total 12"}""");

        Assert.NotNull(payload);
        Assert.Equal("\"total 12\"", payload.ToolResponseRaw);
    }

    [Fact]
    public void TryParse_CodexPayload_ReadsHostAndModelFields()
    {
        var payload = HookPayload.TryParse(
            """
            {"hook_event_name":"PostToolUse","tool_name":"apply_patch","tool_use_id":"call_1",
             "session_id":"ses_1","turn_id":"turn_1","model":"local/qwen3-coder",
             "transcript_path":"/tmp/transcript.jsonl","tool_input":{"command":"*** Begin Patch"},
             "tool_response":"Done"}
            """);

        Assert.NotNull(payload);
        Assert.Equal("turn_1", payload.TurnId);
        Assert.Equal("local/qwen3-coder", payload.Model);
        Assert.Equal("/tmp/transcript.jsonl", payload.TranscriptPath);
        Assert.Equal("PostToolUse", payload.HookEventName);
    }

    [Fact]
    public void TryParse_SubagentLifecyclePayload_UsesEventNameAsToolName()
    {
        var payload = HookPayload.TryParse(
            """{"hook_event_name":"SubagentStart","session_id":"s","turn_id":"t","agent_id":"a","agent_type":"explorer"}""");

        Assert.NotNull(payload);
        Assert.Equal("SubagentStart", payload.ToolName);
        Assert.Equal("explorer", payload.AgentType);
    }
}

public sealed class SubagentTrackerTests
{
    [Fact]
    public void Resolve_CorrelatesLifecycleEventWithCodexToolPayload()
    {
        var session = Guid.NewGuid().ToString("N");
        var turn = Guid.NewGuid().ToString("N");
        var lifecycle = new HookPayload("SubagentStart", null, null)
        {
            SessionId = session,
            TurnId = turn,
            AgentId = "agent-7",
            AgentType = "explorer",
        };
        var tool = new HookPayload("Bash", null, "rg Foo") { SessionId = session, TurnId = turn };

        try
        {
            SubagentTracker.Start(lifecycle);
            Assert.Equal(("agent-7", "explorer"), SubagentTracker.Resolve(tool));
        }
        finally
        {
            var outcome = SubagentTracker.Stop(lifecycle);
            Assert.Equal("{\"continue\":true}", outcome.Stdout);
        }
    }
}

public sealed class CsWriteChecklistHintTests : IClassFixture<HookRepoFixture>
{
    private readonly HookRepoFixture _repo;

    public CsWriteChecklistHintTests(HookRepoFixture repo) => _repo = repo;

    [Fact]
    public void Evaluate_FirstEditOfSession_EmitsTheChecklist()
    {
        var session = NewSession();
        try
        {
            var outcome = CsWriteChecklistHint.Evaluate(Edit(session, _repo.CompiledFile), _repo.Context());

            Assert.Equal(0, outcome.ExitCode);
            Assert.NotNull(outcome.Stdout);
            Assert.Contains("PreToolUse", outcome.Stdout);
            Assert.Contains("dotnet build", outcome.Stdout);
            Assert.DoesNotContain("validate_patch", outcome.Stdout);
        }
        finally
        {
            DeleteMarker(session);
        }
    }

    [Fact]
    public void Evaluate_SecondEditOfSameSession_IsSilent()
    {
        var session = NewSession();
        try
        {
            CsWriteChecklistHint.Evaluate(Edit(session, _repo.CompiledFile), _repo.Context());
            var second = CsWriteChecklistHint.Evaluate(Edit(session, _repo.CompiledFile), _repo.Context());

            // A checklist repeated on every edit of a long refactor is noise, and noise is ignored.
            Assert.Equal(0, second.ExitCode);
            Assert.Null(second.Stdout);
        }
        finally
        {
            DeleteMarker(session);
        }
    }

    [Fact]
    public void Evaluate_FileNoProjectCompiles_IsSilentAndClaimsNothing()
    {
        var session = NewSession();
        try
        {
            // Neither a loose file nor a nested fixture solution's file is the C# the checklist is about,
            // and neither may consume the session's one emission.
            Assert.Null(CsWriteChecklistHint.Evaluate(Edit(session, _repo.FileWithoutProject), _repo.Context()).Stdout);
            Assert.Null(CsWriteChecklistHint.Evaluate(Edit(session, _repo.NestedSolutionFile), _repo.Context()).Stdout);
            Assert.Null(CsWriteChecklistHint.Evaluate(Edit(session, Path.Combine(_repo.Root, "README.md")), _repo.Context()).Stdout);
            Assert.NotNull(CsWriteChecklistHint.Evaluate(Edit(session, _repo.CompiledFile), _repo.Context()).Stdout);
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
        // the checklist on every edit. The always-loaded rule and the skill carry it instead.
        var outcome = CsWriteChecklistHint.Evaluate(new HookPayload("Edit", _repo.CompiledFile, null), _repo.Context());

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

    private static HookPayload Edit(string session, string file) =>
        new("Edit", file, null) { SessionId = session };

    private static void DeleteMarker(string session)
    {
        var marker = Path.Combine(Path.GetTempPath(), "dotnet-toolkit-hooks", $"{session}.cs-checklist");
        if (File.Exists(marker))
        {
            File.Delete(marker);
        }
    }
}
