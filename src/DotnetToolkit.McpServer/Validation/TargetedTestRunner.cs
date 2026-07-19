using System.Diagnostics;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Extensions.Logging;

namespace DotnetToolkit.McpServer.Validation;

/// <summary>
/// Executes ladder level 5 (<c>targeted_tests</c>): runs only the tests that semantically reference the
/// changed symbols, resolved from <c>test_reference</c> edges rather than by running the whole suite.
/// </summary>
public sealed class TargetedTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(5);

    private readonly SolutionLocator _locator;
    private readonly ILogger<TargetedTests> _log;

    public TargetedTests(SolutionLocator locator, ILogger<TargetedTests> log)
    {
        _locator = locator;
        _log = log;
    }

    /// <summary>
    /// Runs the named tests. Returns null when they pass, or the failure output when they don't.
    /// An empty test set is a pass: nothing references the change, so there is nothing to prove.
    /// </summary>
    public async Task<string?> RunAsync(IReadOnlyList<string> testFqNames, CancellationToken cancellationToken)
    {
        if (testFqNames.Count == 0)
            return null;

        // vstest's filter is an OR of fully-qualified name matches; cap the expression so a very wide
        // change does not build an unusable command line.
        var selected = testFqNames.Distinct(StringComparer.Ordinal).Take(50).ToList();
        var filter = string.Join('|', selected.Select(n => $"FullyQualifiedName~{n}"));

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _locator.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("--filter");
        psi.ArgumentList.Add(filter);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return "targeted tests could not be started";

            // Drain both pipes concurrently — reading one to completion first deadlocks as soon as the
            // child fills the other pipe's buffer.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RunTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return $"targeted tests exceeded {RunTimeout.TotalMinutes:F0} minutes and were cancelled";
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode == 0)
                return null;

            var output = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
            return Summarize(output);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Targeted test run failed to execute");
            return $"targeted tests could not be run: {ex.Message}";
        }
    }

    /// <summary>Keeps only the failure-bearing lines — a full vstest log is mostly noise.</summary>
    private static string Summarize(string output)
    {
        var lines = output.Split('\n')
            .Where(l => l.Contains("Failed", StringComparison.Ordinal)
                        || l.Contains("error", StringComparison.OrdinalIgnoreCase)
                        || l.Contains("Assert", StringComparison.Ordinal))
            .Select(l => l.TrimEnd())
            .Take(20)
            .ToList();
        return lines.Count > 0 ? string.Join('\n', lines) : "targeted tests failed (no failure lines captured)";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort: the run is already being reported as a failure.
        }
    }
}
