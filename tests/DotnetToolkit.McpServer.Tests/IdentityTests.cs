using DotnetToolkit.McpServer.Identity;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

public sealed class IdentityTests
{
    [Fact]
    public void UlidIs26CrockfordChars()
    {
        var ulid = Ulid.NewString();
        Assert.Equal(26, ulid.Length);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{26}$", ulid);
    }

    [Fact]
    public void UlidsAreUniqueAndTimeOrdered()
    {
        var first = Ulid.NewString();
        Thread.Sleep(2);
        var second = Ulid.NewString();
        Assert.NotEqual(first, second);
        // The 48-bit timestamp prefix means later ULIDs sort lexicographically after earlier ones.
        Assert.True(string.CompareOrdinal(first, second) < 0);
    }

    [Fact]
    public void SymbolIdIsDeterministicAndPrefixed()
    {
        var a = Ids.SymbolId("PandaAI.Core.Training.TrainingService.StartTrainingAsync", "PandaAI.Core");
        var b = Ids.SymbolId("PandaAI.Core.Training.TrainingService.StartTrainingAsync", "PandaAI.Core");
        Assert.Equal(a, b);
        Assert.Matches("^sym_[0-9a-f]{16}$", a);
    }

    [Fact]
    public void SymbolIdChangesWithNameOrAssembly()
    {
        var baseline = Ids.SymbolId("Ns.Type.Method", "Asm");
        Assert.NotEqual(baseline, Ids.SymbolId("Ns.Type.Renamed", "Asm"));
        Assert.NotEqual(baseline, Ids.SymbolId("Ns.Type.Method", "OtherAsm"));
    }
}
