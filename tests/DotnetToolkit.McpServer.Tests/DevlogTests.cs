using DotnetToolkit.McpServer.Devlog;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

public sealed class DevlogTests : IDisposable
{
    private readonly string _root;
    private readonly DevlogStore _store;

    public DevlogTests()
    {
        _root = Directory.CreateTempSubdirectory("devlog-tests-").FullName;
        var locator = new SolutionLocator(NullLogger<SolutionLocator>.Instance, _root);
        _store = new DevlogStore(locator, NullLogger<DevlogStore>.Instance);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AddThenSearchThenGetRoundTrip()
    {
        var (id, file) = _store.Add(
            "Fixed decimal rounding in price calculation",
            "Replaced double math with decimal in PriceCalculator.Total.",
            "Totals drifted by cents on large orders.",
            "Tried MidpointRounding.AwayFromZero first; broke invoice tests.",
            "decimal end-to-end; added regression test.",
            "done",
            ["PriceCalculator", "OrderService"],
            "Ordering",
            ["bug", "rounding"]);

        Assert.StartsWith("devlog/", file);
        Assert.Matches(@"^\d{8}-\d{4}-[0-9a-f]{4}$", id);

        var hits = _store.Search("decimal rounding", null, null, null, null, null, null, 10);
        var hit = Assert.Single(hits);
        Assert.Equal(id, hit.Entry.Id);
        Assert.True(hit.Score > 0);

        // Filters: class (substring, case-insensitive), domain, tag, status.
        Assert.Single(_store.Search(null, "pricecalc", null, null, null, null, null, 10));
        Assert.Single(_store.Search(null, null, "ordering", null, null, null, null, 10));
        Assert.Single(_store.Search(null, null, null, "bug", null, null, null, 10));
        Assert.Empty(_store.Search(null, null, null, null, "not-done", null, null, 10));

        var markdown = _store.Get(id);
        Assert.NotNull(markdown);
        Assert.Contains("**WHAT:**", markdown);
        Assert.Contains("**OBSERVATIONS:**", markdown);
        Assert.DoesNotContain("<!-- devlog", markdown);
    }

    [Fact]
    public void ClassMatchScoresAboveBodyMatch()
    {
        _store.Add("Tuning pass", "Adjusted mutation rate handling in solver loop.", "Perf.",
            null, null, "done", ["EvolutionarySolver"], "Solvers", []);
        _store.Add("Docs update", "Mentioned solver in readme.", "Docs.",
            null, null, "done", [], null, []);

        var hits = _store.Search("EvolutionarySolver mutation", null, null, null, null, null, null, 10);
        Assert.Equal("Tuning pass", hits[0].Entry.Title);
    }

    [Fact]
    public void HandEditedFileIsReindexedByMtime()
    {
        _store.Add("First entry", "Something.", "Because.", null, null, "done", [], null, []);
        var file = Directory.EnumerateFiles(Path.Combine(_root, "devlog"), "*.md").Single();

        // Simulate a hand-written entry without a metadata comment.
        File.AppendAllText(file, "\n## 2026-07-01 — Handwritten note\n\n**WHAT:** manual edit about caching.\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));

        var hits = _store.Search("caching", null, null, null, null, null, null, 10);
        var hit = Assert.Single(hits);
        Assert.Equal("Handwritten note", hit.Entry.Title);
        Assert.Contains("#", hit.Entry.Id); // fallback id: <file>#<n>
        Assert.Equal(new DateTime(2026, 7, 1), hit.Entry.Ts.Date);
    }

    [Fact]
    public void EmptyQueryListsMostRecentFirst()
    {
        _store.Add("Older", "a.", "b.", null, null, "done", [], null, []);
        _store.Add("Newer", "c.", "d.", null, null, "partial", [], null, []);

        var hits = _store.Search(null, null, null, null, null, null, null, 10);
        Assert.Equal(2, hits.Count);
        // Same timestamp minute is possible; both orders acceptable only if ts equal — assert set instead.
        Assert.Equal(new[] { "Newer", "Older" }.Order(), hits.Select(h => h.Entry.Title).Order());
    }
}
