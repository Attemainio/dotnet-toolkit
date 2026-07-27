using DotnetToolkit.McpServer.Validation;

using Microsoft.CodeAnalysis.Text;

using Xunit;

namespace DotnetToolkit.McpServer.Tests;

public sealed class PatchDraftStoreTests
{
    [Fact]
    public void Put_AssignsAnIdAndADeadline()
    {
        var clock = new MutableClock();
        var store = new PatchDraftStore(clock);

        var stored = store.Put(Draft());

        Assert.StartsWith("draft_", stored.Id);
        Assert.Equal(clock.Now + PatchDraftStore.Lifetime, stored.ExpiresAt);
    }

    [Fact]
    public void Get_ReturnsTheStoredDraftIncludingItsBaseVersions()
    {
        var store = new PatchDraftStore(new MutableClock());
        var stored = store.Put(Draft(("sym_abc", "decl:1234")));

        var found = store.Get(stored.Id);

        var draft = Assert.IsType<PatchDraft>(found);
        Assert.Equal("decl:1234", draft.BaseVersions["sym_abc"]);
    }

    [Fact]
    public void Get_AfterTheLifetimeElapses_ReturnsNull()
    {
        var clock = new MutableClock();
        var store = new PatchDraftStore(clock);
        var stored = store.Put(Draft());

        clock.Now += PatchDraftStore.Lifetime + TimeSpan.FromSeconds(1);

        Assert.Null(store.Get(stored.Id));
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull()
    {
        var store = new PatchDraftStore(new MutableClock());

        Assert.Null(store.Get("draft_nothing"));
    }

    [Fact]
    public void Put_BeyondCapacity_EvictsTheOldestDraft()
    {
        var clock = new MutableClock();
        var store = new PatchDraftStore(clock);

        // One second apart so the eviction order is unambiguous rather than a tie between equal deadlines.
        var stored = new List<PatchDraft>();
        for (var i = 0; i <= PatchDraftStore.Capacity; i++)
        {
            stored.Add(store.Put(Draft()));
            clock.Now += TimeSpan.FromSeconds(1);
        }

        Assert.Null(store.Get(stored[0].Id));
        foreach (var draft in stored.Skip(1))
        {
            Assert.NotNull(store.Get(draft.Id));
        }
    }

    [Fact]
    public void Remove_ForgetsTheDraft()
    {
        var store = new PatchDraftStore(new MutableClock());
        var stored = store.Put(Draft());

        store.Remove(stored.Id);

        Assert.Null(store.Get(stored.Id));
    }

    private static PatchDraft Draft(params (string SymbolId, string Version)[] baseVersions) =>
        new(baseVersions.ToDictionary(v => v.SymbolId, v => v.Version, StringComparer.Ordinal),
            new Dictionary<string, SourceText>(StringComparer.Ordinal),
            new Dictionary<string, SourceText>(StringComparer.Ordinal));

    /// <summary>A hand-advanced clock, so expiry and eviction are exercised without a real wait.</summary>
    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
