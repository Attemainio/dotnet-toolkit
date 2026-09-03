using DotnetToolkit.McpServer.Workspace;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// SDK selection against a repository's <c>global.json</c>, which is what stops the server loading a
/// workspace with MSBuild from a feature band the repository never builds with.
/// </summary>
/// <remarks>
/// The bug these cover, concretely: a machine with 10.0.111 and 10.0.301 installed, a repository
/// pinned to the 10.0.1xx band, and a server that registered 10.0.301 because it was the highest
/// number on the machine. The developer's <c>dotnet build</c> and the server's restore then wrote the
/// same NuGet assets cache from two different SDKs, and whichever ran second reported the workspace
/// degraded — with nothing in the message naming an SDK.
/// </remarks>
public sealed class GlobalJsonPolicyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("globaljson-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string json, string? subdirectory = null)
    {
        var directory = subdirectory is null ? _root : Path.Combine(_root, subdirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "global.json"), json);
        return directory;
    }

    private static readonly Version[] Installed =
        [new(8, 0, 130), new(10, 0, 111), new(10, 0, 203), new(10, 0, 301)];

    // ---- reading ---------------------------------------------------------------------------------

    [Fact]
    public void NoGlobalJsonIsNoConstraint()
    {
        // The common case, and it must stay the previous behaviour: most repositories have none, and
        // for those the newest MSBuild is right, because a newer MSBuild opens an older project and
        // the reverse is not true.
        Assert.Null(GlobalJsonPolicy.Read(_root));
    }

    [Fact]
    public void AGlobalJsonWithNoSdkVersionIsNoConstraint()
    {
        Write("""{ "test": { "runner": "Microsoft.Testing.Platform" } }""");

        Assert.Null(GlobalJsonPolicy.Read(_root));
    }

    [Fact]
    public void AMalformedGlobalJsonIsNoConstraintRatherThanAFailure()
    {
        // The repository's own build reports it far more clearly than this server could, and throwing
        // here would take down a workspace load over a file the server does not own.
        Write("{ this is not json");

        Assert.Null(GlobalJsonPolicy.Read(_root));
    }

    [Fact]
    public void TheNearestGlobalJsonUpwardWins()
    {
        // How the SDK host finds it: one beside the solution and one two directories above are both
        // in force, nearest winning.
        Write("""{ "sdk": { "version": "10.0.100" } }""");
        var nested = Write("""{ "sdk": { "version": "10.0.300" } }""", Path.Combine("src", "app"));

        Assert.Equal(new Version(10, 0, 300), GlobalJsonPolicy.Read(nested)!.Requested);
    }

    [Fact]
    public void AGlobalJsonAboveTheRootStillGoverns()
    {
        Write("""{ "sdk": { "version": "10.0.100" } }""");
        var nested = Path.Combine(_root, "src", "app");
        Directory.CreateDirectory(nested);

        Assert.Equal(new Version(10, 0, 100), GlobalJsonPolicy.Read(nested)!.Requested);
    }

    [Fact]
    public void AnAbsentRollForwardMeansLatestPatch()
    {
        // The SDK host's default, and getting it wrong would widen every unannotated pin in existence.
        Write("""{ "sdk": { "version": "10.0.100" } }""");

        Assert.Equal(RollForward.LatestPatch, GlobalJsonPolicy.Read(_root)!.Mode);
    }

    [Theory]
    [InlineData("disable", nameof(RollForward.Disable))]
    [InlineData("patch", nameof(RollForward.Patch))]
    [InlineData("feature", nameof(RollForward.Feature))]
    [InlineData("minor", nameof(RollForward.Minor))]
    [InlineData("major", nameof(RollForward.Major))]
    [InlineData("latestPatch", nameof(RollForward.LatestPatch))]
    [InlineData("latestFeature", nameof(RollForward.LatestFeature))]
    [InlineData("latestMinor", nameof(RollForward.LatestMinor))]
    [InlineData("latestMajor", nameof(RollForward.LatestMajor))]
    [InlineData("nonsense", nameof(RollForward.LatestPatch))]
    public void EveryRollForwardSpellingParses(string written, string expected)
    {
        // Compared by name rather than by value: the enum is internal, and a public test signature
        // naming it would not compile.
        Write($$"""{ "sdk": { "version": "10.0.100", "rollForward": "{{written}}" } }""");

        Assert.Equal(expected, GlobalJsonPolicy.Read(_root)!.Mode.ToString());
    }

    // ---- selection -------------------------------------------------------------------------------

    [Fact]
    public void LatestPatchStaysInsideItsFeatureBand()
    {
        // The case that started this. 10.0.301 is a higher number and a different servicing line, and
        // a 10.0.1xx pin must not reach it however new it is.
        var policy = new GlobalJsonPolicy(new Version(10, 0, 100), RollForward.LatestPatch, "global.json");

        Assert.Equal(new Version(10, 0, 111), policy.Select(Installed));
    }

    [Fact]
    public void LatestFeatureReachesAcrossBandsAndTakesTheHighest()
    {
        // FluidScript's own pin. Both 10.0.111 and 10.0.301 qualify, so either SDK can load it -- and
        // that is why the symptom was intermittent rather than constant.
        var policy = new GlobalJsonPolicy(new Version(10, 0, 100), RollForward.LatestFeature, "global.json");

        Assert.Equal(new Version(10, 0, 301), policy.Select(Installed));
    }

    [Fact]
    public void FeatureTakesTheNearestQualifyingBandRatherThanTheHighest()
    {
        // Half the modes want the lowest qualifying SDK and half the highest. Collapsing them to
        // "highest allowed" is wrong in a way nothing would notice for months.
        var policy = new GlobalJsonPolicy(new Version(10, 0, 150), RollForward.Feature, "global.json");

        Assert.Equal(new Version(10, 0, 203), policy.Select(Installed));
    }

    [Fact]
    public void PatchTakesTheLowestQualifyingPatchInItsBand()
    {
        var policy = new GlobalJsonPolicy(new Version(10, 0, 200), RollForward.Patch, "global.json");

        Assert.Equal(new Version(10, 0, 203), policy.Select(Installed));
    }

    [Fact]
    public void DisableAcceptsOnlyTheExactVersion()
    {
        Assert.Equal(
            new Version(10, 0, 203),
            new GlobalJsonPolicy(new Version(10, 0, 203), RollForward.Disable, "g").Select(Installed));

        Assert.Null(
            new GlobalJsonPolicy(new Version(10, 0, 200), RollForward.Disable, "g").Select(Installed));
    }

    [Fact]
    public void RollingForwardNeverRollsBackward()
    {
        // `latestMajor` licenses anything newer, never anything older: an 8.0 SDK must not satisfy a
        // 10.0 pin however wide the scope.
        var policy = new GlobalJsonPolicy(new Version(10, 0, 100), RollForward.LatestMajor, "global.json");

        Assert.False(policy.Allows(new Version(8, 0, 130)));
        Assert.Equal(new Version(10, 0, 301), policy.Select(Installed));
    }

    [Fact]
    public void NothingInstalledSatisfiesAPinToAFutureBand()
    {
        // Not an error here -- the caller loads with the newest available and says so, because a
        // workspace that loads with a warning beats one that silently degrades.
        var policy = new GlobalJsonPolicy(new Version(10, 0, 400), RollForward.LatestPatch, "global.json");

        Assert.Null(policy.Select(Installed));
    }

    [Fact]
    public void MinorStaysInsideItsMajor()
    {
        var policy = new GlobalJsonPolicy(new Version(10, 0, 100), RollForward.LatestMinor, "global.json");

        Assert.True(policy.Allows(new Version(10, 5, 100)));
        Assert.False(policy.Allows(new Version(11, 0, 100)));
    }

    [Fact]
    public void TheRealFluidScriptPinResolvesOnATwoBandMachine()
    {
        // End to end through Read, with the exact file that provoked this: `latestFeature` on
        // 10.0.100 is satisfied by both installed bands, which is why the repository builds fine on
        // 10.0.111 and why pinning developers to 10.0.301 was never the fix.
        Write("""
            {
              "sdk": { "version": "10.0.100", "rollForward": "latestFeature" },
              "test": { "runner": "Microsoft.Testing.Platform" }
            }
            """);

        var policy = GlobalJsonPolicy.Read(_root)!;

        Assert.True(policy.Allows(new Version(10, 0, 111)));
        Assert.True(policy.Allows(new Version(10, 0, 301)));
    }
}
