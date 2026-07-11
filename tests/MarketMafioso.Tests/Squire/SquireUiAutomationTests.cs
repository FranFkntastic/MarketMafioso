using MarketMafioso.Squire.Observation;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireUiAutomationTests
{
    [Fact]
    public void FindDesynthesizeEntry_FindsExactEntryCaseInsensitively()
    {
        Assert.Equal(1, DalamudSquireActionGameAdapter.FindDesynthesizeEntry(["Try On", "DESYNTHESIZE", "Discard"]));
    }

    [Fact]
    public void FindDesynthesizeEntry_DoesNotAcceptSimilarDestructiveLabels()
    {
        Assert.Equal(-1, DalamudSquireActionGameAdapter.FindDesynthesizeEntry(["Discard", "Search for Item"]));
    }
}
