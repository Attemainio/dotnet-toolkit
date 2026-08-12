using DotnetToolkit.McpServer.Tools;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Covers the acknowledgement rule on its own, without a workspace: whether a caller gets past the
/// large-source guard is the whole of the guard's contract, and it is the part that would fail
/// silently — a guard that never lets anyone through and one that never stops anyone both look like
/// working code from the outside.
/// </summary>
public sealed class ResponseGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    public ResponseGuardTests() => ResponseGuard.Reset();

    [Fact]
    public void WarnsOnTheFirstRequest()
    {
        Assert.True(ResponseGuard.ShouldWarn("sym_first", "source:full", Now));
    }

    /// <summary>Re-sending the same call is the consent, so the repeat is served rather than warned.</summary>
    [Fact]
    public void DoesNotWarnOnAnIdenticalRepeat()
    {
        ResponseGuard.ShouldWarn("sym_repeat", "source:full", Now);

        Assert.False(ResponseGuard.ShouldWarn("sym_repeat", "source:full", Now));
        Assert.False(ResponseGuard.ShouldWarn("sym_repeat", "source:full", Now.AddMinutes(5)));
    }

    /// <summary>
    /// A different include is a different question — acknowledging the whole source does not
    /// acknowledge a later fetch that asks for something else and happens to be large too.
    /// </summary>
    [Fact]
    public void WarnsSeparatelyPerInclude()
    {
        ResponseGuard.ShouldWarn("sym_include", "source:full", Now);

        Assert.True(ResponseGuard.ShouldWarn("sym_include", "all", Now));
        Assert.True(ResponseGuard.ShouldWarn("sym_include", null, Now));
    }

    /// <summary>Acknowledgements expire, since a declaration may be a different size by then.</summary>
    [Fact]
    public void WarnsAgainOnceTheAcknowledgementHasExpired()
    {
        ResponseGuard.ShouldWarn("sym_expiry", "source:full", Now);

        Assert.True(ResponseGuard.ShouldWarn("sym_expiry", "source:full", Now.AddHours(1)));
    }

    /// <summary>
    /// A repeat refreshes the acknowledgement rather than letting it age out mid-task: a caller
    /// reading the same large symbol throughout one piece of work consented once.
    /// </summary>
    [Fact]
    public void ARepeatRefreshesTheAcknowledgement()
    {
        ResponseGuard.ShouldWarn("sym_refresh", "source:full", Now);
        ResponseGuard.ShouldWarn("sym_refresh", "source:full", Now.AddMinutes(10));

        Assert.False(ResponseGuard.ShouldWarn("sym_refresh", "source:full", Now.AddMinutes(20)));
    }

    /// <summary>Symbols are tracked independently — one large fetch does not wave through the next.</summary>
    [Fact]
    public void WarnsSeparatelyPerSymbol()
    {
        ResponseGuard.ShouldWarn("sym_one", "source:full", Now);

        Assert.True(ResponseGuard.ShouldWarn("sym_two", "source:full", Now));
    }

    /// <summary>
    /// The count sums the spans handed in, so a partial's parts add up, and a symbol with no editable
    /// declaration at all measures zero rather than throwing. Spans are what <c>declarationSites</c>
    /// reports, which is what keeps the advice's quoted size agreeing with the sites beside it.
    /// </summary>
    [Fact]
    public void DeclaredLineCountSumsEverySpanItIsGiven()
    {
        Assert.Equal(0, ResponseGuard.DeclaredLineCount([]));
        Assert.Equal(1, ResponseGuard.DeclaredLineCount([("A.cs", 7, 7, 7)]));
        Assert.Equal(531, ResponseGuard.DeclaredLineCount([("A.cs", 8, 534, 9), ("B.cs", 1, 4, 1)]));
    }
}
