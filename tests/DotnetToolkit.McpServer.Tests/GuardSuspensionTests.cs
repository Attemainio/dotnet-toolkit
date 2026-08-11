using DotnetToolkit.McpServer.Hooks;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Covers the guard off-switch, whose failure mode is silence: a suspension that outlives its purpose
/// leaves the plugin installed and inert, so every test here is really about it coming back on.
/// </summary>
public sealed class GuardSuspensionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "dt-guards-" + Guid.NewGuid().ToString("N")[..8]);

    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Current_WithNoStateFile_ReportsGuardsActive()
    {
        var state = GuardSuspension.Current(_root, Now);

        Assert.False(state.Suspended);
        Assert.Equal("active", state.Source);
        Assert.Null(state.Until);
    }

    [Fact]
    public void Suspend_ThenCurrent_ReportsTheRemainingWindow()
    {
        var until = GuardSuspension.Suspend(_root, TimeSpan.FromMinutes(10), Now);

        var state = GuardSuspension.Current(_root, Now.AddMinutes(9));

        Assert.True(state.Suspended);
        Assert.Equal("timed", state.Source);
        Assert.Equal(Now.AddMinutes(10), until);
        Assert.Equal(until, state.Until);
    }

    /// <summary>The property the whole design turns on: nothing has to remember to re-arm the guards.</summary>
    [Fact]
    public void Current_PastTheExpiry_ReArmsAndDeletesTheStateFile()
    {
        GuardSuspension.Suspend(_root, TimeSpan.FromMinutes(10), Now);

        var state = GuardSuspension.Current(_root, Now.AddMinutes(11));

        Assert.False(state.Suspended);
        Assert.Equal("active", state.Source);
        Assert.False(File.Exists(GuardSuspension.StateFile(_root)));
    }

    /// <summary>An unreadable expiry must not read as "suspended forever" — the safe direction is guarded.</summary>
    [Fact]
    public void Current_WithUnparseableContent_ReArmsAndDeletesTheStateFile()
    {
        var file = GuardSuspension.StateFile(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "whenever");

        var state = GuardSuspension.Current(_root, Now);

        Assert.False(state.Suspended);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Suspend_BeyondTheCap_IsClampedToMaxDuration()
    {
        var until = GuardSuspension.Suspend(_root, TimeSpan.FromDays(7), Now);

        Assert.Equal(Now + GuardSuspension.MaxDuration, until);
    }

    [Fact]
    public void Suspend_WithANonPositiveDuration_UsesTheDefaultRatherThanExpiringAtOnce()
    {
        var until = GuardSuspension.Suspend(_root, TimeSpan.Zero, Now);

        Assert.Equal(Now + GuardSuspension.DefaultDuration, until);
    }

    [Fact]
    public void Resume_ClearsAnActiveSuspension()
    {
        GuardSuspension.Suspend(_root, TimeSpan.FromHours(1), Now);

        var cleared = GuardSuspension.Resume(_root);

        Assert.True(cleared);
        Assert.False(GuardSuspension.Current(_root, Now).Suspended);
    }

    [Fact]
    public void Environment_OverridesTheStateFile_AndResumeReportsItCannotClearIt()
    {
        var previous = Environment.GetEnvironmentVariable(GuardSuspension.DisableVariable);
        try
        {
            Environment.SetEnvironmentVariable(GuardSuspension.DisableVariable, "1");

            var state = GuardSuspension.Current(_root, Now);

            Assert.True(state.Suspended);
            Assert.Equal("environment", state.Source);
            Assert.Null(state.Until);
            Assert.False(GuardSuspension.Resume(_root));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GuardSuspension.DisableVariable, previous);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("")]
    public void Environment_SetToAnOffValue_LeavesTheGuardsActive(string value)
    {
        var previous = Environment.GetEnvironmentVariable(GuardSuspension.DisableVariable);
        try
        {
            Environment.SetEnvironmentVariable(GuardSuspension.DisableVariable, value);

            Assert.False(GuardSuspension.Current(_root, Now).Suspended);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GuardSuspension.DisableVariable, previous);
        }
    }
}
