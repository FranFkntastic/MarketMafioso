using MarketMafioso.Squire.Observation;
using Franthropy.Dalamud.Automation.Inventory;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireUiAutomationTests
{
    [Fact]
    public void FindDesynthesizeEntry_FindsExactEntryCaseInsensitively()
    {
        var option = new DalamudContextMenuOptionSpec("Desynthesis", new HashSet<string> { "Desynthesis", "Desynthesize" });
        Assert.Equal(1, DalamudContextMenuOptionParser.Find(["Try On", "DESYNTHESIZE", "Discard"], option).Index);
        Assert.Equal(1, DalamudContextMenuOptionParser.Find(["Try On", "DESYNTHESIS", "Discard"], option).Index);
    }

    [Fact]
    public void FindDesynthesizeEntry_DoesNotAcceptSimilarDestructiveLabels()
    {
        var option = new DalamudContextMenuOptionSpec("Desynthesis", new HashSet<string> { "Desynthesis", "Desynthesize" });
        Assert.False(DalamudContextMenuOptionParser.Find(["Discard", "Search for Item"], option).Success);
    }
}
