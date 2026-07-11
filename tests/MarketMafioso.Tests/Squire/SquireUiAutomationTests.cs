using MarketMafioso.Squire.Observation;
using Franthropy.Dalamud.Automation.Inventory;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireUiAutomationTests
{
    [Fact]
    public void FindDesynthesizeEntry_FindsExactEntryCaseInsensitively()
    {
        Assert.Equal(1, DalamudDesynthesisUiTransaction.FindDesynthesisEntry(["Try On", "DESYNTHESIZE", "Discard"]));
        Assert.Equal(1, DalamudDesynthesisUiTransaction.FindDesynthesisEntry(["Try On", "DESYNTHESIS", "Discard"]));
    }

    [Fact]
    public void FindDesynthesizeEntry_DoesNotAcceptSimilarDestructiveLabels()
    {
        Assert.Equal(-1, DalamudDesynthesisUiTransaction.FindDesynthesisEntry(["Discard", "Search for Item"]));
    }
}
