using System.Diagnostics;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Tools;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// End-to-end semantic-tier test against the SampleSolution fixture (copied to the
/// test output dir). Requires the .NET SDK for restore + MSBuildWorkspace.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WorkspaceIntegrationTests : IAsyncLifetime
{
    private string _fixtureDir = "";
    private SolutionLocator _locator = null!;
    private ProjectIndex _index = null!;
    private WorkspaceHost _workspace = null!;

    public async Task InitializeAsync()
    {
        if (!Microsoft.Build.Locator.MSBuildLocator.IsRegistered)
            Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();

        _fixtureDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "SampleSolution");
        await RunDotnet("restore Sample.slnx", _fixtureDir);

        _locator = new SolutionLocator(NullLogger<SolutionLocator>.Instance, _fixtureDir);
        _index = new ProjectIndex(_locator, NullLogger<ProjectIndex>.Instance);
        _index.StartInitialization();
        _workspace = new WorkspaceHost(_locator, _index, NullLogger<WorkspaceHost>.Instance);
        _workspace.StartLoading();

        var solution = await _workspace.GetSolutionAsync(TimeSpan.FromMinutes(3));
        Assert.NotNull(solution);
        Assert.Equal(2, solution!.ProjectIds.Count);
    }

    public Task DisposeAsync()
    {
        _workspace.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FindReferencesLocatesCallSite()
    {
        var result = await RoslynTools.FindReferences(_workspace, _index, _locator, "Widget.Spin");
        Assert.Contains("App/Program.cs", result);
        Assert.Contains("widget.Spin(3)", result);
    }

    [Fact]
    public async Task FindImplementationsResolvesInterface()
    {
        var result = await RoslynTools.FindImplementations(_workspace, _index, _locator, "IWidget");
        Assert.Contains("Sample.Lib.Widget", result);
    }

    [Fact]
    public async Task GetSymbolShowsSignatureAndDoc()
    {
        var result = await RoslynTools.GetSymbol(_workspace, _index, _locator, "Sample.Lib.Widget");
        Assert.StartsWith("C Sample.Lib.Widget : IWidget", result);
        Assert.Contains("A spinning widget.", result);
        Assert.Contains("Spin", result);
    }

    [Fact]
    public async Task DiagnosticsReportUnusedVariable()
    {
        var result = await RoslynTools.Diagnostics(_workspace, _index, _locator, scope: "solution");
        Assert.Contains("CS0219", result); // 'unused' assigned but never used
        Assert.Contains("App/Program.cs", result);
    }

    private static async Task RunDotnet(string args, string workingDir)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var psi = new ProcessStartInfo(dotnet, args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"dotnet {args} failed:\n{stdout}\n{stderr}");
    }
}
