using System.Diagnostics;
using DotnetToolkit.McpServer.Git;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Semantic diff over a real (tiny) git repo in /tmp. No MSBuild involved — the diff reads source out
/// of git and compares fingerprints, which is what makes it cheap enough to run per question.
/// </summary>
public sealed class SemanticDiffTests : IAsyncLifetime
{
    private string _root = "";
    private GitAnalyzer _git = null!;
    private SemanticDiff _diff = null!;

    private const string Original = """
        namespace Demo;

        public class Calc
        {
            public int Add(int a, int b) => a + b;
            public int Mul(int a, int b) => a * b;
        }
        """;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "dt-git-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        await Git("init", "-q");
        await Git("config", "user.email", "test@example.com");
        await Git("config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(_root, "Calc.cs"), Original);
        await Git("add", ".");
        await Git("commit", "-q", "-m", "initial");

        var locator = new SolutionLocator(NullLogger<SolutionLocator>.Instance, _root);
        _git = new GitAnalyzer(locator, NullLogger<GitAnalyzer>.Instance);
        _diff = new SemanticDiff(_git);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private async Task Commit(string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Calc.cs"), content);
        await Git("add", ".");
        await Git("commit", "-q", "-m", message);
    }

    // The core promise: a reformat/comment-only commit is NOT a semantic change.
    [Fact]
    public async Task CommentAndFormattingOnlyCommit_ReportsNoSemanticChange()
    {
        await Commit("""
            namespace Demo;

            // A calculator.
            public class Calc
            {
                /// <summary>Adds.</summary>
                public int Add(int a,  int b)  => a + b;

                public int Mul(int a, int b) => a * b;   // multiply
            }
            """, "reformat + comments");

        var result = await _diff.CompareAsync("HEAD~1", "HEAD");

        Assert.Empty(result.Changed);
        Assert.Empty(result.Added);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public async Task BodyChange_IsNonBreaking()
    {
        await Commit(Original.Replace("=> a + b;", "=> b + a;"), "reorder addition");

        var result = await _diff.CompareAsync("HEAD~1", "HEAD");

        var change = Assert.Single(result.Changed);
        Assert.Equal(["body"], change.LayersChanged);
        Assert.Equal("non-breaking", change.ApiImpact);
    }

    [Fact]
    public async Task PublicSignatureChange_IsBreakingPublic()
    {
        await Commit(Original.Replace("public int Add(int a, int b) => a + b;",
                                      "public int Add(int a, int b, int c) => a + b + c;"), "add operand");

        var result = await _diff.CompareAsync("HEAD~1", "HEAD");

        // Arity changes present as a removal plus an addition of the same name.
        Assert.Contains(result.Added, a => a.Contains("Add"));
        Assert.Contains(result.Removed, r => r.Contains("Add"));
    }

    [Fact]
    public async Task AddedAndRemovedMembersAreReported()
    {
        await Commit("""
            namespace Demo;

            public class Calc
            {
                public int Add(int a, int b) => a + b;
                public int Sub(int a, int b) => a - b;
            }
            """, "swap Mul for Sub");

        var result = await _diff.CompareAsync("HEAD~1", "HEAD");

        Assert.Contains(result.Added, a => a.Contains("Sub"));
        Assert.Contains(result.Removed, r => r.Contains("Mul"));
    }

    private async Task Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdout, stderr);
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {await stderr}");
    }
}
